#!/usr/bin/env bash
# R5-12: disposable Compose stack, real k6 through Nginx, API stop/rejoin, one/three comparison and DB budgets.
# Linux/WSL; Docker, .NET 10, jq and curl. Never operates on the developer's Compose project or its data.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
for tool in docker dotnet jq curl; do command -v "$tool" >/dev/null || { echo "$tool is required" >&2; exit 1; }; done
[[ -f warehouse/Dockerfile ]] || { echo 'Initialize the warehouse: git submodule update --init' >&2; exit 1; }
PROJECT="shop-r5-proof-$RANDOM"
WORK=$(mktemp -d "${TMPDIR:-/tmp}/$PROJECT.XXXXXX")
RESULTS=${RESULTS_DIR:-"$PWD/artifacts/requirement-5"}
mkdir -p "$RESULTS"
RESULTS=$(cd "$RESULTS" && pwd -P)
export COMPOSE_PROJECT_NAME=$PROJECT COMPOSE_FILE="$PWD/compose.yaml:$WORK/override.yaml"
export COMPOSE_PROFILES=replica SHOP_NETWORK_SUBNET=172.30.59.0/24
export JWT_SIGNING_KEY="$(head -c 32 /dev/urandom | base64)"
# Share fresh disposable credentials between the object store and its clients (R5-07).
export S3_ACCESS_KEY=proof S3_BUCKET=shop-images
export S3_SECRET_KEY="$(head -c 32 /dev/urandom | base64)"
export API_PORT=0 POSTGRES_PORT=0 REPLICA_PORT=0 REDIS_PORT=0 RABBITMQ_PORT=0 RABBITMQ_UI_PORT=0
export MAILPIT_UI_PORT=0 MAILPIT_SMTP_PORT=0 EMAIL_PROVIDER=Mailpit
export ADMIN_EMAIL=admin@shop.local ADMIN_PASSWORD='Admin123!' DEMO_SEED_ENABLED=true
export ASPNETCORE_ENVIRONMENT=Production READ_ROUTING_ENABLED=true CATALOG_CACHE_ENABLED=true
export POSTGRES_DB=shop POSTGRES_USER=shop POSTGRES_PASSWORD=shop REPLICATION_PASSWORD=replicator
export POSTGRES_MAX_CONNECTIONS=100 API_DB_POOL_SIZE=15 API_READ_DB_POOL_SIZE=10 WORKER_DB_POOL_SIZE=5 MIGRATOR_DB_POOL_SIZE=5
export INVENTORY_DELAY_MS=0 REPLICA_APPLY_DELAY=0 RABBITMQ_USER=shop RABBITMQ_PASSWORD=shop
export COMPOSE_ENV_FILES="$WORK/empty.env"
touch "$COMPOSE_ENV_FILES"
dc() { docker compose --env-file "$COMPOSE_ENV_FILES" "$@"; }
fail() { echo "FAILED: $*" >&2; exit 1; }
step() { printf '\n== %s\n' "$*"; }
wait_for() {
  local deadline=$((SECONDS + $1)); shift
  until "$@"; do ((SECONDS < deadline)) || fail "timed out: $*"; sleep 0.5; done
}
cleanup() {
  local result=$?
  [[ -z ${sampler:-} ]] || { kill "$sampler" 2>/dev/null || true; wait "$sampler" 2>/dev/null || true; }
  dc logs --no-color >"$RESULTS/compose.log" 2>&1 || true
  dc ps -a >"$RESULTS/containers.txt" 2>&1 || true
  docker rm -f "$PROJECT-k6" >/dev/null 2>&1 || true
  dc down --volumes --remove-orphans >/dev/null 2>&1 || true
  docker image rm "$PROJECT-api" "$PROJECT-worker" "$PROJECT-inventory" >/dev/null 2>&1 || true
  echo "Proof artifacts: $RESULTS; scratch files: $WORK"
  exit "$result"
}
trap cleanup EXIT
cat >"$WORK/override.yaml" <<EOF
services:
  migrator: { image: $PROJECT-api }
  api1: { image: $PROJECT-api }
  api2: { image: $PROJECT-api }
  api3: { image: $PROJECT-api }
  worker: { image: $PROJECT-worker }
  inventory: { image: $PROJECT-inventory }
EOF

step 'Validate the isolated Compose configuration'
dc config --quiet || fail 'invalid proof Compose configuration'

step 'Build the current source and prove three production worker registrations (R5-11)'
dotnet test tests/Shop.Modules.Notifications.IntegrationTests -m:1 -nr:false \
  --filter 'FullyQualifiedName~ThreeProductionWorkersShareThirtyEmails|FullyQualifiedName~LosingOneWorkerDuringASend' \
  --logger 'trx;LogFileName=workers.trx' --results-directory "$RESULTS" >"$RESULTS/workers.txt" 2>&1 ||
  { cat "$RESULTS/workers.txt" >&2; fail 'worker proof failed'; }
