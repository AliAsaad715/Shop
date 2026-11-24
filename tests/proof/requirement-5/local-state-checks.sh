#!/usr/bin/env bash
# The local-state audit's checks (task R5-01): what breaks when three API instances share one database and one Redis.
# Three Shop.Api processes run side by side on this machine, on a throwaway Postgres and a throwaway Redis. There is no
# Nginx yet (R5-02), so every request goes to the instance it names and each result is exact, not a share of nine.
#
#   tests/proof/requirement-5/local-state-checks.sh
#   ROUNDS=5 tests/proof/requirement-5/local-state-checks.sh    # more first-start rounds for check 1
#
# The Compose stack and its data are never touched: the two containers have their own names and ports, and RabbitMQ
# and the warehouse point at a closed port, so the API runs Degraded without them, as designed. Everything is removed
# at the end, even when a step fails; the instances' logs stay in the folder printed at the start. Needs Docker, the
# .NET SDK, curl and jq, on Linux or WSL (the limiter checks send from 127.0.0.x addresses). What each result means,
# and which task fixes it, is in docs/requirement-5/local-state-audit.md.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
# An instance that loses the startup race ends with SIGABRT. Without this, each crash leaves a 90 MB core dump behind,
# and an instance started while the dumps are written can hang
ulimit -c 0

readonly POSTGRES=shop-r5-audit-postgres REDIS=shop-r5-audit-redis
readonly POSTGRES_PORT=55432 REDIS_PORT=56379
readonly ROUNDS=${ROUNDS:-3}
readonly PRODUCT=019a2180-0000-7000-8000-000000000205 # Limited Edition Desk Lamp, from the demo seed
readonly CATEGORY=019a2180-0000-7000-8000-000000000003 # Home
WORK=$(mktemp -d "${TMPDIR:-/tmp}/shop-r5-audit.XXXXXX")
WORK=$(cd "$WORK" && pwd -P)
[[ "$WORK" == "$(cd "${TMPDIR:-/tmp}" && pwd -P)"/shop-r5-audit.* ]] ||
  { printf 'Unexpected audit work directory: %s\n' "$WORK" >&2; exit 1; }
JWT_KEY=$(head -c 32 /dev/urandom | base64)
readonly WORK JWT_KEY
declare -A PIDS=()

step() { printf '\n== %s\n' "$*"; }
say() { printf '   %s\n' "$*"; }
fail() {
  printf 'FAILED: %s\n' "$*" >&2
  exit 1
}

# Checks a condition every half second until it holds, and fails after the given seconds
wait_for() {
  local what=$1 seconds=$2
  shift 2
  local deadline=$((SECONDS + seconds))
  until "$@"; do
    ((SECONDS < deadline)) || fail "waited ${seconds} s for $what"
    sleep 0.5
  done
}

api() { echo "http://127.0.0.1:510$1"; } # api 2: http://127.0.0.1:5102
code() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
sql() { docker exec "$POSTGRES" psql -X -q -At -v ON_ERROR_STOP=1 -U shop -d "${DB:-shop}" "$@"; }

# One instance, as Compose would run it, with its own port and its own image folder (no shared volume)
start_api() {
  local n=$1
  mkdir -p "$WORK/images-api$n"
  (
    cd src/Shop.Api
    exec env ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS="$(api "$n")" \
      ConnectionStrings__Shop="Host=127.0.0.1;Port=$POSTGRES_PORT;Database=shop;Username=shop;Password=shop" \
      ConnectionStrings__Redis="127.0.0.1:$REDIS_PORT" \
      Images__Provider=Local Images__RootPath="$WORK/images-api$n" \
      Jwt__SigningKey="$JWT_KEY" Admin__Email=admin@shop.local Admin__Password='Admin123!' DemoSeed__Enabled=true \
      Cors__AllowedOrigins__0=http://localhost:5173 Inventory__Address=http://127.0.0.1:1 \
      RabbitMq__Host=127.0.0.1 RabbitMq__Port=1 RabbitMq__UserName=audit RabbitMq__Password=audit \
      dotnet bin/Release/net10.0/Shop.Api.dll
  ) >"$WORK/api$n.log" 2>&1 &
  PIDS[$n]=$!
  disown "$!" # bash would print each crash's "Aborted"; check 1 reports it instead
}

