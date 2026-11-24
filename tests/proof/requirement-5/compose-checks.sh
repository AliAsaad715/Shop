#!/usr/bin/env bash
# The Compose layout's checks (task R5-03): `docker compose up` with three API instances behind Nginx and three email
# workers, started on empty volumes, then looked at through Nginx as a client would. R5-04 also checks that a failed
# migrator blocks them, succeeds on retry, and is the only service that initializes the database.
#
#   tests/proof/requirement-5/compose-checks.sh
#
# It runs as its own Compose project with other host ports, so the stack you run (project "shop") and its data are
# never touched. The API's and the worker's images are built from a local `dotnet publish`, the command their
# Dockerfiles run, which is much faster than a full docker build; the warehouse uses the shop-inventory image Compose
# built before. Everything is removed at the end, even when a step fails. Needs Docker, the .NET SDK, curl, jq, a .env
# with JWT_SIGNING_KEY (Compose reads it), and the images compose.yaml names; step 5 also uses grafana/k6:2.3.0.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
export LC_ALL=C

readonly PROJECT=shop-r5-compose
WORK=$(mktemp -d "${TMPDIR:-/tmp}/$PROJECT.XXXXXX")
readonly WORK
export COMPOSE_PROJECT_NAME=$PROJECT COMPOSE_FILE="$PWD/compose.yaml:$WORK/override.yaml"
export API_PORT=58180 POSTGRES_PORT=55433 REDIS_PORT=56380 RABBITMQ_PORT=55672 RABBITMQ_UI_PORT=55673 \
  MAILPIT_UI_PORT=58025 MAILPIT_SMTP_PORT=51025 REPLICA_PORT=55434
# The proof requires the demo catalog and must always capture mail locally, regardless of the developer's .env.
export DEMO_SEED_ENABLED=true EMAIL_PROVIDER=Mailpit
# Keep the fixed trust subnet separate from a developer's running shop network (R5-05).
export SHOP_NETWORK_SUBNET=172.30.51.0/24
readonly NGINX=http://localhost:$API_PORT RABBIT=http://localhost:$RABBITMQ_UI_PORT MAILPIT=http://localhost:$MAILPIT_UI_PORT
readonly PRODUCT=019a2180-0000-7000-8000-000000000202 # Cotton T-Shirt; the warehouse seed stocks 100

step() { printf '\n== %s\n' "$*"; }
say() { printf '   %s\n' "$*"; }
fail() {
  printf 'FAILED: %s\n' "$*" >&2
  exit 1
}