for host in Api Worker; do
  dotnet publish "src/Shop.$host" -c Release -o "$WORK/$host" -p:UseAppHost=false -nologo -v q \
    >"$RESULTS/publish-$host.txt" 2>&1 || { cat "$RESULTS/publish-$host.txt" >&2; fail 'publish failed'; }
  printf '%s\n' 'FROM mcr.microsoft.com/dotnet/aspnet:10.0' 'WORKDIR /app' \
    'RUN mkdir -p /data/images && chown $APP_UID:$APP_UID /data/images' 'COPY . .' 'USER $APP_UID' \
    "ENTRYPOINT [\"dotnet\", \"Shop.$host.dll\"]" |
    docker build -q -t "$PROJECT-${host,,}" -f - "$WORK/$host" >/dev/null
done
dc build inventory >"$RESULTS/build-inventory.txt" 2>&1 || { cat "$RESULTS/build-inventory.txt" >&2; fail 'warehouse build failed'; }
dc up -d --no-build --wait --wait-timeout 180 >"$RESULTS/startup.txt" 2>&1 || { cat "$RESULTS/startup.txt" >&2; fail 'stack startup failed'; }
API="http://127.0.0.1:$(dc port nginx 80 | head -n 1 | sed 's/.*://')"
network=$(docker inspect -f '{{range $name, $details := .NetworkSettings.Networks}}{{$name}}{{end}}' "$(dc ps -q nginx)")
proxy_id=$(dc ps -q nginx)
api2_id=$(dc ps -q api2)
api3_id=$(dc ps -q api3)
api2_address=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$api2_id")
curl -fsS "$API/health" >/dev/null

step 'Budget and actual max_connections on primary AND replica (R5-09)'
tests/proof/requirement-5/connection-budget.sh >"$RESULTS/connection-budget.json"
cat "$RESULTS/connection-budget.json"
git show r5-before-connection-budget:compose.yaml >"$WORK/before.yaml"
docker compose --project-directory "$PWD" --env-file "$COMPOSE_ENV_FILES" -f "$WORK/before.yaml" --profile replica \
  config --format json >"$WORK/before.json"
if tests/proof/requirement-5/connection-budget.sh --config-only "$WORK/before.json" >"$RESULTS/connection-budget-before.txt" 2>&1; then
  fail 'the unbounded pre-fix pools unexpectedly passed the budget check'
fi
# Negative control: a 100-connection API pool must fail the same validator.
dc config --format json | jq '.services.api1.environment.ConnectionStrings__Shop |=
  gsub("Maximum Pool Size=[0-9]+"; "Maximum Pool Size=100")' >"$WORK/oversized.json"
if tests/proof/requirement-5/connection-budget.sh --config-only "$WORK/oversized.json" >"$RESULTS/over-budget.txt" 2>&1; then
  fail 'oversized pools were accepted'
fi

