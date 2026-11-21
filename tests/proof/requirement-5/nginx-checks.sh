#!/usr/bin/env bash
# The Nginx load balancer's checks (task R5-02): deploy/nginx/default.conf in front of three real API containers, on a
# throwaway Docker network with its own Postgres and Redis. Every request goes through Nginx, as a client's would.
#
#   tests/proof/requirement-5/nginx-checks.sh
#
# The Compose stack and its data are never touched: everything here has its own names, and Nginx is published on
# 127.0.0.1:58080 only. The API image is built from a local `dotnet publish` (the same command the API's Dockerfile
# runs), which is much faster than a full docker build. RabbitMQ and the warehouse point at a closed port, so the API
# runs Degraded without them, as designed. Everything is removed at the end, even when a step fails. Needs Docker,
# the .NET SDK, curl and jq, and the nginx:1.30.7-alpine image.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
ulimit -c 0 # a crashing instance must not leave a core dump behind
export LC_ALL=C # decimal points in printf, whatever the machine's locale

readonly PREFIX=shop-r5-nginx
readonly NETWORK=$PREFIX IMAGE=$PREFIX-api NGINX_IMAGE=nginx:1.30.7-alpine
readonly PROXY=$PREFIX NGINX=http://127.0.0.1:58080
readonly CATEGORY=019a2180-0000-7000-8000-000000000003 # Home, seeded with the schema
WORK=$(mktemp -d "${TMPDIR:-/tmp}/$PREFIX.XXXXXX")
JWT_KEY=$(head -c 32 /dev/urandom | base64)
readonly WORK JWT_KEY

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

code() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
seconds_since() { awk -v ns="$(($(date +%s%N) - $1))" 'BEGIN { printf "%.1f", ns / 1e9 }'; }
access_log() { docker logs "$PROXY" 2>/dev/null; }
error_log() { docker logs "$PROXY" 2>&1 >/dev/null; }

cleanup() {
  docker rm -f "$PROXY" "$PREFIX-api1" "$PREFIX-api2" "$PREFIX-api3" "$PREFIX-postgres" "$PREFIX-redis" \
    "$PREFIX-test" >/dev/null 2>&1 || true
  docker network rm "$NETWORK" >/dev/null 2>&1 || true
  docker image rm "$IMAGE" >/dev/null 2>&1 || true
}

# One API instance. Its name on the network is apiN, the name the Nginx config uses
start_api() {
  docker run -d --name "$PREFIX-api$1" --network "$NETWORK" --network-alias "api$1" \
    -e ASPNETCORE_ENVIRONMENT=Production \
    -e ConnectionStrings__Shop="Host=postgres;Port=5432;Database=shop;Username=shop;Password=shop" \
    -e ConnectionStrings__Redis=redis:6379 -e Images__RootPath=/tmp/images \
    -e Jwt__SigningKey="$JWT_KEY" -e Admin__Email=admin@shop.local -e Admin__Password='Admin123!' \
    -e DemoSeed__Enabled=true -e Cors__AllowedOrigins__0=http://localhost:5173 \
    -e Inventory__Address=http://127.0.0.1:1 \
    -e RabbitMq__Host=127.0.0.1 -e RabbitMq__Port=1 -e RabbitMq__UserName=checks -e RabbitMq__Password=checks \
    "$IMAGE" >/dev/null
}

listening() { # listening <n> [since]: only log lines after a restart's Unix time count
  docker logs ${2:+--since "$2"} "$PREFIX-api$1" 2>&1 | grep -q 'Now listening on' && return 0
  [[ $(docker inspect -f '{{.State.Running}}' "$PREFIX-api$1") == true ]] ||
    fail "api$1 stopped while starting: $(docker logs "$PREFIX-api$1" 2>&1 | grep -m 1 -E 'Exception|fail:' || true)"
  return 1
}

