#!/usr/bin/env bash
# R5-09: validate every configured pool, including read-pool failover to primary, then ask BOTH real servers.
# --config-only <resolved-compose.json> validates a configuration without a running stack.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
fail() { printf 'FAILED: %s\n' "$*" >&2; exit 1; }
command -v jq >/dev/null || fail 'jq is required'
config_only=false
if [[ ${1:-} == --config-only && $# == 2 ]]; then
  config_only=true
  config=$(cat "$2")
elif (($# == 0)); then
  config=$(docker compose --profile replica config --format json)
else
  fail 'usage: connection-budget.sh [--config-only resolved-compose.json]'
fi
report=$(jq '
  def pool: (try (capture("(?i)(?:^|;)\\s*(?:Maximum Pool Size|MaxPoolSize)\\s*=\\s*(?<n>[0-9]+)").n | tonumber) catch 100) // 100;
  def maximum: [.command[] | select(test("^max_connections=")) | split("=")[1] | tonumber][0] // 100;
  .services as $s |
  ["api1", "api2", "api3" | {instance: ., write: ($s[.].environment.ConnectionStrings__Shop | pool),
    read: ($s[.].environment.ConnectionStrings__ShopRead | pool)}] as $apis |
  ($s.worker.deploy.replicas // 1) as $workers |
  ($s.worker.environment.ConnectionStrings__Shop | pool) as $workerPool |
  {apis: $apis, workers: $workers, workerPool: $workerPool,
   migratorPool: ($s.migrator.environment.ConnectionStrings__Shop | pool),
   primaryWorstCase: (($apis | map(.write + .read) | add) + $workers * $workerPool),
   replicaWorstCase: ($apis | map(.read) | add), headroom: 10,
   primaryConfiguredMax: ($s.postgres | maximum), replicaConfiguredMax: ($s["db-replica"] | maximum)}
' <<<"$config")
jq -e '(.apis | all(.write > 0 and .read > 0)) and .workerPool > 0 and .migratorPool > 0 and
  (.primaryWorstCase + .headroom <= .primaryConfiguredMax) and
  (.replicaWorstCase + .headroom <= .replicaConfiguredMax) and
  (.migratorPool + .headroom <= .primaryConfiguredMax)' <<<"$report" >/dev/null ||
  fail "configured connection budget exceeds the server limit: $report"

if [[ $config_only == false ]]; then
  maximum() {
    docker compose --profile replica exec -T "$1" sh -c \
      'exec psql -X -q -At -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "SHOW max_connections"'
  }
  primary=$(maximum postgres)
  replica=$(maximum db-replica)
  [[ $primary =~ ^[0-9]+$ && $replica =~ ^[0-9]+$ ]] || fail 'could not read max_connections on both servers'
  report=$(jq --argjson primary "$primary" --argjson replica "$replica" \
    '. + {primaryActualMax: $primary, replicaActualMax: $replica}' <<<"$report")
  jq -e '.primaryWorstCase + .headroom <= .primaryActualMax and
    .replicaWorstCase + .headroom <= .replicaActualMax and
    .migratorPool + .headroom <= .primaryActualMax' <<<"$report" >/dev/null ||
    fail "running servers do not have room for the configured pools: $report"
fi
printf '%s\n' "$report"