initialize_database() {
  mkdir -p "$WORK/images-api1"
  (
    cd src/Shop.Api
    env ASPNETCORE_ENVIRONMENT=Production \
      ConnectionStrings__Shop="Host=127.0.0.1;Port=$POSTGRES_PORT;Database=shop;Username=shop;Password=shop" \
      ConnectionStrings__Redis="127.0.0.1:$REDIS_PORT" Images__Provider=Local Images__RootPath="$WORK/images-api1" \
      Admin__Email=admin@shop.local Admin__Password='Admin123!' DemoSeed__Enabled=true \
      dotnet bin/Release/net10.0/Shop.Api.dll --migrate-and-seed
  )
}

state() {
  if grep -q 'Now listening on' "$WORK/api$1.log"; then
    echo started
  elif kill -0 "${PIDS[$1]}" 2>/dev/null; then
    echo starting
  else
    echo crashed
  fi
}

wait_started_or_crashed() {
  local n deadline=$((SECONDS + 90))
  for n in "$@"; do
    while [[ $(state "$n") == starting ]]; do
      ((SECONDS < deadline)) || fail "api$n neither started nor stopped in 90 s; see $WORK/api$n.log"
      sleep 0.5
    done
  done
}

# Where and why an instance stopped: a migration fails before the host starts, a seeder while it starts
crash_reason() {
  local log=$WORK/api$1.log where error
  error=$(grep -o -m 1 -E 'PostgresException \(0x[0-9A-Fa-f]+\): .*' "$log" | sed -E 's/^[^)]*\): //' || true)
  if grep -q 'Hosting failed to start' "$log"; then where="in a startup seeder"; else where="while migrating"; fi
  echo "$where: ${error:-see $log}"
}

stop_apis() {
  local pid deadline=$((SECONDS + 30))
  for pid in "${PIDS[@]}"; do kill "$pid" 2>/dev/null || true; done
  for pid in "${PIDS[@]}"; do
    while kill -0 "$pid" 2>/dev/null && ((SECONDS < deadline)); do sleep 0.2; done
  done
  PIDS=()
}

cleanup() {
  stop_apis
  docker rm -f "$POSTGRES" "$REDIS" >/dev/null 2>&1 || true
}

for tool in docker dotnet curl jq; do command -v "$tool" >/dev/null || fail "$tool is missing"; done
for image in postgres:18 redis:8.10.3-alpine; do
  docker image inspect "$image" >/dev/null 2>&1 || fail "the $image image isn't on this machine: docker pull $image"
