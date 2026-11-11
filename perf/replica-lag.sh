#!/usr/bin/env bash
# How far db-replica is behind the primary (task R3-10). Each check first writes a marker to the primary's WAL, a
# logical decoding message that changes no table, so the replica has something fresh to replay even when nothing
# else writes. Then the primary's view, pg_stat_replication: the WAL the replica hasn't replayed yet, and how long the
# latest changes took to reach it (write), to reach its disk (flush) and to show in its reads (replay).
#
#   perf/replica-lag.sh          the primary's view, the slot that keeps WAL for the replica, and the replica's view
#   perf/replica-lag.sh watch    a line a second until Ctrl+C, for instance while perf/run.sh runs
#
# Needs the replica: docker compose --profile replica up -d --wait
set -euo pipefail
cd "$(dirname "$0")/.."

REPLICA_SERVICE=${REPLICA_SERVICE:-db-replica}

# Runs SQL from stdin, or from -c, on a service's maintenance database as its superuser
sql() {
  local service=$1
  shift
  docker compose --profile replica exec -T "$service" sh -c \
    'exec psql -X -q -v ON_ERROR_STOP=1 -P footer=off -U "$POSTGRES_USER" -d postgres "$@"' sh "$@"
}

if ! docker compose --profile replica ps --status running --services | grep -qx "$REPLICA_SERVICE"; then
  echo "$REPLICA_SERVICE isn't running; start it with: docker compose --profile replica up -d --wait" >&2
  exit 1
fi

# An interval in milliseconds, or "-" when the primary has no recent measurement
MS="coalesce(round(extract(epoch FROM %s) * 1000, 1)::text || ' ms', '-')"
lag() { printf "$MS" "$1"; }

case ${1:-} in
  '')
    marker=$(sql postgres -At -c "SELECT pg_logical_emit_message(true, 'replica-lag', '')")
    # Up to 10 s for the replica to report the marker replayed, so the lags below include it
    for _ in $(seq 100); do
      [[ $(sql postgres -At -c "SELECT coalesce(bool_and(replay_lsn >= '$marker'), false) FROM pg_stat_replication") == t ]] && break
      sleep 0.1
    done

    echo "The primary's view (pg_stat_replication)"
    sql postgres <<SQL
SELECT application_name AS replica, client_addr, state, sync_state,
       pg_size_pretty(pg_wal_lsn_diff(pg_current_wal_lsn(), replay_lsn)) AS not_replayed,
       $(lag write_lag) AS write_lag, $(lag flush_lag) AS flush_lag, $(lag replay_lag) AS replay_lag
FROM pg_stat_replication;
SQL
    echo "The slot that keeps WAL on the primary until the replica has it"
    sql postgres <<'SQL'
SELECT slot_name, active, wal_status, pg_size_pretty(pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn)) AS wal_kept,
       pg_size_pretty(safe_wal_size) AS before_it_is_lost
FROM pg_replication_slots;
SQL
    echo "The replica's view"
    sql "$REPLICA_SERVICE" <<'SQL'
SELECT pg_is_in_recovery() AS standby, pg_last_wal_receive_lsn() AS received, pg_last_wal_replay_lsn() AS replayed,
       pg_size_pretty(pg_wal_lsn_diff(pg_last_wal_receive_lsn(), pg_last_wal_replay_lsn())) AS received_not_replayed,
       current_setting('recovery_min_apply_delay') AS apply_delay;
SQL
    ;;
  watch)
    echo "time      replica     state      not replayed  write lag   flush lag   replay lag"
    while :; do
      sql postgres -At <<SQL
SELECT pg_logical_emit_message(true, 'replica-lag', '') \g /dev/null
SELECT format('%-9s %-11s %-10s %-13s %-11s %-11s %s', to_char(clock_timestamp(), 'HH24:MI:SS'),
              coalesce(application_name, '-'), coalesce(state, 'none'),
              coalesce(pg_size_pretty(pg_wal_lsn_diff(pg_current_wal_lsn(), replay_lsn)), '-'),
              $(lag write_lag), $(lag flush_lag), $(lag replay_lag))
FROM (SELECT 1) AS always LEFT JOIN pg_stat_replication ON true;
SQL
      sleep 1
    done
    ;;
  *)
    echo "usage: perf/replica-lag.sh [watch]" >&2
    exit 2
    ;;
esac
