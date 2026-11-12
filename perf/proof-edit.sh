#!/usr/bin/env bash
# The plan's live-edit exhibit (task R3-12). An admin edits a product (PUT, R3-08), and the next request shows the new
# data with a new ETag at once, although product details come from the cache (R3-07); one more request with the new
# ETag is a 304 again (R3-09). The product is put back at the end. Run it against the running stack:
#
#   perf/proof-edit.sh | tee perf/results/edit-proof.txt
#
# The admin is ADMIN_EMAIL and ADMIN_PASSWORD, from the environment, else .env, else compose's defaults.
set -euo pipefail
cd "$(dirname "$0")/.."

API=${API_URL:-http://localhost:$(docker compose port api 8080 | sed 's/.*://')}
setting() { grep -E "^$1=" .env 2>/dev/null | tail -1 | cut -d= -f2- || true; }
email=${ADMIN_EMAIL:-$(setting ADMIN_EMAIL)}
password=${ADMIN_PASSWORD:-$(setting ADMIN_PASSWORD)}
email=${email:-admin@shop.local}
password=${password:-Admin123!}

body=$(mktemp)
trap 'rm -f -- "$body"' EXIT

# The product the load test views most: the first by ID
id=$(docker compose exec -T postgres sh -c \
  'psql -X -q -At -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "SELECT id FROM products.products ORDER BY id::text LIMIT 1"')
url="$API/api/v1/products/$id"

# One request, shown as its status, ETag and Cache-Control, and the body's fields that the edit changes
show() {
  echo "\$ curl -i${1:+ -H 'If-None-Match: $1'} \"\$API/api/v1/products/\$ID\""
  # Emptied first: curl writes nothing for a 304's missing body, and would leave the last body in place
  : >"$body"
  curl -sS -D - -o "$body" ${1:+-H "If-None-Match: $1"} "$url" | tr -d '\r' | grep -iE '^(HTTP/|etag:|cache-control:)'
  if [[ -s $body ]]; then
    jq -r '"body: \(.name), price \(.price), \(.available) in stock"' "$body"
  else
    echo "body: none"
  fi
}

etag() { curl -sS -D - -o /dev/null "$url" | tr -d '\r' | awk -F': ' 'tolower($1) == "etag" { print $2 }'; }

put() {
  curl -sS -o /dev/null -w '%{http_code}' -X PUT "$url" -H "Authorization: Bearer $token" \
    -H 'Content-Type: application/json' -d "$1"
}

echo "ID=$id"
echo
echo "1. Read twice: the second request sends the ETag back"
show
before=$(etag)
show "$before"

token=$(curl -sS "$API/api/v1/auth/login" -H 'Content-Type: application/json' \
  -d "$(jq -n --arg email "$email" --arg password "$password" '{email: $email, password: $password}')" | jq -r .accessToken)
curl -sS "$url" -o "$body"
original=$(jq -c '{name, description, price, categoryId}' "$body")
edited=$(jq -c '.price += 1' <<<"$original")

echo
echo "2. An admin raises the price by 1: PUT \$API/api/v1/products/\$ID -> $(put "$edited")"
echo
echo "3. The same request as before, with the old ETag: the new price and a new ETag, at once"
show "$before"
after=$(etag)
echo
echo "4. Once more, with the new ETag"
show "$after"

echo
echo "Put the product back: PUT -> $(put "$original")"