# A Compose setting as Compose resolves it: from the environment, else from .env, else its default
setting() {
  local name=$1 fallback=$2 value
  if [[ -n ${!name:-} ]]; then
    printf '%s' "${!name}"
    return
  fi
  value=$(sed -n "s/^$name=//p" .env 2>/dev/null | tail -n 1)
  value=${value#[\"\']}
  value=${value%[\"\']}
  printf '%s' "${value:-$fallback}"
}

instance_of() { curl -s -D - -o /dev/null "$@" | tr -d '\r' | sed -n 's/^[Xx]-[Ii]nstance: //p'; }

cleanup() {
  docker compose down -v --remove-orphans >/dev/null 2>&1 || true
  docker image rm "$PROJECT-api" "$PROJECT-worker" >/dev/null 2>&1 || true
}

for tool in docker dotnet curl jq; do command -v "$tool" >/dev/null || fail "$tool is missing"; done
[[ -n $(setting JWT_SIGNING_KEY "") ]] || fail "set JWT_SIGNING_KEY in .env first (see .env.example)"
for image in postgres:18 redis:8.10.3-alpine rabbitmq:4.3.7-management-alpine axllent/mailpit:v1.33.1 \
  nginx:1.30.7-alpine mcr.microsoft.com/dotnet/aspnet:10.0; do
  docker image inspect "$image" >/dev/null 2>&1 || fail "the $image image isn't on this machine: docker pull $image"
done
docker image inspect shop-inventory >/dev/null 2>&1 || fail "no shop-inventory image yet: docker compose build inventory"
for port in "$API_PORT" "$POSTGRES_PORT" "$REDIS_PORT" "$RABBITMQ_PORT" "$RABBITMQ_UI_PORT" "$MAILPIT_UI_PORT" \
  "$MAILPIT_SMTP_PORT"; do
  ! (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null || fail "port $port is in use; stop what listens there first"
done
cleanup
trap cleanup EXIT

step "Setup"
say "work folder: $WORK"
for host in Api Worker; do
  dotnet publish "src/Shop.$host/Shop.$host.csproj" -c Release -o "$WORK/$host" -p:UseAppHost=false -nologo -v q \
    >"$WORK/publish-$host.log" 2>&1 || { cat "$WORK/publish-$host.log" >&2; fail "dotnet publish of Shop.$host failed"; }
done
# The runtime stages of src/Shop.Api/Dockerfile and src/Shop.Worker/Dockerfile
printf '%s\n' 'FROM mcr.microsoft.com/dotnet/aspnet:10.0' 'WORKDIR /app' \
  'RUN mkdir -p /data/images && chown $APP_UID:$APP_UID /data/images' 'COPY . .' 'USER $APP_UID' 'EXPOSE 8080' \
  'ENTRYPOINT ["dotnet", "Shop.Api.dll"]' | docker build -q -t "$PROJECT-api" -f - "$WORK/Api" >/dev/null
printf '%s\n' 'FROM mcr.microsoft.com/dotnet/aspnet:10.0' 'WORKDIR /app' 'COPY . .' 'USER $APP_UID' \
  'ENTRYPOINT ["dotnet", "Shop.Worker.dll"]' | docker build -q -t "$PROJECT-worker" -f - "$WORK/Worker" >/dev/null
cat >"$WORK/override.yaml" <<EOF
services:
  migrator: { image: $PROJECT-api }
  api1: { image: $PROJECT-api }
  api2: { image: $PROJECT-api }
  api3: { image: $PROJECT-api }
  worker: { image: $PROJECT-worker }
  inventory: { image: shop-inventory }
EOF
say "built the API's and the worker's images from dotnet publish; project $PROJECT, Nginx on $NGINX"

step "0. A failed migrator blocks API instances and workers on empty volumes (R5-04)"
if ADMIN_PASSWORD=weak docker compose up -d --no-build api1 api2 api3 worker >"$WORK/failed-up.log" 2>&1; then
  fail "Compose started despite a weak admin seed password"
fi
migrator=$(docker compose ps -a -q migrator)
[[ -n $migrator && $(docker inspect -f '{{.State.ExitCode}}' "$migrator") == 1 ]] ||
  fail "the migrator did not exit with code 1"
for service in api1 api2 api3 worker; do
  for id in $(docker compose ps -a -q "$service"); do
    [[ $(docker inspect -f '{{.State.Running}}' "$id") == false ]] || fail "$service started despite failed initialization"
  done
done
say "migrator exited 1; none of the three API instances or three workers started"

step "1. Correct the password and retry on the same database"
started=$SECONDS
docker compose up -d --no-build --wait >"$WORK/up.log" 2>&1 || { tail -n 20 "$WORK/up.log" >&2; fail "docker compose up failed"; }
say "every service up and healthy after $((SECONDS - started)) s:"
# One line per service, with the host ports it publishes (IPv4 only, to keep it short)
docker compose ps --format '{{.Service}}|{{.Status}}|{{.Ports}}' | awk -F'|' '{
  n[$1]++; status[$1] = $2; ports = $3; out = ""
  while (match(ports, /[0-9.]+:[0-9]+->[0-9]+/)) {
    out = out (out == "" ? "" : ", ") substr(ports, RSTART, RLENGTH)
    ports = substr(ports, RSTART + RLENGTH)
  }
  published[$1] = out
} END {
  for (s in n) printf "     %-13s %s%s%s\n", s, (n[s] > 1 ? n[s] " x " : ""), status[s], (published[s] != "" ? "   " published[s] : "")
}' | sort
published=$(docker compose ps --format '{{.Service}} {{.Ports}}' | grep -- '->' | awk '{ print $1 }' | sort -u | paste -sd ' ')
[[ ! " $published " =~ \ api[123]\  ]] || fail "an API instance publishes a port: $published"
say "services publishing a port: $published; none of api1-api3"
migrator=$(docker compose ps -a -q migrator)
[[ $(docker inspect -f '{{.State.ExitCode}}' "$migrator") == 0 ]] || fail "migrator did not succeed"
finished=$(docker inspect -f '{{.State.FinishedAt}}' "$migrator")
for service in api1 api2 api3 worker; do
  for id in $(docker compose ps -q "$service"); do
    beginning=$(docker inspect -f '{{.State.StartedAt}}' "$id")
    [[ "$finished" < "$beginning" || "$finished" == "$beginning" ]] || fail "$service started before migrator finished"
  done
done
say "migrator exited 0 before every API instance and worker started; api2/api3 no longer wait for api1"

step "2. Only migrator initializes; runtime hosts never migrate or seed"
docker compose logs --no-log-prefix migrator >"$WORK/migrator.log" 2>&1
grep -q 'Database initialization completed' "$WORK/migrator.log" || fail "no successful initialization log"
for service in api1 api2 api3 worker; do
  logs=$(docker compose logs --no-log-prefix "$service" 2>&1)
  ! grep -q -E 'Applying migration|No migrations were applied|Running module seeder|Seeded admin account|Seeded [0-9]+ demo products|Demo catalog already present' \
    <<<"$logs" || fail "$service migrated or seeded at startup"
  say "$service: $(grep -c 'Applying migration' <<<"$logs" || true) migrations applied," \
    "$(grep -c -E 'Seeded admin account|Seeded [0-9]+ demo products' <<<"$logs" || true) seeding steps," \
    "$(grep -c 'Unhandled exception' <<<"$logs" || true) crashes"
done
say "worker restarts: $(for id in $(docker compose ps -q worker); do docker inspect -f '{{.RestartCount}}' "$id"; done | paste -sd ' ')" \
  "(notifications schema prepared by migrator)"
[[ $(curl -s -L -o /dev/null -w '%{http_code}' "$NGINX/docs") == 200 ]] || fail "/docs is unavailable"
image_path=$(docker compose exec -T postgres psql -X -q -At -U "$(setting POSTGRES_USER shop)" -d "$(setting POSTGRES_DB shop)" \
  -c "SELECT image_path FROM products.products WHERE id='$PRODUCT'")
for service in api1 api2 api3; do
  # The image has bash, not curl. Ask each instance internally, without publishing any API port.
  docker compose exec -T "$service" bash -c \
    "exec 3<>/dev/tcp/127.0.0.1/8080; printf 'GET /images/$image_path HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3; timeout 3 head -n 1 <&3 | grep -q ' 200 '" ||
    fail "demo image is unavailable on $service"
done
say "demo image is available on api1/api2/api3 through the shared images volume; /docs returns 200 after its redirect"

step "3. X-Instance through Nginx"
say "GET /health: X-Instance $(instance_of "$NGINX/health"); a 404: X-Instance $(instance_of "$NGINX/api/v1/missing")"
for i in $(seq 1 300); do instance_of "$NGINX/health"; done | sort | uniq -c >"$WORK/split.txt"
say "300 requests one after another: $(awk '{ printf "%s%s %s", sep, $2, $1; sep = ", " }' "$WORK/split.txt")"

step "4. RabbitMQ tells the connections apart"
auth="$(setting RABBITMQ_USER shop):$(setting RABBITMQ_PASSWORD shop)"
say "connections: $(curl -s -u "$auth" "$RABBIT/api/connections" | jq -r '[.[].client_properties.connection_name] | sort | join(", ")')"
say "consumers of the email queue: $(curl -s -u "$auth" "$RABBIT/api/queues/%2F/email.order-confirmation" | jq -r .consumers)"

step "5. An order through Nginx, and k6"
customer="compose-$(date +%s)@shop.test"
credentials=$(jq -nc --arg email "$customer" '{email: $email, password: "Check-Pass-123!"}')
curl -s -o /dev/null -H 'Content-Type: application/json' -d "$credentials" "$NGINX/api/v1/auth/register"
token=$(curl -s -H 'Content-Type: application/json' -d "$credentials" "$NGINX/api/v1/auth/login" | jq -r .accessToken)
[[ $token == ey* ]] || fail "the new customer can't sign in"
status=$(curl -s -D "$WORK/order.headers" -o "$WORK/order.json" -w '%{http_code}' -H "Authorization: Bearer $token" \
  -H 'Content-Type: application/json' -d "{\"lines\":[{\"productId\":\"$PRODUCT\",\"quantity\":1}]}" "$NGINX/api/v1/orders")
order=$(jq -r .id "$WORK/order.json")
[[ $status == 201 ]] || fail "order creation returned $status"
emailed() { [[ $(curl -s "$MAILPIT/api/v1/search?query=to:$customer" | jq -r '.messages_count // 0') -gt 0 ]]; }
for _ in $(seq 60); do emailed && break; sleep 0.5; done
emailed || fail "the confirmation email never arrived"
say "POST /api/v1/orders: $status from $(tr -d '\r' <"$WORK/order.headers" | sed -n 's/^[Xx]-[Ii]nstance: //p');" \
  "its email sent by $(docker compose logs worker 2>&1 | grep "Sent the confirmation for order $order" | awk '{ print $1 }' | head -n 1)"
if docker image inspect grafana/k6:2.3.0 >/dev/null 2>&1; then
  cat >"$WORK/k6.js" <<'EOF'
import http from 'k6/http';
import { Counter } from 'k6/metrics';
const answered = new Counter('answered_by');
export const options = {
  vus: 10,
  duration: '10s',
  // A threshold on each tagged count makes k6's summary print it
  thresholds: {
    'answered_by{instance:api1}': ['count>0'], 'answered_by{instance:api2}': ['count>0'],
    'answered_by{instance:api3}': ['count>0'], http_req_failed: ['rate==0'],
  },
};
export default function () {
  const response = http.get(`${__ENV.API_URL}/api/v1/products?page=1&pageSize=20`);
  answered.add(1, { instance: response.headers['X-Instance'] || 'none' });
}
EOF
  # As this user, like perf/run.sh: the work folder is private to it
  docker run --rm --network "${PROJECT}_default" -u "$(id -u):$(id -g)" -e API_URL=http://nginx \
    -v "$WORK:/scripts:ro" grafana/k6:2.3.0 run --quiet /scripts/k6.js >"$WORK/k6.txt" 2>&1 ||
    { cat "$WORK/k6.txt" >&2; fail "k6 failed"; }
  counts=$(grep -A1 'answered_by{instance' "$WORK/k6.txt" | sed -n 's/.*count=\([0-9]*\).*/\1/p' | paste -sd ' ')
  [[ $(wc -w <<<"$counts") == 3 ]] || fail "k6 counted no answers: $(head -n 3 "$WORK/k6.txt")"
  say "k6, 10 users for 10 s through Nginx (as perf/run.sh points it):" \
    "$(awk '{ printf "api1 %s, api2 %s, api3 %s", $1, $2, $3 }' <<<"$counts");" \
    "failed requests $(grep -A1 'http_req_failed$' "$WORK/k6.txt" | sed -n 's/.*rate=\([0-9.]*%\).*/\1/p')"
else
  say "k6: skipped, no grafana/k6:2.3.0 image here"
fi

step "6. Explicit initialization is repeatable; restarting runtime hosts never initializes"
docker compose run --rm --no-deps migrator >"$WORK/repeated-migrator.log" 2>&1 || fail "repeated initialization failed"
grep -q 'Demo catalog already present' "$WORK/repeated-migrator.log" || fail "demo catalog was not preserved"
initial_started=$(docker inspect -f '{{.State.StartedAt}}' "$migrator")
docker compose restart api1 api2 api3 worker >"$WORK/restart.log" 2>&1
docker compose up -d --no-build --no-deps --wait api1 api2 api3 worker >>"$WORK/restart.log" 2>&1 || fail "runtime restart failed"
[[ $(docker inspect -f '{{.State.StartedAt}}' "$migrator") == "$initial_started" ]] || fail "runtime restart reran migrator"
! docker compose logs --no-log-prefix api1 api2 api3 worker 2>&1 |
  grep -q -E 'Applying migration|No migrations were applied|Running module seeder|Seeded admin account|Seeded [0-9]+ demo products|Demo catalog already present' ||
  fail "a runtime host migrated or seeded after restarting"
say "explicit rerun preserved the catalog; all six runtime hosts restarted without initialization"
