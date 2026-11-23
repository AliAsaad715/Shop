#!/usr/bin/env bash
# R5-07: run `before` on the parent commit, then `after` on this feature. All resources are disposable;
# neither the normal Shop project nor its volumes are touched. Requires Docker, dotnet, curl, jq and sha256sum.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
export LC_ALL=C
MODE=${1:-after}
[[ $MODE == before || $MODE == after ]] || { echo 'usage: image-storage-checks.sh before|after' >&2; exit 1; }
readonly PREFIX=shop-r5-images NETWORK=shop-r5-images IMAGE=shop-r5-images-api
readonly STORAGE_IMAGE=chrislusf/seaweedfs:4.53 URL=http://127.0.0.1:58087
readonly CATEGORY=019a2180-0000-7000-8000-000000000003
WORK=$(mktemp -d "${TMPDIR:-/tmp}/$PREFIX.XXXXXX")
JWT_KEY=$(head -c 32 /dev/urandom | base64)
readonly WORK JWT_KEY
step() { printf '\n== %s\n' "$*"; }
say() { printf '   %s\n' "$*"; }
fail() { echo "FAILED: $*" >&2; exit 1; }
wait_for() {
  local deadline=$((SECONDS + $2)) what=$1; shift 2
  until "$@"; do ((SECONDS < deadline)) || fail "waited for $what"; sleep 0.5; done
}
cleanup() {
  docker rm -f "$PREFIX-nginx" "$PREFIX-api1" "$PREFIX-api2" "$PREFIX-api3" "$PREFIX-postgres" \
    "$PREFIX-redis" "$PREFIX-seaweed" "$PREFIX-migrator" "$PREFIX-import" >/dev/null 2>&1 || true
  docker volume rm "$PREFIX-local1" "$PREFIX-local2" "$PREFIX-local3" "$PREFIX-s3" >/dev/null 2>&1 || true
  docker network rm "$NETWORK" >/dev/null 2>&1 || true
  docker image rm "$IMAGE" >/dev/null 2>&1 || true
}
for tool in docker dotnet curl jq sha256sum; do command -v "$tool" >/dev/null || fail "$tool is missing"; done
for image in postgres:18 redis:8.10.3-alpine nginx:1.30.7-alpine mcr.microsoft.com/dotnet/aspnet:10.0; do
  docker image inspect "$image" >/dev/null 2>&1 || fail "docker pull $image first"
done
if [[ $MODE == after ]]; then docker image inspect "$STORAGE_IMAGE" >/dev/null || fail "docker pull $STORAGE_IMAGE first"; fi
! (exec 3<>/dev/tcp/127.0.0.1/58087) 2>/dev/null || fail 'port 58087 is in use'
cleanup
trap cleanup EXIT
step "R5-07 $MODE; source $(git rev-parse --short HEAD) (working tree compiled below)"
say "isolated resources: $PREFIX; work folder: $WORK"
dotnet publish src/Shop.Api/Shop.Api.csproj -c Release -o "$WORK/publish" -p:UseAppHost=false -nologo -v q \
  >"$WORK/publish.log" 2>&1 || { cat "$WORK/publish.log" >&2; fail publish; }
printf '%s\n' 'FROM mcr.microsoft.com/dotnet/aspnet:10.0' 'WORKDIR /app' \
  'RUN mkdir -p /data/images && chown $APP_UID:$APP_UID /data/images' 'COPY . .' 'USER $APP_UID' \
  'ENTRYPOINT ["dotnet", "Shop.Api.dll"]' | docker build -q -t "$IMAGE" -f - "$WORK/publish" >/dev/null
docker network create "$NETWORK" >/dev/null
subnet=$(docker network inspect -f '{{(index .IPAM.Config 0).Subnet}}' "$NETWORK")
for volume in local1 local2 local3 s3; do docker volume create "$PREFIX-$volume" >/dev/null; done
docker run -d --name "$PREFIX-postgres" --network "$NETWORK" --network-alias postgres \
  -e POSTGRES_USER=shop -e POSTGRES_PASSWORD=shop -e POSTGRES_DB=shop postgres:18 >/dev/null