sample() {
  while true; do
    for service in postgres db-replica; do
      count=$(dc exec -T "$service" sh -c 'psql -X -q -At -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "SELECT count(*) FROM pg_stat_activity WHERE backend_type = '\''client backend'\''"') || return
      printf '%s %s %s\n' "$(date +%s)" "$service" "$count"
    done
    sleep 1
  done
}
sample >"$RESULTS/connections.txt" & sampler=$!
k6_run() {
  local label=$1 mode=$2 expected=$3 duration=${4:-15s}
  docker run --rm --name "$PROJECT-k6" --network "$network" -u "$(id -u):$(id -g)" \
    -v "$PWD/perf/scalability.js:/scripts/scalability.js:ro" -v "$RESULTS:/results" \
    -e API_URL=http://nginx -e MODE="$mode" -e LABEL="$label" -e EXPECT_INSTANCES="$expected" \
    -e RATE="${RATE:-100}" -e DURATION="$duration" -e COMMIT="$(git rev-parse HEAD)" \
    grafana/k6:2.3.0 run --quiet /scripts/scalability.js >"$RESULTS/$label.txt" 2>&1
}
step 'Distribution: 500 k6 requests, counted by X-Instance'
k6_run distribution distribution api1,api2,api3 || { cat "$RESULTS/distribution.txt" >&2; fail 'distribution failed'; }
jq -e '.total == 500 and .thresholdsPassed' "$RESULTS/distribution.json" >/dev/null || fail 'distribution totals are wrong'
cat "$RESULTS/distribution.json"

step 'Continuity: stop api2 while k6 keeps sending traffic, then prove it rejoins'
k6_run continuity continuity api1,api2,api3 45s & load=$!
started() { [[ -f $RESULTS/continuity.txt ]] && grep -q R5_LOAD_STARTED "$RESULTS/continuity.txt"; }
wait_for 60 started
sleep 5
printf 'stop api2 %s\n' "$(date -u +%FT%TZ)" >"$RESULTS/continuity-events.txt"
dc stop -t 30 api2 >/dev/null
for _ in $(seq 1 30); do
  curl -fsS -D - -o /dev/null "$API/health" | tr -d '\r' | sed -n 's/^[Xx]-[Ii]nstance: //p'
done >"$RESULTS/while-api2-stopped.txt"
! grep -q '^api2$' "$RESULTS/while-api2-stopped.txt" || fail 'a stopped instance answered'
grep -q '^api1$' "$RESULTS/while-api2-stopped.txt" && grep -q '^api3$' "$RESULTS/while-api2-stopped.txt" || fail 'surviving instances did not both answer'
sleep 8
printf 'start api2 %s\n' "$(date -u +%FT%TZ)" >>"$RESULTS/continuity-events.txt"
restart_at=$(date -u +%FT%TZ)
docker start "$api2_id" >/dev/null
listening() { docker logs --since "$restart_at" "$api2_id" 2>&1 | grep 'Now listening on' >/dev/null; }
wait_for 30 listening
ready_at=$(docker logs --since "$restart_at" --timestamps "$api2_id" 2>&1 | awk '/Now listening on/{print $1}' | tail -n 1)
rejoined() {
  # Observe ALL ongoing k6 traffic. A sparse health probe can miss api2 while least_conn sends it other requests.
  first_success=$(docker logs --since "$ready_at" --timestamps "$proxy_id" 2>/dev/null |
    grep -F " 200 upstream=$api2_address:8080 upstream_status=200 " | awk 'NR==1' || true)
  [[ -n $first_success ]]
}
# Allow log collection to finish, but assert the response's own timestamp, not when a polling loop noticed it.
wait_for 15 rejoined
answered_at=${first_success%% *}
rejoin_seconds=$(( $(date -d "$answered_at" +%s) - $(date -d "$ready_at" +%s) ))
printf 'listener %s\nfirst successful api2 response %s\nwhole elapsed seconds %s\n' \
  "$ready_at" "$answered_at" "$rejoin_seconds" >>"$RESULTS/continuity-events.txt"
printf '%s\n' "$first_success" >"$RESULTS/rejoin-evidence.txt"
((rejoin_seconds <= 10)) || fail "api2 took $rejoin_seconds seconds to rejoin"
wait "$load" || { cat "$RESULTS/continuity.txt" >&2; fail 'continuity load failed'; }

step 'Same-rate comparison: one instance, then three, on the SAME demo data'
dc stop api2 api3 >/dev/null
for _ in $(seq 1 10); do curl -s -o /dev/null "$API/health"; done
sleep 11
k6_run one-instance comparison api1 || fail 'one-instance run failed'
docker start "$api2_id" "$api3_id" >/dev/null
sleep 12
k6_run three-instances comparison api1,api2,api3 || fail 'three-instance run failed'
kill "$sampler" 2>/dev/null || true; wait "$sampler" 2>/dev/null || true; sampler=
awk '{if ($3 > peak[$2]) peak[$2]=$3} END {for (server in peak) print server, peak[server]}' \
  "$RESULTS/connections.txt" >"$RESULTS/peak-connections.txt"
cat "$RESULTS/peak-connections.txt"
[[ $(wc -l <"$RESULTS/peak-connections.txt") == 2 ]] || fail 'connection samples are missing a database server'
while read -r server peak; do
  [[ $peak =~ ^[0-9]+$ && $peak -le 90 ]] || fail "$server reached $peak client connections, leaving less than the ten-connection headroom"
done <"$RESULTS/peak-connections.txt"
dc logs --no-color api1 api2 api3 worker 2>&1 | grep -E '53300|too many clients|too_many_connections' >"$RESULTS/database-errors.txt" || true
[[ ! -s $RESULTS/database-errors.txt ]] || fail 'database connection exhaustion occurred'
{
  echo '# Requirement 5 read-load comparison'
  echo
  echo 'Same rate and six demo products. This verifies distribution/continuity; it is not the Requirement 3 capacity benchmark.'
  echo
  echo '| Instances | Requests | Error rate | p50 ms | p95 ms | p99 ms |'
  echo '| --- | ---: | ---: | ---: | ---: | ---: |'
  jq -r '[.label, .total, .errorRate, .latencyMs["p(50)"], .latencyMs["p(95)"], .latencyMs["p(99)"]] | "| " + (map(tostring) | join(" | ")) + " |"' \
    "$RESULTS/one-instance.json" "$RESULTS/three-instances.json"
} >"$RESULTS/comparison.md"
cat "$RESULTS/comparison.md"
step 'PASS: worker recovery, pool budget, distribution, continuity and same-rate comparison'