# Nginx logs upstream addresses; these turn them back into instance names
declare -A NAME=()
learn_addresses() {
  local n address
  for n in 1 2 3; do
    address=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$PREFIX-api$n" 2>/dev/null || true)
    if [[ -n $address ]]; then NAME["$address:8080"]=api$n; fi
  done
}

# "i|instances" for each access-log line of a batch: the request's i, and every instance it went to, in order
answered_by() {
  local i addresses names address
  access_log | grep -F "batch=$1&" | sed -nE 's/.*[?&]i=([0-9]+)[& ].* upstream=(.*) upstream_status=.*/\1|\2/p' |
    while IFS='|' read -r i addresses; do
      names=""
      for address in ${addresses//,/ }; do names+="${NAME[$address]:-$address} then "; done
      echo "$i|${names% then }"
    done
}

count_by_instance() {
  answered_by "$1" | sed 's/.* then //; s/.*|//' | sort | uniq -c | awk '{ printf "%s%s %s", sep, $2, $1; sep = ", " }'
}

requests() { # requests <batch> <count>
  local i
  for i in $(seq 1 "$2"); do code "$NGINX/health?batch=$1&i=$i" >/dev/null; done
}

for tool in docker dotnet curl jq; do command -v "$tool" >/dev/null || fail "$tool is missing"; done
for image in postgres:18 redis:8.10.3-alpine mcr.microsoft.com/dotnet/aspnet:10.0 "$NGINX_IMAGE"; do
  docker image inspect "$image" >/dev/null 2>&1 || fail "the $image image isn't on this machine: docker pull $image" \
    "(if Docker Hub is unreachable, pull public.ecr.aws/docker/library/$image and docker tag it)"
done
! (exec 3<>/dev/tcp/127.0.0.1/58080) 2>/dev/null || fail "port 58080 is in use; stop what listens there first"
cleanup
trap cleanup EXIT

step "Setup"
say "work folder: $WORK"
dotnet publish src/Shop.Api/Shop.Api.csproj -c Release -o "$WORK/publish" -p:UseAppHost=false -nologo -v q \
  >"$WORK/publish.log" 2>&1 || { cat "$WORK/publish.log" >&2; fail "dotnet publish failed"; }
printf 'FROM mcr.microsoft.com/dotnet/aspnet:10.0\nWORKDIR /app\nCOPY . .\nUSER $APP_UID\nENTRYPOINT ["dotnet", "Shop.Api.dll"]\n' |
  docker build -q -t "$IMAGE" -f - "$WORK/publish" >/dev/null
say "built the API image from dotnet publish"
docker network create "$NETWORK" >/dev/null
docker run -d --name "$PREFIX-postgres" --network "$NETWORK" --network-alias postgres \
  -e POSTGRES_USER=shop -e POSTGRES_PASSWORD=shop -e POSTGRES_DB=shop postgres:18 >/dev/null
docker run -d --name "$PREFIX-redis" --network "$NETWORK" --network-alias redis redis:8.10.3-alpine >/dev/null
wait_for "Postgres" 60 docker exec "$PREFIX-postgres" pg_isready -q -h 127.0.0.1 -U shop -d shop
say "network $NETWORK with Postgres and Redis"

step "1. The config: Nginx $(docker run --rm --entrypoint nginx "$NGINX_IMAGE" -v 2>&1 | sed 's/.*nginx\///') accepts it while no instance exists yet"
test_config() {
  docker run --rm --name "$PREFIX-test" --network "$NETWORK" -v "$1:/etc/nginx/conf.d/default.conf:ro" \
    --entrypoint nginx "$NGINX_IMAGE" -t 2>&1 | tail -n 1 | sed 's/^nginx: //' || true
}
say "deploy/nginx/default.conf:    $(test_config "$PWD/deploy/nginx/default.conf")"
sed 's/ resolve;/;/' deploy/nginx/default.conf >"$WORK/without-resolve.conf"
say "the same without resolve:     $(test_config "$WORK/without-resolve.conf")"
say "                              $(docker run --rm --network "$NETWORK" -v "$WORK/without-resolve.conf:/etc/nginx/conf.d/default.conf:ro" \
  --entrypoint nginx "$NGINX_IMAGE" -t 2>&1 | grep -m 1 -o 'host not found in upstream "[^"]*"' || true)"

step "2. resolve: Nginx starts with api1 and api2; api3 starts later and gets traffic without a reload"
start_api 1
wait_for "api1 to start (it migrates and seeds)" 120 listening 1
start_api 2
wait_for "api2 to start" 60 listening 2
docker run -d --name "$PROXY" --network "$NETWORK" -p 127.0.0.1:58080:80 \
  -v "$PWD/deploy/nginx/default.conf:/etc/nginx/conf.d/default.conf:ro" "$NGINX_IMAGE" >/dev/null
nginx_answers() { [[ $(code "$NGINX/health") == 200 ]]; }
wait_for "Nginx to answer" 30 nginx_answers
learn_addresses
requests before-api3 12
say "12 requests, api3 not started yet: $(count_by_instance before-api3)"
start_api 3
wait_for "api3 to start" 60 listening 3
learn_addresses
listened=$(date +%s%N)
i=0
api3_answered() {
  i=$((i + 1))
  code "$NGINX/health?batch=join&i=$i" >/dev/null
  answered_by join | grep -q '|api3$'
}
wait_for "Nginx to send api3 a request" 30 api3_answered
say "api3 answered its first request $(seconds_since "$listened") s after it started listening (a failed lookup is" \
  "retried every 5 s: resolver_timeout)"
say "Nginx's error log meanwhile: $(error_log | grep -m 1 -o 'api3 could not be resolved[^,]*' || echo 'nothing about api3')"

step "3. least_conn spreads requests over the three instances; each request is logged once"
requests spread 300
say "300 requests one after another: $(count_by_instance spread)"
marker=$(head -c 6 /dev/urandom | od -An -tx1 | tr -d ' \n')
code "$NGINX/health?batch=once&i=1&marker=$marker" >/dev/null
sleep 1
say "access-log lines for one request: $(access_log | grep -c "$marker" || true)"
say "a line: $(access_log | grep "$marker" | tail -n 1)"

step "4. Headers: Host keeps the port; no Nginx version"
say "OpenAPI servers through Nginx: $(curl -s "$NGINX/openapi/v1.json" | jq -c .servers)"
say "Server header: $(curl -s -D - -o /dev/null "$NGINX/health" | grep -i '^server:' | tr -d '\r')"

step "5. Request size: product images up to 2 MB get through to the API"
token=$(curl -s -H 'Content-Type: application/json' -d '{"email":"admin@shop.local","password":"Admin123!"}' \
  "$NGINX/api/v1/auth/login" | jq -r .accessToken)
[[ $token == ey* ]] || fail "the admin can't sign in through Nginx"
upload() { # upload <KB>: a PNG signature and random bytes, posted as a new product
  { printf '\x89PNG\r\n\x1a\n'; head -c $(($1 * 1024 - 8)) /dev/urandom; } >"$WORK/$1k.png"
  curl -s -o "$WORK/upload.out" -w '%{http_code} %{content_type}' -H "Authorization: Bearer $token" \
    -F name="Upload $1 KB" -F description='R5-02 size check' -F price=10 -F categoryId=$CATEGORY \
    -F "image=@$WORK/$1k.png;type=image/png" "$NGINX/api/v1/products"
}
say "1.5 MB image: $(upload 1536)   (Nginx's default limit of 1 MB would refuse it)"
result=$(upload 3072)
say "3 MB image:   $result $(jq -r '.code // empty' "$WORK/upload.out" 2>/dev/null || true)   (the API's own answer)"
say "5 MB image:   $(upload 5120)   (Nginx's answer, above client_max_body_size 4m)"

step "6. WebSockets through /hubs/"
websocket() { # the handshake a browser sends; prints the status line
  curl -s -i --max-time "${2:-1}" -H 'Connection: Upgrade' -H 'Upgrade: websocket' -H 'Sec-WebSocket-Version: 13' \
    -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' "$1" | head -n 1 | tr -d '\r' || true
}
for n in $(seq 1 6); do websocket "$NGINX/hubs/admin?batch=ws&i=$n&access_token=$token" 3 >"$WORK/ws$n.txt" & done
wait
say "6 WebSockets open at once, without negotiating: $(cat "$WORK"/ws?.txt | sort | uniq -c | awk '{ $1 = $1 " x"; print }' | paste -sd ' ')"
sleep 1
say "the instances they reached: $(count_by_instance ws)"
say "a line, token hidden: $(access_log | grep -F 'batch=ws&' | tail -n 1)"
negotiated=""
for n in $(seq 1 10); do
  connection=$(curl -s -X POST -H "Authorization: Bearer $token" "$NGINX/hubs/admin/negotiate?negotiateVersion=1" |
    jq -r .connectionToken)
  negotiated+="$(websocket "$NGINX/hubs/admin?id=$connection&access_token=$token" | awk '{ print $2 }') "
done
say "10 page loads that negotiate first, as the live pages do today (R5-10 fixes): $negotiated"

step "7. Passive health checks: api2 stops for 13 s while requests keep coming"
i=0
started=$(date +%s%N)
(
  while (($(date +%s%N) - started < 34000000000)); do
    i=$((i + 1))
    sent=$(seconds_since "$started")
    echo "$i $sent $(curl -s -o /dev/null -w '%{http_code} %{time_total}' --max-time 10 "$NGINX/health?batch=load&i=$i" ||
      echo '000 10')"
    sleep 0.1
  done
) >"$WORK/load.txt" &
load=$!
sleep 5
stopping=$(seconds_since "$started")
docker stop -t 30 "$PREFIX-api2" >/dev/null
stopped=$(seconds_since "$started")
remaining=$((18 - ${stopped%.*}))
if ((remaining > 0)); then sleep "$remaining"; fi
starting=$(seconds_since "$started")
restart=$(date +%s)
docker start "$PREFIX-api2" >/dev/null
wait_for "api2 to start again" 60 listening 2 "$restart"
back=$(seconds_since "$started")
wait "$load"
learn_addresses
declare -A BY=()
while IFS='|' read -r n instances; do BY[$n]=$instances; done < <(answered_by load)
total=0 errors=0 slowest=0 slowest_line="" last_api2=0 first_api2=""
failovers=()
while read -r n sent status took; do
  total=$((total + 1))
  [[ $status == 200 ]] || errors=$((errors + 1))
  if awk -v a="$took" -v b="$slowest" 'BEGIN { exit !(a > b) }'; then slowest=$took slowest_line="request at $sent s: ${BY[$n]:-?}"; fi
  case ${BY[$n]:-} in
    *" then "*) failovers+=("at $sent s: ${BY[$n]} in $(printf '%.1f' "$took") s") ;;
    api2) if awk -v t="$sent" -v s="$starting" 'BEGIN { exit !(t < s) }'; then last_api2=$sent; elif [[ -z $first_api2 ]]; then first_api2=$sent; fi ;;
  esac
done <"$WORK/load.txt"
say "docker stop api2 at $stopping s (it stopped at $stopped s); docker start at $starting s (listening at $back s)"
say "$total requests: $((total - errors)) answered 200, $errors failed; the slowest took $(printf '%.1f' "$slowest") s ($slowest_line)"
say "api2's last answer before stopping: at $last_api2 s; its first after starting: at ${first_api2:-never} s"
say "requests that failed over to another instance: ${#failovers[@]}"
for line in "${failovers[@]}"; do say "  $line"; done
say "Nginx's error log: $(error_log | grep -c -E 'connect\(\) failed|timed out .*while connecting' || true) failed connections," \
  "$(error_log | grep -c 'temporarily disabled' || true) times an instance was taken out," \
  "$(error_log | grep -c 'api2 could not be resolved' || true) failed lookups of api2"