docker run -d --name "$PREFIX-redis" --network "$NETWORK" --network-alias redis redis:8.10.3-alpine >/dev/null
wait_for Postgres 60 docker exec "$PREFIX-postgres" pg_isready -q -h 127.0.0.1 -U shop -d shop
COMMON=(-e ASPNETCORE_ENVIRONMENT=Production -e Jwt__SigningKey="$JWT_KEY"
  -e ConnectionStrings__Shop='Host=postgres;Port=5432;Database=shop;Username=shop;Password=shop'
  -e ConnectionStrings__Redis=redis:6379 -e CatalogCache__Enabled=false
  -e Admin__Email=admin@shop.local -e Admin__Password='Admin123!' -e DemoSeed__Enabled=true
  -e Inventory__Address=http://127.0.0.1:1 -e RabbitMq__Host=127.0.0.1 -e RabbitMq__Port=1
  -e RabbitMq__UserName=proof -e RabbitMq__Password=proof)
S3=(-e Images__Provider=S3 -e Images__S3__Endpoint=http://seaweedfs:8333 -e Images__S3__Bucket=shop-images
  -e Images__S3__AccessKey=proof -e Images__S3__SecretKey=isolated-proof-only)
step 'Initialize the legacy database and demo images with Local storage'
docker run --rm --name "$PREFIX-migrator" --network "$NETWORK" "${COMMON[@]}" \
  -e Images__Provider=Local -e Images__RootPath=/data/images -v "$PREFIX-local1:/data/images" \
  "$IMAGE" --migrate-and-seed >"$WORK/migrator.log" 2>&1 || { cat "$WORK/migrator.log" >&2; fail migrator; }
