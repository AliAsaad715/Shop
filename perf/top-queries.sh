#!/usr/bin/env bash
# Lists the app's costliest queries from pg_stat_statements (task R3-04): the statements with the most total time in
# the app's database since the counts were last reset. perf/run.sh resets them before every run and saves the top
# ten beside the k6 result; after any load, this shows where the database time went.
#
#   perf/top-queries.sh [N]        the top N (10), each with its full text, ready for EXPLAIN
#   perf/top-queries.sh reset      starts the counts again from zero
#   perf/top-queries.sh json [N]   the same as JSON, for perf/run.sh
#
# Postgres loads pg_stat_statements at startup (compose.yaml). The extension lives in the maintenance database
# "postgres", not the app's, so the app's schema and the seeded snapshot stay as they are.
set -euo pipefail
cd "$(dirname "$0")/.."
DATABASE_SERVICE=${DATABASE_SERVICE:-postgres}

# psql on the maintenance database; the psql variable app names the app's database
sql() {
  docker compose --profile replica exec -T "$DATABASE_SERVICE" sh -c 'export PGOPTIONS="-c client_min_messages=warning"
    exec psql -X -q -At -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres -v app="$POSTGRES_DB" "$@"' sh "$@"
}

# The library must have been loaded when Postgres started; the extension is made the first time it's needed
prepare() {
  if [[ $(sql -c "SELECT current_setting('shared_preload_libraries')") != *pg_stat_statements* ]]; then
    echo "Postgres hasn't loaded pg_stat_statements. Recreate it with the setting in compose.yaml:" \
      "docker compose up -d postgres" >&2
    exit 1
  fi

  if [[ $DATABASE_SERVICE == postgres ]]; then
    sql -c 'CREATE EXTENSION IF NOT EXISTS pg_stat_statements'
  elif [[ $(sql -c "SELECT count(*) FROM pg_extension WHERE extname = 'pg_stat_statements'") != 1 ]]; then
    echo "Create pg_stat_statements on the primary and wait for it to replay before inspecting the replica" >&2
    exit 1
  fi
}

# The app's statements, ranked by total time. The perf tools' own queries, which read the statistics views, are left
# out, so only what the app asked for remains
RANKED="WITH s AS (
  SELECT calls, total_exec_time, mean_exec_time, rows, regexp_replace(query, '[[:space:]]+', ' ', 'g') AS query
  FROM pg_stat_statements
  WHERE dbid = (SELECT oid FROM pg_database WHERE datname = :'app')
    AND query !~ 'pg_(stat_activity|database|stat_statements)'
)
SELECT row_number() OVER (ORDER BY total_exec_time DESC) AS n, sum(total_exec_time) OVER () AS all_ms, s.*
FROM s"

count() {
  local n=${1:-10}
  if [[ ! $n =~ ^[1-9][0-9]*$ ]]; then
    echo "N must be a whole number above 0" >&2
    exit 2
  fi
  echo "$n"
}

case ${1:-} in
  reset)
    prepare
    sql -c 'SELECT pg_stat_statements_reset()' >/dev/null
    ;;
  json)
    limit=$(count "${2:-}")
    prepare
    sql -v limit="$limit" <<SQL
SELECT coalesce(json_agg(json_build_object('calls', calls, 'totalMs', round(total_exec_time::numeric, 1),
  'meanMs', round(mean_exec_time::numeric, 3), 'rows', rows, 'share', round((total_exec_time / nullif(all_ms, 0))::numeric, 4),
  'query', query) ORDER BY n), '[]')
FROM ($RANKED) ranked WHERE n <= :limit
SQL
    ;;
  '' | [0-9]*)
    limit=$(count "${1:-}")
    prepare
    sql -v limit="$limit" <<SQL
SELECT format('Since %s: %s ms of statements in the database %s',
              to_char(stats_reset AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS "UTC"'),
              (SELECT round(coalesce(max(all_ms), 0)::numeric, 1) FROM ($RANKED) ranked), :'app')
FROM pg_stat_statements_info;
SELECT format(E'\n%s. %s%% of the time: %s calls, %s ms each, %s ms in all, %s rows\n%s', n,
              round((100 * total_exec_time / nullif(all_ms, 0))::numeric, 1), calls, round(mean_exec_time::numeric, 3),
              round(total_exec_time::numeric, 1), rows, query)
FROM ($RANKED) ranked WHERE n <= :limit ORDER BY n;
SQL
    ;;
  *)
    echo "usage: perf/top-queries.sh [N] | reset | json [N]" >&2
    exit 2
    ;;
esac
