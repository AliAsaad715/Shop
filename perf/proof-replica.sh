#!/usr/bin/env bash
# The plan's live-replica exhibit (task R3-12). With the replica replaying 5 seconds late and read routing on (R3-11),
# a customer's new order still shows at once in "my orders": a customer who has just written reads from the primary.
# The replica itself only has the order 5 seconds later. Run it against the running stack:
#
#   perf/proof-replica.sh | tee perf/results/replica-proof.txt
#
# It recreates db-replica with REPLICA_APPLY_DELAY=5s and the api with READ_ROUTING_ENABLED=true, and puts both back
# at the end: no delay, and routing as .env says. The customer is a seeded one (perf/seed.sql).
set -euo pipefail
cd "$(dirname "$0")/.."

API=${API_URL:-http://localhost:$(docker compose port api 8080 | sed 's/.*://')}
email=${CUSTOMER_EMAIL:-customer100@shop.test}
password=${CUSTOMER_PASSWORD:-Shopper123!}

restore() {
  docker compose --profile replica up -d --no-build --wait db-replica >/dev/null 2>&1
  docker compose up -d --no-build --no-deps --force-recreate --wait api >/dev/null 2>&1
}
trap restore EXIT

healthy() {
  for _ in $(seq 60); do
    curl -fs -o /dev/null "$API/health" && return 0
    sleep 1
  done
  echo "The api doesn't answer $API/health after 60 s" >&2
  exit 1
}

replica_rows() {
  docker compose --profile replica exec -T db-replica sh -c \
    'psql -X -q -At -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v id="$1"' sh "$1" <<'SQL'
SELECT count(*) FROM orders.orders WHERE id = :'id'::uuid
SQL
}

since() { echo "$(date +%s.%N) - $1" | bc | awk '{ printf "%.2f", $1 }'; }

echo "The replica replays every change 5 s late, and the api sends a customer's reads to the primary for a while after they write"
REPLICA_APPLY_DELAY=5s docker compose --profile replica up -d --no-build --force-recreate --wait db-replica >/dev/null 2>&1
READ_ROUTING_ENABLED=true docker compose up -d --no-build --no-deps --force-recreate --wait api >/dev/null 2>&1
healthy
echo "replica apply delay: $(docker compose --profile replica exec -T db-replica sh -c \
  'psql -X -q -At -U "$POSTGRES_USER" -d postgres -c "SHOW recovery_min_apply_delay"')"

token=$(curl -sS "$API/api/v1/auth/login" -H 'Content-Type: application/json' \
  -d "$(jq -n --arg email "$email" --arg password "$password" '{email: $email, password: $password}')" | jq -r .accessToken)
product=$(docker compose exec -T postgres sh -c \
  'psql -X -q -At -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "SELECT id FROM products.products ORDER BY id::text LIMIT 1"')

start=$(date +%s.%N)
order=$(curl -sS "$API/api/v1/orders" -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
  -d "$(jq -n --arg id "$product" '{lines: [{productId: $id, quantity: 1}]}')" | jq -r .id)
echo
echo "$email places order $order"

listed=$(curl -sS "$API/api/v1/orders?page=1&pageSize=20" -H "Authorization: Bearer $token" |
  jq --arg id "$order" 'any(.items[]; .id == $id)')
echo "after $(since "$start") s: in \"my orders\": $listed; rows on the replica itself: $(replica_rows "$order")"

while [[ $(replica_rows "$order") != 1 ]]; do
  sleep 0.2
done
echo "after $(since "$start") s: the replica has it too"
echo
echo "Putting back: no apply delay, routing as .env says"
