#!/usr/bin/env bash
# Loads the Requirement 3 dataset (task R3-01) into the running stack: perf/seed.sql into the app's database, then
# plenty of stock for every seeded product into the warehouse (tests/proof/app-seed.sql). Every run makes the same
# rows. Run it once, on a database the api has migrated, then save the snapshot every step restores:
#
#   perf/seed.sh && perf/snapshot.sh save
#
# Seeded customers are customer1@shop.test to customer50000@shop.test, all with the password Shopper123!.
set -euo pipefail
cd "$(dirname "$0")/.."

# Runs psql on a service's own database, with the user and database name from the container's settings
db() {
  local service=$1
  shift
  docker compose exec -T "$service" sh -c \
    'exec psql -X -q -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" "$@"' sh "$@"
}

if ! seeded=$(db postgres -At -c "SELECT count(*) FROM products.products WHERE id = md5('product-1')::uuid" 2>/dev/null); then
  echo "The app's tables aren't there yet: start the stack with docker compose up -d --wait, so the api creates them" >&2
  exit 1
fi

if [[ $seeded != 0 ]]; then
  echo "The app's database is already seeded. To start again from the seed, run perf/snapshot.sh restore;" \
    "for an empty database, docker compose down -v" >&2
  exit 1
fi

echo "Seeding the app's database, which takes a few minutes"
start=$(date +%s)
db postgres <perf/seed.sql
echo "Stocking the warehouse"
db inventory-db <tests/proof/app-seed.sql
# The six demo products too: the load test orders whatever the catalog lists, and the R2 proof's product X (one unit)
# and sold-out shoes would turn its orders into 409s. tests/proof/README.md shows how to reseed them for the proof
db inventory-db -c 'UPDATE stock_levels SET available = 1000000 WHERE available < 1000000'
echo "Seeded in $(($(date +%s) - start)) s:"

db postgres <<'SQL'
SELECT (SELECT count(*) FROM users.user_roles ur JOIN users.roles r ON r.id = ur.role_id WHERE r.name = 'Customer') AS customers,
       (SELECT count(*) FROM products.categories) AS categories,
       (SELECT count(*) FROM products.products) AS products,
       (SELECT count(*) FROM orders.orders) AS orders,
       (SELECT count(*) FROM orders.order_lines) AS order_lines,
       pg_size_pretty(pg_database_size(current_database())) AS size;
SQL
echo "Next: perf/snapshot.sh save"