start_storage() {
  docker run -d --name "$PREFIX-seaweed" --network "$NETWORK" --network-alias seaweedfs \
    -v "$PREFIX-s3:/data" -e AWS_ACCESS_KEY_ID=proof -e AWS_SECRET_ACCESS_KEY=isolated-proof-only \
    -e S3_BUCKET=shop-images "$STORAGE_IMAGE" mini -dir=/data -ip=seaweedfs -ip.bind=0.0.0.0 \
    -webdav=false -admin.ui=false -s3.autoCreateBucket=false >/dev/null
}
storage_ready() { docker exec "$PREFIX-seaweed" wget -q -O /dev/null http://127.0.0.1:8888/buckets/shop-images/ >/dev/null 2>&1; }
if [[ $MODE == after ]]; then
  step 'Import legacy files twice, retaining keys and validating content'
  start_storage
  wait_for SeaweedFS 90 storage_ready
  for round in 1 2; do
    docker run --rm --name "$PREFIX-import" --network "$NETWORK" "${COMMON[@]}" "${S3[@]}" \
      -v "$PREFIX-local1:/legacy-images:ro" -e Images__RootPath=/legacy-images "$IMAGE" --import-images \
      >"$WORK/import$round.log" 2>&1 || { cat "$WORK/import$round.log" >&2; fail import; }
    grep 'Image import completed' "$WORK/import$round.log" | sed 's/^/   /'
  done
  docker run --rm --name "$PREFIX-migrator" --network "$NETWORK" "${COMMON[@]}" "${S3[@]}" \
    "$IMAGE" --migrate-and-seed >"$WORK/s3-migrator.log" 2>&1 || { cat "$WORK/s3-migrator.log" >&2; fail 'S3 migrator'; }
fi
start_api() {
  local n=$1; local -a storage=("${S3[@]}")
  if [[ $MODE == before ]]; then storage=(-e Images__Provider=Local -e Images__RootPath=/data/images -v "$PREFIX-local$n:/data/images"); fi
  docker run -d --name "$PREFIX-api$n" --network "$NETWORK" --network-alias "api$n" "${COMMON[@]}" \
    -e INSTANCE_NAME="api$n" -e ReverseProxy__Enabled=true -e ReverseProxy__TrustedNetworks__0="$subnet" \
    "${storage[@]}" "$IMAGE" >/dev/null
}
listening() { docker logs "$PREFIX-api$1" 2>&1 | grep -q 'Now listening on'; }
for n in 1 2 3; do start_api "$n"; wait_for "api$n" 60 listening "$n"; done
docker run -d --name "$PREFIX-nginx" --network "$NETWORK" -p 127.0.0.1:58087:80 \
  -v "$PWD/deploy/nginx/default.conf:/etc/nginx/conf.d/default.conf:ro" nginx:1.30.7-alpine >/dev/null
proxy_ready() { [[ $(curl -s -o /dev/null -w '%{http_code}' "$URL/health") == 200 ]]; }
wait_for Nginx 30 proxy_ready
token=$(curl -fsS -H 'Content-Type: application/json' -d '{"email":"admin@shop.local","password":"Admin123!"}' \
  "$URL/api/v1/auth/login" | jq -r .accessToken)
[[ $token == ey* ]] || fail login
cp src/Modules/Products/Shop.Modules.Products/Seeding/demo-product.png "$WORK/upload.png"
source_hash=$(sha256sum "$WORK/upload.png" | cut -d' ' -f1)
upload() {
  curl -s -D "$WORK/upload.headers" -o "$WORK/upload.json" -w '%{http_code}' -H "Authorization: Bearer $token" \
    -F name='R5-07 upload' -F description='Cross-instance image proof' -F price=10 -F categoryId="$CATEGORY" \
    -F "image=@$WORK/upload.png;type=image/png" "$URL/api/v1/products"
}
step 'Upload once through Nginx; read three times from each instance through Nginx'
[[ $(upload) == 201 ]] || { cat "$WORK/upload.json"; fail upload; }
owner=$(tr -d '\r' <"$WORK/upload.headers" | sed -n 's/^[Xx]-[Ii]nstance: api//p')
path=$(jq -r .imageUrl "$WORK/upload.json")
say "upload answered by api$owner; public path $path"
nine_reads() {
  local attempt n status hash found=0; local -a counts=(0 0 0 0)
  for attempt in $(seq 1 90); do
    status=$(curl -s -D "$WORK/read.headers" -o "$WORK/read.body" -w '%{http_code}' "$URL$path?attempt=$attempt")
    n=$(tr -d '\r' <"$WORK/read.headers" | sed -n 's/^[Xx]-[Ii]nstance: api//p')
    [[ $n =~ ^[123]$ ]] || fail 'missing X-Instance'
    ((counts[n] < 3)) || continue
    counts[n]=$((counts[n] + 1))
    if [[ $status == 200 ]]; then
      hash=$(sha256sum "$WORK/read.body" | cut -d' ' -f1)
      [[ $hash == "$source_hash" ]] || fail 'image bytes changed'
      found=$((found + 1))
    elif [[ $MODE != before || $status != 404 ]]; then fail "image read returned $status"; fi
    say "api$n read ${counts[n]}: $status"
    ((counts[1] + counts[2] + counts[3] < 9)) || break
  done
  ((counts[1] == 3 && counts[2] == 3 && counts[3] == 3)) || fail 'not all instances answered'
  say "$found of 9 found; SHA-256 $source_hash"
  if [[ $MODE == before ]]; then ((found == 3)) || fail 'expected only the uploader to have the file'; else ((found == 9)) || fail 'expected all images'; fi
}
nine_reads
if [[ $MODE == after ]]; then
  step 'Legacy demo URLs work on S3 and API containers have no image-volume mounts'
  legacy=$(docker exec "$PREFIX-postgres" psql -X -At -U shop -d shop -c "SELECT image_path FROM products.products ORDER BY id LIMIT 1")
  [[ $(curl -s -o /dev/null -w '%{http_code}' "$URL/images/$legacy") == 200 ]] || fail 'legacy image URL'
  for n in 1 2 3; do
    [[ $(docker inspect -f '{{len .Mounts}}' "$PREFIX-api$n") == 0 ]] || fail "api$n has a mount"
  done
  say 'old demo URL: 200; all three API containers: zero mounts'
  step 'Recreate the uploader and SeaweedFS, retaining only the storage volume'
  docker rm -f "$PREFIX-api$owner" "$PREFIX-seaweed" >/dev/null
  start_storage
  wait_for SeaweedFS 90 storage_ready
  start_api "$owner"
  wait_for "recreated uploader" 60 listening "$owner"
  sleep 12 # Docker DNS cache and the passive upstream failure timeout
  nine_reads
  step 'Storage failure is 503 and cannot create a product; restart recovers'
  before=$(docker exec "$PREFIX-postgres" psql -X -At -U shop -d shop -c 'SELECT count(*) FROM products.products')
  docker stop "$PREFIX-seaweed" >/dev/null
  [[ $(upload) == 503 && $(jq -r .code "$WORK/upload.json") == common.service_unavailable ]] || fail 'upload outage response'
  [[ $(curl -s -o "$WORK/error.json" -w '%{http_code}' "$URL$path") == 503 ]] || fail 'read outage response'
  after=$(docker exec "$PREFIX-postgres" psql -X -At -U shop -d shop -c 'SELECT count(*) FROM products.products')
  [[ $before == "$after" ]] || fail 'outage created a product'
  say 'upload and image read: 503; database product count unchanged'
  docker start "$PREFIX-seaweed" >/dev/null
  wait_for SeaweedFS 90 storage_ready
  nine_reads
fi
step "R5-07 $MODE checks passed"
