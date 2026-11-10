#!/usr/bin/env bash
# Shows the plan Postgres actually runs for a query, with real times and buffers (task R3-05): the query comes on stdin,
# and the output is the query followed by its EXPLAIN (ANALYZE, BUFFERS). The query runs twice and the second plan is
# kept, so a cold cache doesn't decide what the plan looks like. Used for the plans in perf/plans/:
#
#   perf/explain.sh < query.sql > perf/plans/<name>.before.txt
#
# ANALYZE really runs the query, so EXPLAIN only reads: never an INSERT, UPDATE or DELETE.
set -euo pipefail
cd "$(dirname "$0")/.."

query=$(cat)
if [[ $query =~ (^|[^[:alnum:]_])(INSERT|UPDATE|DELETE|TRUNCATE|DROP|ALTER|CREATE)([^[:alnum:]_]|$) ]]; then
  echo "perf/explain.sh only explains queries that read; ANALYZE would run this one for real" >&2
  exit 2
fi

query=${query%;}
echo "$query;"
echo
docker compose exec -T postgres sh -c 'exec psql -X -q -At -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' <<SQL | tail -n +2
EXPLAIN (ANALYZE, BUFFERS) $query \g /dev/null
SELECT '';
EXPLAIN (ANALYZE, BUFFERS) $query;
SQL
