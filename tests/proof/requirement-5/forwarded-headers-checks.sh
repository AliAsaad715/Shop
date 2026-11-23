#!/usr/bin/env bash
# R5-05: two real client addresses behind one Nginx and one API, on disposable Docker resources.
# Run `.../forwarded-headers-checks.sh before` before changing the application, then run it without arguments after.
# One API isolates address forwarding from R5-06's separate distributed-counter work. Needs Docker, dotnet, curl, jq.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
export LC_ALL=C
readonly PHASE=${1:-after} PREFIX=shop-r5-forwarded PORT=58085
[[ $PHASE == before || $PHASE == after ]] || { echo 'usage: forwarded-headers-checks.sh [before|after]' >&2; exit 1; }
readonly NETWORK=$PREFIX IMAGE=$PREFIX-api PROXY=$PREFIX-nginx
WORK=$(mktemp -d "${TMPDIR:-/tmp}/$PREFIX.XXXXXX")
JWT_KEY=$(head -c 32 /dev/urandom | base64)
readonly WORK JWT_KEY
step() { printf '\n== %s\n' "$*"; }
say() { printf '   %s\n' "$*"; }
fail() { printf 'FAILED: %s\n' "$*" >&2; exit 1; }
cleanup() {
  docker rm -f "$PROXY" "$PREFIX-api1" "$PREFIX-postgres" "$PREFIX-migrator" \
    "$PREFIX-client1" "$PREFIX-client2" >/dev/null 2>&1 || true
  docker network rm "$NETWORK" >/dev/null 2>&1 || true
  docker image rm "$IMAGE" >/dev/null 2>&1 || true
}
wait_for() {
  local description=$1 deadline=$((SECONDS + 90))
  shift
  until "$@"; do
    ((SECONDS < deadline)) || fail "timeout: $description; logs in $WORK"
    sleep 0.5
  done
}
for tool in docker dotnet curl jq; do command -v "$tool" >/dev/null || fail "$tool is missing"; done
for image in postgres:18 nginx:1.30.7-alpine mcr.microsoft.com/dotnet/aspnet:10.0; do
  docker image inspect "$image" >/dev/null 2>&1 || fail "pull $image first"
