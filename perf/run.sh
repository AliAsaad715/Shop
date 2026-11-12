#!/usr/bin/env bash
# Runs one step of Requirement 3 (task R3-02b): runs perf/load.js three times at a fixed rate, each from the seeded
# snapshot, and saves next to each k6 result what the system did while it was measured: CPU per container, database
# connections, the top queries and Redis hits. perf/compare.sh turns the runs into the before/after report.
#
#   perf/run.sh <label> <rate>        e.g. perf/run.sh baseline 200
#
# Results go to perf/results/<label>/. RUNS sets how many runs (3); WARMUP, DURATION, CUSTOMER_EMAILS,
# CUSTOMER_PASSWORD, CUSTOMERS, ETAGS and ONLY pass through to perf/load.js, with five seeded customers unless set.
# Needs Docker and jq.
set -euo pipefail
set -m # each background job gets its own process group, so stopping a sampler stops everything it started
cd "$(dirname "$0")/.."

K6_IMAGE=grafana/k6:2.3.0
K6_CONTAINER=shop-perf-k6
REDIS_SERVICE=${REDIS_SERVICE:-redis}
REPLICA_SERVICE=${REPLICA_SERVICE:-db-replica}
RUNS=${RUNS:-3}
SAMPLE_SECONDS=2
# The seeded customers every step uses (perf/seed.sql): the two with the most orders, and three typical ones
CUSTOMER_EMAILS=${CUSTOMER_EMAILS:-customer1@shop.test,customer2@shop.test,customer100@shop.test,customer5000@shop.test,customer40000@shop.test}
CUSTOMER_PASSWORD=${CUSTOMER_PASSWORD:-Shopper123!}