done
for port in "$POSTGRES_PORT" "$REDIS_PORT" 5101 5102 5103; do
  ! (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null || fail "port $port is in use; stop what listens there first"
done
trap cleanup EXIT

step "Setup"
say "logs: $WORK"
dotnet build src/Shop.Api/Shop.Api.csproj -c Release -nologo -v q >"$WORK/build.log" 2>&1 ||
  { cat "$WORK/build.log" >&2; fail "the API doesn't build"; }
say "built src/Shop.Api (Release)"
docker rm -f "$POSTGRES" "$REDIS" >/dev/null 2>&1 || true
docker run -d --name "$POSTGRES" -p "127.0.0.1:$POSTGRES_PORT:5432" \
  -e POSTGRES_USER=shop -e POSTGRES_PASSWORD=shop -e POSTGRES_DB=shop postgres:18 >/dev/null
docker run -d --name "$REDIS" -p "127.0.0.1:$REDIS_PORT:6379" redis:8.10.3-alpine >/dev/null
wait_for "Postgres to accept connections" 60 docker exec "$POSTGRES" pg_isready -q -h 127.0.0.1 -U shop -d shop
say "throwaway Postgres on 127.0.0.1:$POSTGRES_PORT and Redis on 127.0.0.1:$REDIS_PORT"

step "1. Explicit migrator, then three instances start at once ($ROUNDS empty-database rounds; R5-04)"
crashes=0
for round in $(seq 1 "$ROUNDS"); do
  DB=postgres sql -c 'DROP DATABASE IF EXISTS shop WITH (FORCE)' -c 'CREATE DATABASE shop'
  docker exec "$REDIS" redis-cli FLUSHALL >/dev/null
  rm -rf "$WORK"/images-api*
  initialize_database >"$WORK/round$round-migrator.log" 2>&1 ||
    { cat "$WORK/round$round-migrator.log" >&2; fail "migrator failed in round $round"; }
  for n in 1 2 3; do start_api "$n"; done
  wait_started_or_crashed 1 2 3
  for n in 1 2 3; do
    if [[ $(state "$n") == started ]]; then
      outcome="started"
    else
      outcome="CRASHED $(crash_reason "$n")"
      crashes=$((crashes + 1))
    fi
    if [[ $n == 1 ]]; then say "round $round: api$n $outcome"; else say "         api$n $outcome"; fi
    cp "$WORK/api$n.log" "$WORK/round$round-api$n.log"
  done
  stop_apis
done
say "-> $crashes of $((ROUNDS * 3)) first starts crashed"
[[ $crashes == 0 ]] || fail "an instance crashed after explicit initialization"
for n in 1 2 3; do
  start_api "$n"
  wait_started_or_crashed "$n"
  [[ $(state "$n") == started ]] || fail "api$n doesn't start even alone; see $WORK/api$n.log"
done
say "started again one at a time: api1 $(curl -s "$(api 1)/health"), api2 $(curl -s "$(api 2)/health")," \
  "api3 $(curl -s "$(api 3)/health") (Degraded: no RabbitMQ or warehouse here)"

step "2. Login rate limiter in memory: wrong passwords from one client"
code --interface 127.0.0.2 "$(api 1)/health" >/dev/null ||
  fail "curl can't send from 127.0.0.2 on this machine (Linux and WSL can)"
wrong_login() {
  code --interface "$1" -H 'Content-Type: application/json' \
    -d '{"email":"nobody@example.com","password":"Wrong-Pass-1"}' "$(api "$2")/api/v1/auth/login"
}
codes=""
for i in $(seq 1 10); do codes+="$(wrong_login 127.0.0.3 1) "; done
say "10 tries, all to api1:            $codes"
codes=""
for i in $(seq 1 18); do codes+="$(wrong_login 127.0.0.4 $(((i - 1) % 3 + 1))) "; done
say "18 tries, taking turns api1-api3: $codes"
say "-> $(sed 's/429.*//' <<<"$codes" | grep -o 401 | wc -l | tr -d ' ') tries before the first 429; one instance allows 5"

step "3. HybridCache's local copy: a product renamed through api1, then read from each instance"
token=$(curl -s -H 'Content-Type: application/json' -d '{"email":"admin@shop.local","password":"Admin123!"}' \
  "$(api 3)/api/v1/auth/login" | jq -r .accessToken)
[[ $token == ey* ]] || fail "the admin can't sign in"
name() { curl -s "$(api "$1")/api/v1/products/$PRODUCT" | jq -r .name; }
for n in 1 2 3; do name "$n" >/dev/null; done # every instance now holds a local copy
renamed="Desk Lamp renamed at $(date +%H:%M:%S)"
body=$(curl -s "$(api 1)/api/v1/products/$PRODUCT" | jq -c --arg name "$renamed" '{name: $name, description, price, categoryId}')
started=$(date +%s%N)
status=$(code -X PUT -H "Authorization: Bearer $token" -H 'Content-Type: application/json' -d "$body" \
  "$(api 1)/api/v1/products/$PRODUCT")
[[ $status == 204 ]] || fail "the rename returned $status"
say "PUT through api1: $status"
for i in $(seq 1 16); do
  row="" old=0
  for n in 1 2 3; do
    if [[ $(name "$n") == "$renamed" ]]; then row+="api$n new   "; else row+="api$n OLD   "; old=1; fi
  done
  say "$(awk -v ns="$(($(date +%s%N) - started))" 'BEGIN { printf "%4.1f s", ns / 1e9 }')  $row"
  [[ $old == 1 ]] || break
  sleep 0.5
done

step "4. Product images on local disk: each instance has its own folder, like containers without a shared volume"
seeded=$(curl -s "$(api 1)/api/v1/products/$PRODUCT" | jq -r .imageUrl)
say "a demo image seeded by migrator:  api1 $(code "$(api 1)$seeded"), api2 $(code "$(api 2)$seeded"), api3 $(code "$(api 3)$seeded")"
png=$(find "$WORK"/images-api? -type f -name '*.png' -print -quit)
[[ -n $png ]] || fail "no seeded image was found in any instance's folder"
uploaded=$(curl -s -H "Authorization: Bearer $token" -F name='Audit upload' -F description='R5-01 image check' \
  -F price=10 -F categoryId=$CATEGORY -F "image=@$png;type=image/png" "$(api 1)/api/v1/products" | jq -r .imageUrl)
found=""
for i in 1 2 3; do for n in 1 2 3; do found+="api$n:$(code "$(api "$n")$uploaded") "; done; done
say "uploaded through api1, 9 requests: $found"
say "-> $(grep -o ':200' <<<"$found" | wc -l | tr -d ' ') of 9 found"

step "5. SignalR negotiation: the negotiate request and the WebSocket reach different instances"
websocket() {
  curl -s -i --max-time 2 -H 'Connection: Upgrade' -H 'Upgrade: websocket' -H 'Sec-WebSocket-Version: 13' \
    -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' "$1" | head -n 1 | tr -d '\r' || true
}
negotiate() {
  curl -s -X POST -H "Authorization: Bearer $token" "$(api 1)/hubs/admin/negotiate?negotiateVersion=1" | jq -r .connectionToken
}
say "negotiate on api1, WebSocket on api1: $(websocket "$(api 1)/hubs/admin?id=$(negotiate)&access_token=$token")"
say "negotiate on api1, WebSocket on api2: $(websocket "$(api 2)/hubs/admin?id=$(negotiate)&access_token=$token")"
say "no negotiate (skipNegotiation), WebSocket on api2: $(websocket "$(api 2)/hubs/admin?access_token=$token")"

step "6. Absolute URLs: what the Host header changes"
servers() { curl -s ${1:+-H "Host: $1"} "$(api 1)/openapi/v1.json" | jq -c .servers; }
say "OpenAPI servers, Host: localhost       (Nginx \$host):      $(servers localhost)"
say "OpenAPI servers, Host: localhost:8080  (Nginx \$http_host): $(servers localhost:8080)"
location=$(curl -s -i -H "Authorization: Bearer $token" -H 'Host: api1:8080' -F name='Audit location' \
  -F description='R5-01 location check' -F price=10 -F categoryId=$CATEGORY -F "image=@$png;type=image/png" \
  "$(api 1)/api/v1/products" | grep -i '^location:' | tr -d '\r' || true)
say "POST /products with Host: api1:8080 -> $location"

step "7. Warehouse release retries (pending_releases): 20 queued, the warehouse unreachable"
ids=$(sql -c "INSERT INTO orders.pending_releases (order_id, queued_at, attempts, next_attempt_at, last_error)
  SELECT gen_random_uuid(), now(), 0, now(), 'R5-01 audit' FROM generate_series(1, 20) RETURNING order_id")
untried() { [[ $(sql -c "SELECT count(*) FROM orders.pending_releases WHERE last_error = 'R5-01 audit'") == 0 ]]; }
wait_for "every queued release to be tried once" 60 untried
sleep 10 # instances that read a row before the first one saved it are still working through their copy
shared=0
while read -r id; do
  handlers=$(grep -l "Retry 1 failed for warehouse release $id" "$WORK"/api[123].log | wc -l || true)
  if ((handlers > 1)); then shared=$((shared + 1)); fi
done <<<"$ids"
say "-> $shared of 20 rows had their first retry run by more than one instance"
say "first-retry log lines: api1 $(grep -c 'Retry 1 failed for warehouse release' "$WORK/api1.log"), api2" \
  "$(grep -c 'Retry 1 failed for warehouse release' "$WORK/api2.log"), api3" \
  "$(grep -c 'Retry 1 failed for warehouse release' "$WORK/api3.log")"

step "8. Database connections: the budget"
say "Postgres max_connections: $(sql -c 'SHOW max_connections'); Npgsql opens up to 100 per instance by default" \
  "(Maximum Pool Size), so three instances may ask for 300"

step "9. Checked and safe"
say "a token from api3, GET /users/me: api1 $(code -H "Authorization: Bearer $token" "$(api 1)/api/v1/users/me")," \
  "api2 $(code -H "Authorization: Bearer $token" "$(api 2)/api/v1/users/me")," \
  "api3 $(code -H "Authorization: Bearer $token" "$(api 3)/api/v1/users/me")"
customer="lockout-$(date +%s)@shop.test"
[[ $(code -H 'Content-Type: application/json' -d "{\"email\":\"$customer\",\"password\":\"Right-Pass-123!\"}" \
  "$(api 1)/api/v1/auth/register") == 201 ]] || fail "can't register $customer"
sign_in() {
  code --interface 127.0.0.5 -H 'Content-Type: application/json' -d "{\"email\":\"$customer\",\"password\":\"$1\"}" \
    "$(api "$2")/api/v1/auth/login"
}
codes=""
for i in 1 2 3 4 5; do codes+="api$(((i - 1) % 3 + 1)):$(sign_in Wrong-Pass-1 $(((i - 1) % 3 + 1))) "; done
say "account lockout, 5 wrong passwords: $codes-> the right one on api3: $(sign_in Right-Pass-123! 3)"
say "Data Protection lines in every log, Information level included: $(cat "$WORK"/*api*.log | grep -c -i 'DataProtection' || true)"