done
! (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null || fail "port $PORT is in use"
cleanup
trap cleanup EXIT

step "R5-05 $PHASE: setup"
say "source commit $(git rev-parse --short HEAD); work folder $WORK"
dotnet publish src/Shop.Api/Shop.Api.csproj -c Release -o "$WORK/publish" -p:UseAppHost=false -nologo -v q \
  >"$WORK/publish.log" 2>&1 || { cat "$WORK/publish.log" >&2; fail 'API publish failed'; }
printf '%s\n' 'FROM mcr.microsoft.com/dotnet/aspnet:10.0' 'WORKDIR /app' 'COPY . .' 'USER $APP_UID' \
  'ENTRYPOINT ["dotnet", "Shop.Api.dll"]' | docker build -q -t "$IMAGE" -f - "$WORK/publish" >/dev/null
docker network create "$NETWORK" >/dev/null
subnet=$(docker network inspect -f '{{(index .IPAM.Config 0).Subnet}}' "$NETWORK")
docker run -d --name "$PREFIX-postgres" --network "$NETWORK" --network-alias postgres \
  -e POSTGRES_USER=shop -e POSTGRES_PASSWORD=shop -e POSTGRES_DB=shop postgres:18 >/dev/null
wait_for Postgres docker exec "$PREFIX-postgres" pg_isready -q -h 127.0.0.1 -U shop -d shop
api_settings=(--network "$NETWORK" -e ASPNETCORE_ENVIRONMENT=Production \
  -e 'ConnectionStrings__Shop=Host=postgres;Port=5432;Database=shop;Username=shop;Password=shop' \
  -e CatalogCache__Enabled=false -e ReadRouting__Enabled=false -e DemoSeed__Enabled=false \
  -e Admin__Email= -e Admin__Password= -e Jwt__SigningKey="$JWT_KEY" \
  -e Inventory__Address=http://127.0.0.1:1 -e RabbitMq__Host=127.0.0.1 -e RabbitMq__Port=1 \
  -e RabbitMq__UserName=checks -e RabbitMq__Password=checks -e Cors__AllowedOrigins__0=http://localhost:5173 \
  -e LoginRateLimit__PermitLimit=5 -e LoginRateLimit__WindowSeconds=60 \
  -e ReverseProxy__Enabled=true -e ReverseProxy__TrustedNetworks__0="$subnet")
docker run --rm --name "$PREFIX-migrator" "${api_settings[@]}" "$IMAGE" --migrate-and-seed \
  >"$WORK/migrator.log" 2>&1 || { cat "$WORK/migrator.log" >&2; fail 'migrator failed'; }
docker run -d --name "$PREFIX-api1" --network-alias api1 "${api_settings[@]}" "$IMAGE" >/dev/null
listening() { docker logs "$PREFIX-api1" 2>&1 | grep -q 'Now listening on'; }
wait_for API listening
docker run -d --name "$PROXY" --network "$NETWORK" --network-alias nginx -p "127.0.0.1:$PORT:80" \
  -v "$PWD/deploy/nginx/default.conf:/etc/nginx/conf.d/default.conf:ro" nginx:1.30.7-alpine >/dev/null
healthy() { [[ $(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/health") == 200 ]]; }
wait_for Nginx healthy
for client in 1 2; do
  docker run -d --name "$PREFIX-client$client" --network "$NETWORK" --user 1654:1654 \
    --entrypoint bash mcr.microsoft.com/dotnet/aspnet:10.0 -c 'sleep infinity' >/dev/null
done
address() { docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$1"; }
client1=$(address "$PREFIX-client1") client2=$(address "$PREFIX-client2") proxy=$(address "$PROXY")
[[ -n $client1 && -n $client2 && $client1 != "$client2" ]] || fail 'clients have no distinct addresses'
say "trusted network $subnet; Nginx $proxy; client1 $client1; client2 $client2"

login() {
  docker exec -i "$PREFIX-client$1" bash -s -- "$2" <<'CLIENT'
set -euo pipefail
body='{"email":"","password":""}'
exec 3<>/dev/tcp/nginx/80
printf 'POST /api/v1/auth/login HTTP/1.1\r\nHost: shop.forwarded.test:8080\r\nContent-Type: application/json\r\nContent-Length: %s\r\nConnection: close\r\nX-Forwarded-For: %s\r\nX-Forwarded-Proto: https\r\nX-Forwarded-Host: forged.example\r\n\r\n%s' \
  "${#body}" "$1" "$body" >&3
timeout 5 head -n 1 <&3 | tr -d '\r' | awk '{ print $2 }'
CLIENT
}
step "IP quota with forged client-supplied headers"
codes=()
for attempt in 1 2 3 4 5 6; do codes+=("$(login 1 "198.51.100.$attempt, 203.0.113.$attempt")"); done
[[ ${codes[*]} == '401 401 401 401 401 429' ]] || fail "unexpected first-client quota: ${codes[*]}"
second=$(login 2 '198.51.100.99')
say "client1, six attempts: ${codes[*]}; client2, its first attempt: $second"
if [[ $PHASE == before ]]; then
  [[ $second == 429 ]] || fail 'baseline did not reproduce a shared Nginx-address quota'
  say 'before: distinct clients share one bucket; the baseline application does not emit partition-key logs'
else
  [[ $second == 401 ]] || fail 'the second client shares the first client quota'
  [[ $(login 1 '192.0.2.250') == 429 ]] || fail 'a forged header bypassed the quota'
  docker logs "$PREFIX-api1" >"$WORK/api.log" 2>&1
  grep -q "partition $client1 " "$WORK/api.log" || fail 'client1 partition was not logged'
  grep -q "partition $client2 " "$WORK/api.log" || fail 'client2 partition was not logged'
  ! grep -q "partition $proxy " "$WORK/api.log" || fail 'Nginx was used as the partition'
  ! grep -q -E 'partition (198\.51\.100|203\.0\.113|192\.0\.2)' "$WORK/api.log" || fail 'forged address was logged as the partition'
  say 'after: quotas are independent, forged headers cannot bypass them, and logs use each real client address'
fi
for client in "$client1" "$client2"; do
  docker logs "$PROXY" 2>/dev/null | grep -q "$client .*POST /api/v1/auth/login" || fail 'Nginx did not log a real client'
done
say "Nginx access log contains both client addresses, distinct from $proxy"

step 'External URL keeps HTTP scheme and Host port despite forged forwarding headers'
servers=$(curl -s -H 'Host: shop.forwarded.test:8080' -H 'X-Forwarded-Proto: https' \
  -H 'X-Forwarded-Host: forged.example' "http://127.0.0.1:$PORT/openapi/v1.json" | jq -c .servers)
[[ $servers == '[{"url":"http://shop.forwarded.test:8080/"}]' ]] || fail "incorrect servers URL: $servers"
say "OpenAPI servers: $servers"
say "PASS: $PHASE"