if [[ $# -ne 2 ]]; then
  echo "usage: perf/run.sh <label> <rate>   e.g. perf/run.sh baseline 200" >&2
  exit 2
fi

label=$1
rate=$2
if [[ ! $label =~ ^[A-Za-z0-9._-]+$ ]]; then
  echo "The label may hold only letters, digits, dots, dashes and underscores" >&2
  exit 2
fi

if [[ ! $rate =~ ^[0-9]+([.][0-9]+)?$ ]] || ! awk -v r="$rate" 'BEGIN { exit !(r > 0) }'; then
  echo "The rate is requests per second: a number above 0" >&2
  exit 2
fi

if [[ ! $RUNS =~ ^[1-9][0-9]*$ ]]; then
  echo "RUNS must be a whole number above 0" >&2
  exit 2
fi

command -v jq >/dev/null || { echo "perf/run.sh needs jq: sudo apt install jq, or brew install jq" >&2; exit 1; }

# The results name the commit; the plan runs every step on a tagged one
# COMMIT names it instead when the app under test was built from another commit, as perf/final.sh does (task R3-12)
if [[ -n ${COMMIT:-} ]]; then
  commit=$COMMIT
else
  commit=$(git describe --tags --always --dirty)
  git describe --tags --exact-match >/dev/null 2>&1 || echo "Note: HEAD has no tag; tag each step's commit (git tag r3-$label)"
  [[ $commit != *-dirty ]] || echo "Note: there are uncommitted changes, so the results name $commit"
fi

dir=perf/results/$label
mkdir -p "$dir"
tmp=$(mktemp -d)
samplers=()
stop_samplers() {
  local pid
  for pid in ${samplers[@]+"${samplers[@]}"}; do
    kill -- "-$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
  done
  samplers=()
}

cleanup() {
  stop_samplers
  docker rm -f "$K6_CONTAINER" >/dev/null 2>&1 || true
  rm -rf -- "${tmp:?}"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

# Runs SQL on the app's database: the -c argument, or stdin
app_sql() {
  database_sql postgres "$@"
}

database_sql() {
  local service=$1
  shift
  docker compose --profile replica exec -T "$service" sh -c \
    'exec psql -X -q -At -F "|" -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" "$@"' sh "$@"
}

redis_running() {
  docker compose ps --status running --services | grep -qx "$REDIS_SERVICE"
}

replica_running() {
  docker compose --profile replica ps --status running --services | grep -qx "$REPLICA_SERVICE"
}

# Every container's CPU, as "<epoch> <container> <percent of one core>" lines, until killed
sample_cpu() {
  local out now
  while :; do
    out=$(docker stats --no-stream --format '{{.Name}} {{.CPUPerc}}' 2>/dev/null) || out=
    now=$(date +%s)
    [[ -z $out ]] || awk -v t="$now" '{ sub(/%$/, "", $2); print t, $1, $2 }' <<<"$out"
    sleep "$SAMPLE_SECONDS"
  done
}

# The app's database connections, as "<epoch>|<all>|<active>|<waiting>|<wait events>" lines, until killed
CONNECTIONS_SQL="SELECT extract(epoch FROM clock_timestamp())::bigint, count(*),
  count(*) FILTER (WHERE state = 'active'),
  count(*) FILTER (WHERE state = 'active' AND wait_event_type IS NOT NULL),
  coalesce(string_agg(wait_event_type || ':' || wait_event, ',') FILTER (WHERE state = 'active' AND wait_event_type IS NOT NULL), '')
  FROM pg_stat_activity WHERE backend_type = 'client backend' AND datname = current_database() AND pid <> pg_backend_pid()"

# Starts both samplers in the background. The database one keeps a single session for the whole run, opened before
# the load starts: once the app's pool takes every connection Postgres allows, a new session couldn't get in, and the
# samples would stop exactly when they matter most
start_samplers() {
  sample_cpu >"$tmp/cpu.txt" 2>/dev/null &
  samplers=($!)
  (printf '%s\n\\watch %s\n' "$CONNECTIONS_SQL" "$SAMPLE_SECONDS" | app_sql) >"$tmp/db.txt" 2>/dev/null &
  samplers+=($!)
  if replica_running; then
    (printf '%s\n\\watch %s\n' "$CONNECTIONS_SQL" "$SAMPLE_SECONDS" | database_sql "$REPLICA_SERVICE") >"$tmp/replica.txt" 2>/dev/null &
    samplers+=($!)
  fi
}

# Counts start from zero for each run, where the counters exist: pg_stat_statements since task R3-04, Redis with R3-07
reset_counters() {
  if perf/top-queries.sh reset >/dev/null 2>&1; then statements=yes; else statements=no; fi
  replica_statements=no
  if replica_running && DATABASE_SERVICE="$REPLICA_SERVICE" perf/top-queries.sh reset >/dev/null 2>&1; then replica_statements=yes; fi
  if redis_running; then
    docker compose exec -T "$REDIS_SERVICE" redis-cli CONFIG RESETSTAT >/dev/null
  fi
}

# The newest k6 result of this label, if any
newest_result() {
  local files=("$dir/$label"-*Z.json)
  [[ -e ${files[${#files[@]} - 1]} ]] && echo "${files[${#files[@]} - 1]}"
  return 0
}

# Summarizes either server's connection samples within the measured window.
database_stats() {
  jq -n -R --argjson from "$2" --argjson to "$3" '
    [inputs | split("|") | select(length == 5)
      | {t: (.[0] | tonumber), all: (.[1] | tonumber), active: (.[2] | tonumber), waiting: (.[3] | tonumber),
         waits: (.[4] | if . == "" then [] else split(",") end)}]
    | map(select(.t >= $from and .t <= $to))
    | if length == 0 then null else {
        maxConnections: (map(.all) | max), avgActive: (map(.active) | add / length * 10 | round / 10),
        maxActive: (map(.active) | max), maxWaiting: (map(.waiting) | max),
        waitEvents: ([.[].waits[]] | group_by(.) | map({key: .[0], value: length}) | sort_by(-.value) | from_entries)
      } end' <"$1"
}

# Writes <result>.system.json: what the system did between the result's measuredFrom and measuredTo.
write_system() {
  local result=$1 from to cpu database queries redis replica=null replica_queries=null notes='[]'
  from=$(jq '.window.measuredFrom | sub("\\.[0-9]+Z$"; "Z") | fromdateiso8601' "$result")
  to=$(jq '.window.measuredTo | sub("\\.[0-9]+Z$"; "Z") | fromdateiso8601' "$result")

  cpu=$(jq -n -R --argjson from "$from" --argjson to "$to" --argjson names "$names" '
    [inputs | split(" ") | select(length == 3) | {t: (.[0] | tonumber), service: $names[.[1]], cpu: (.[2] | tonumber? // null)}]
    | map(select(.t >= $from and .t <= $to and .service != null and .cpu != null))
    | group_by(.service)
    | map({key: .[0].service, value: {avgPercent: (map(.cpu) | add / length * 10 | round / 10),
                                       maxPercent: (map(.cpu) | max * 10 | round / 10)}})
    | from_entries' <"$tmp/cpu.txt")

  database=$(database_stats "$tmp/db.txt" "$from" "$to")
  if [[ -f $tmp/replica.txt ]]; then replica=$(database_stats "$tmp/replica.txt" "$from" "$to"); fi
  if [[ $replica_statements == yes ]]; then replica_queries=$(DATABASE_SERVICE="$REPLICA_SERVICE" perf/top-queries.sh json 10); fi

  if [[ $statements == yes ]]; then
    queries=$(perf/top-queries.sh json 10)
  else
    queries=null
    notes=$(jq -c '. + ["Postgres has not loaded pg_stat_statements (perf/top-queries.sh says how), so there are no top queries"]' <<<"$notes")
  fi

  if redis_running; then
    redis=$(docker compose exec -T "$REDIS_SERVICE" redis-cli INFO stats | tr -d '\r' | jq -R -s '
      [split("\n")[] | select(test("^keyspace_(hits|misses):")) | split(":") | {(.[0]): (.[1] | tonumber)}] | add
      | {hits: .keyspace_hits, misses: .keyspace_misses,
         hitRatio: (if .keyspace_hits + .keyspace_misses > 0
                    then .keyspace_hits / (.keyspace_hits + .keyspace_misses) * 1000 | round / 1000 else null end)}')
  else
    redis=null
    notes=$(jq -c --arg service "$REDIS_SERVICE" '. + ["no \($service) service (task R3-07), so there are no cache hits"]' <<<"$notes")
  fi

  jq -n --arg label "$label" --argjson run "$run" --arg result "$(basename "$result")" --arg commit "$commit" \
    --arg snapshot "$snapshot" --argjson cores "$cores" --argjson sampleSeconds "$SAMPLE_SECONDS" \
    --slurpfile k6 "$result" --argjson cpu "$cpu" --argjson database "$database" --argjson topQueries "$queries" \
    --argjson redis "$redis" --argjson notes "$notes" --argjson replica "$replica" --argjson replicaQueries "$replica_queries" '
    {label: $label, run: $run, result: $result, commit: $commit, snapshot: $snapshot, window: $k6[0].window,
     sampleSeconds: $sampleSeconds, cpu: {cores: $cores, containers: $cpu}, database: $database,
     topQueries: $topQueries, replica: {database: $replica, topQueries: $replicaQueries}, redis: $redis, notes: $notes}' >"${result%.json}.system.json"
}

echo "Step \"$label\" at $commit: $RUNS run$([[ $RUNS -eq 1 ]] || echo s) at $rate req/s, each from the seeded snapshot"
docker rm -f "$K6_CONTAINER" >/dev/null 2>&1 || true # left behind by a run that crashed
perf/snapshot.sh restore

api=${API_URL:-http://localhost:$(docker compose port api 8080 | sed 's/.*://')}
cores=$(docker info --format '{{.NCPU}}')
snapshot=$(app_sql -c "SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname = current_database() || '_snapshot'")
# Container names to compose services, so the results say "api" rather than "shop-api-1"
names=$(docker compose --profile replica ps --format json | jq -s --arg k6 "$K6_CONTAINER" 'flatten | map({(.Name): .Service}) | add + {($k6): "k6"}')

k6_env=(-e "API_URL=$api" -e "RATE=$rate" -e "LABEL=$label" -e "COMMIT=$commit" -e "RESULTS_DIR=$dir")
for name in WARMUP DURATION CUSTOMER_EMAILS CUSTOMER_PASSWORD CUSTOMERS ETAGS ONLY; do
  if [[ -n ${!name:-} ]]; then
    k6_env+=(-e "$name=${!name}")
  fi
done

# A terminal gets k6's live progress bar, and Ctrl+C reaches k6, which stops and still saves what it measured.
# Elsewhere, such as a log file, the progress would print every second, so k6 runs quiet
tty=(-i)
quiet=(--quiet)
if [[ -t 0 && -t 1 ]]; then
  tty=(-it)
  quiet=()
fi

valid=0
for ((run = 1; run <= RUNS; run++)); do
  echo
  echo "== $label: run $run of $RUNS =="
  # Every run starts from the seeded data. A run creates about 10,000 orders, all for the five customers that read
  # "my orders", so without this their histories, and that endpoint's work, would grow from run to run
  if [[ $run -gt 1 ]]; then
    perf/snapshot.sh restore
  fi

  reset_counters
  previous=$(newest_result)
  start_samplers

  set +e
  docker run --rm "${tty[@]}" --name "$K6_CONTAINER" --network host -u "$(id -u):$(id -g)" \
    -v "$PWD/perf:/shop/perf" -w /shop "$K6_IMAGE" run ${quiet[@]+"${quiet[@]}"} "${k6_env[@]}" perf/load.js
  k6_exit=$?
  set -e
  stop_samplers

  # 0 is a valid run and 99 an invalid one, whose result says why; anything else (setup failed, Ctrl+C) stops here
  if [[ $k6_exit -ne 0 && $k6_exit -ne 99 ]]; then
    echo "k6 stopped with exit code $k6_exit, so no more runs start" >&2
    exit 1
  fi

  result=$(newest_result)
  if [[ -z $result || $result == "$previous" ]]; then
    echo "k6 saved no result, so no more runs start" >&2
    exit 1
  fi

  write_system "$result"
  jq -r --slurpfile system "${result%.json}.system.json" '$system[0] as $s
    | "Run \($s.run): " + (if .valid then "valid" else "NOT valid (\(.problems | join("; ")))" end)
      + ". p50 \(.overall.p50Ms) ms, p95 \(.overall.p95Ms) ms, p99 \(.overall.p99Ms) ms. CPU "
      + ([$s.cpu.containers | to_entries[] | "\(.key) \(.value.avgPercent)%"] | join(", "))
      + (if $s.database then ". Database: up to \($s.database.maxConnections) connections, \($s.database.maxWaiting) waiting" else "" end)
      + (if $s.redis.hitRatio then ". Redis keyspace hit ratio \($s.redis.hitRatio)" else "" end)' "$result"
  echo "Saved ${result%.json}.system.json"
  if jq -e .valid "$result" >/dev/null; then
    valid=$((valid + 1))
  fi
done

echo
echo "$valid of $RUNS runs valid, in $dir/. perf/compare.sh builds the before/after report."
[[ $valid -eq $RUNS ]]
