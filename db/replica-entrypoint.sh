#!/usr/bin/env bash
# db-replica's entrypoint (task R3-10). On the first start, with no data yet, it copies the primary with pg_basebackup
# -R, which also writes standby.signal and how to reach the primary (primary_conninfo), so Postgres starts as a standby
# that streams the primary's WAL. On every later start the copy is there, and Postgres resumes where it stopped. Either
# way, the image's own entrypoint then starts Postgres.
set -euo pipefail

PRIMARY=${PRIMARY_HOST:-postgres}
SLOT=db_replica

if [[ ! -s $PGDATA/PG_VERSION ]]; then
  echo "db-replica: copying $PRIMARY with pg_basebackup"
  export PGPASSWORD=$POSTGRES_PASSWORD
  until pg_isready -q -h "$PRIMARY" -U "$POSTGRES_USER" -d "$POSTGRES_DB"; do
    sleep 1
  done

  # As the primary's superuser: a role that may replicate and nothing else, and no slot left from an earlier copy,
  # since this copy makes its own
  psql -X -q -v ON_ERROR_STOP=1 -h "$PRIMARY" -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
    -v password="$REPLICATION_PASSWORD" -v slot="$SLOT" <<'SQL'
SELECT 'CREATE ROLE replicator' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'replicator') \gexec
ALTER ROLE replicator WITH LOGIN REPLICATION PASSWORD :'password';
SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name = :'slot' AND NOT active \g /dev/null
SQL

  # The slot makes the primary keep the WAL the replica hasn't received yet, up to its max_slot_wal_keep_size.
  # pg_basebackup empties the folder again if the copy fails, so the next start copies afresh
  mkdir -p "$PGDATA"
  chown postgres:postgres "$PGDATA"
  chmod 700 "$PGDATA"
  PGPASSWORD=$REPLICATION_PASSWORD gosu postgres pg_basebackup --host="$PRIMARY" --username=replicator \
    --pgdata="$PGDATA" --write-recovery-conf --wal-method=stream --checkpoint=fast --create-slot --slot="$SLOT"
  echo "db-replica: copied; starting as a standby of $PRIMARY"
fi

exec docker-entrypoint.sh "$@"
