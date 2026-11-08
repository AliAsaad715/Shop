#!/usr/bin/env bash
# Saves and restores the seeded databases for Requirement 3 (task R3-02b), so every step starts from the same data
# and test orders from one step never leak into the next. The app's and the warehouse's databases are saved together,
# because the app's orders and the warehouse's reservations must match.
#
#   perf/snapshot.sh save       once, right after seeding (R3-01)
#   perf/snapshot.sh restore    perf/run.sh does this before every label
#   perf/snapshot.sh status
#
# A snapshot is a copy of a database inside its own Postgres (CREATE DATABASE ... TEMPLATE), named <database>_snapshot,
# so saving and restoring take seconds and nothing leaves the Docker volumes. Copying needs a database nobody is
# connected to, so the api and inventory services stop meanwhile, and any other client, such as an IDE, is
# disconnected. Database-level settings (ALTER DATABASE ... SET) aren't copied; the project has none.
set -euo pipefail
cd "$(dirname "$0")/.."

DATABASES=(postgres inventory-db) # the compose services whose POSTGRES_DB is saved
CLIENTS=(inventory api)           # the services connected to them, in the order they start
REDIS_SERVICE=${REDIS_SERVICE:-redis}

# Runs SQL from stdin on a service's Postgres, connected to its maintenance database. The container's own settings
# name the user and the database; the psql variables db, snap and tmp carry the names into the SQL
sql() {
  local service=$1
  shift
  docker compose exec -T "$service" sh -c 'export PGOPTIONS="-c client_min_messages=warning"
    exec psql -X -q -At -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres \
      -v db="$POSTGRES_DB" -v snap="${POSTGRES_DB}_snapshot" -v tmp="${POSTGRES_DB}_restoring" "$@"' sh "$@"
}

# The databases must be up; the api and the warehouse only need containers, since save and restore start them
require_stack() {
  local running created service
  running=$(docker compose ps --status running --services)
  created=$(docker compose ps --all --services)
  for service in "${DATABASES[@]}"; do
    grep -qx "$service" <<<"$running" && continue
    echo "The $service service isn't running; start the stack first: docker compose up -d --wait" >&2
    exit 1
  done
  for service in "${CLIENTS[@]}"; do
    grep -qx "$service" <<<"$created" && continue
    echo "The $service service has no container; start the stack first: docker compose up -d --wait" >&2
    exit 1
  done
}

# Prints one line per database, and fails when either has no snapshot
status() {
  local service line missing=0
  for service in "${DATABASES[@]}"; do
    line=$(sql "$service" <<'SQL'
SELECT :'snap', coalesce((SELECT pg_size_pretty(pg_database_size(oid)) || ', ' || coalesce(shobj_description(oid, 'pg_database'), '')
                          FROM pg_database WHERE datname = :'snap'), 'none')
SQL
)
    printf '%-13s %s\n' "$service" "${line//|/: }"
    [[ $line == *'|none' ]] && missing=1
  done
  return $missing
}

stop_clients() {
  echo "Stopping ${CLIENTS[*]} while the databases are copied"
  docker compose --progress quiet stop "${CLIENTS[@]}"
  trap restart_after_failure EXIT
}

# When a copy fails, the app and the warehouse still come back, on whatever data the databases hold
restart_after_failure() {
  echo "Starting ${CLIENTS[*]} again after the failure above" >&2
  docker compose --progress quiet start "${CLIENTS[@]}" || true
}

# Starts the warehouse, then the app, and waits until both answer
start_clients() {
  local id state url
  docker compose --progress quiet start inventory
  id=$(docker compose ps -q inventory)
  # The health status, or plain "running" for an image without a healthcheck
  for _ in $(seq 60); do
    state=$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$id")
    [[ $state == healthy || $state == running ]] && break
    sleep 1
  done
  [[ $state == healthy || $state == running ]] || { echo "inventory isn't healthy after 60 s" >&2; exit 1; }

  docker compose --progress quiet start api
  url=${API_URL:-http://localhost:$(docker compose port api 8080 | sed 's/.*://')}
  for _ in $(seq 60); do
    if curl -fsS -o /dev/null "$url/health" 2>/dev/null; then
      trap - EXIT
      echo "The api and the warehouse are back"
      return 0
    fi
    sleep 1
  done
  echo "The api doesn't answer $url/health after 60 s" >&2
  exit 1
}

save() {
  require_stack
  local note service closed
  note="saved $(date -u +%Y-%m-%dT%H:%M:%SZ) at $(git describe --tags --always --dirty 2>/dev/null || echo unknown)"
  stop_clients
  for service in "${DATABASES[@]}"; do
    closed=$(sql "$service" -v note="$note" <<'SQL'
SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname IN (:'db', :'snap') AND pid <> pg_backend_pid();
DROP DATABASE IF EXISTS :"snap" WITH (FORCE);
CREATE DATABASE :"snap" TEMPLATE :"db" STRATEGY FILE_COPY;
COMMENT ON DATABASE :"snap" IS :'note';
SQL
)
    [[ $closed == 0 ]] || echo "Disconnected $closed other client(s) from $service"
  done
  start_clients
  status
}

# Copies the snapshot to a temporary database first, so the live one is only replaced once the copy has worked
restore() {
  require_stack
  local service
  if ! status >/dev/null; then
    status >&2 || true
    echo "No snapshot to restore: run perf/snapshot.sh save right after seeding" >&2
    exit 1
  fi

  stop_clients
  for service in "${DATABASES[@]}"; do
    sql "$service" <<'SQL'
SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = :'snap' AND pid <> pg_backend_pid() \g /dev/null
DROP DATABASE IF EXISTS :"tmp" WITH (FORCE);
CREATE DATABASE :"tmp" TEMPLATE :"snap" STRATEGY FILE_COPY;
DROP DATABASE IF EXISTS :"db" WITH (FORCE);
ALTER DATABASE :"tmp" RENAME TO :"db";
SQL
  done

  # A cache filled by the previous step would carry its state into the next one
  if docker compose ps --status running --services | grep -qx "$REDIS_SERVICE"; then
    docker compose exec -T "$REDIS_SERVICE" redis-cli FLUSHALL >/dev/null
    echo "Emptied $REDIS_SERVICE"
  fi

  start_clients
  echo "Restored the snapshot"
}

case ${1:-} in
  save) save ;;
  restore) restore ;;
  status) require_stack && status ;;
  *) echo "usage: perf/snapshot.sh save | restore | status" >&2; exit 2 ;;
esac
