#!/usr/bin/env bash
# The final runs of Requirement 3 (task R3-12). Every step is measured again on one machine, at one rate, with today's
# load test, snapshot and services, so the results table compares like with like. Only the app changes, built from the
# commit each step was tagged at:
#
#   step           commit             what the step adds
#   baseline       r3-rebaseline      nothing yet; R3-04's query counter runs only in Development
#   after-db       r3-before-redis    keyset pagination and fewer round trips (R3-05, R3-06)
#   after-redis    r3-after-redis     Redis and HybridCache (R3-07)
#   after-etag     r3-after-etag      ETags and 304s on product details (R3-09), on top of Redis
#   after-replica  r3-after-replica   the read replica, with read routing on (R3-10, R3-11)
#
#   perf/final.sh build          builds every step's API image, shop-api:<step>; the machine may be busy meanwhile
#   perf/final.sh run <rate>     measures every step with perf/run.sh, writes perf/results/comparison.md, then records
#                                the live-edit and live-replica exhibits (perf/proof-edit.sh, perf/proof-replica.sh)
#
# FROM=<step> starts at that step, to finish a batch that stopped partway; the steps before it keep their runs.
#
# A .gitignore rule kept five Orders files out of the commits before r3-before-redis, so those builds take them from
# 3f36a42, which restored them; every step then runs the same pending-release code. Every app is published in the SDK
# image the Dockerfile builds with, from this machine's NuGet cache, so the builds need no network; the image is the
# Dockerfile's runtime stage (perf/final.Dockerfile). The replica runs only for the step that uses it. Each step keeps its three latest valid runs at the rate, trying up to five. Measure on mains power
# with nothing else running: a step takes about 15 minutes. Needs Docker, git and jq.
set -euo pipefail
cd "$(dirname "$0")/.."

STEPS=(
  "baseline r3-rebaseline"
  "after-db r3-before-redis"
  "after-redis r3-after-redis"
  "after-etag r3-after-etag"
  "after-replica r3-after-replica"
)
RELEASES_FIX=3f36a42
RELEASES=src/Modules/Orders/Shop.Modules.Orders/Releases
SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0 # what src/Shop.Api/Dockerfile builds with
PACKAGES=${NUGET_PACKAGES:-$HOME/.nuget/packages}
KEEP=3     # valid runs a step needs
ATTEMPTS=5 # runs a step may take, invalid ones included

usage() {
  echo "usage: perf/final.sh build | run <rate>" >&2
  exit 2
}

# Builds each step's image from a throwaway worktree of its commit, so the checkout here never changes. The package
# cache is mounted read-only: a package it lacks fails the build, and a restore with network at that commit fixes it
build() {
  local step label ref
  tree=$(mktemp -d) # global, for the trap
  trap 'git worktree remove --force "$tree/app" 2>/dev/null || true; rmdir "$tree" 2>/dev/null || true' EXIT
  for step in "${STEPS[@]}"; do
    read -r label ref <<<"$step"
    git rev-parse -q --verify "$ref^{commit}" >/dev/null || { echo "There is no $ref; tag the step's commit first" >&2; exit 1; }
    git worktree add --quiet --detach "$tree/app" "$ref"
    if [[ -z $(git -C "$tree/app" ls-files "$RELEASES") ]]; then
      git -C "$tree/app" checkout --quiet "$RELEASES_FIX" -- "$RELEASES"
    fi

    echo "== $label: building $ref ($(git rev-parse --short "$ref^{commit}"))"
    if ! docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -e DOTNET_NOLOGO=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        -e NUGET_PACKAGES=/nuget -v "$PACKAGES:/nuget:ro" -v "$tree/app:/src" -w /src "$SDK_IMAGE" \
        dotnet publish src/Shop.Api/Shop.Api.csproj --configuration Release --output /src/publish \
        -p:UseAppHost=false -p:NuGetAudit=false >"$tree/publish.log" 2>&1; then
      grep -E "error|warning NU" "$tree/publish.log" | sort -u | head -20 >&2
      echo "Publishing $label failed; the full log is $tree/publish.log" >&2
      trap - EXIT
      exit 1
    fi
    docker build --quiet --file perf/final.Dockerfile --tag "shop-api:$label" \
      --label "shop.step=$label" --label "shop.commit=$(git rev-parse "$ref^{commit}")" "$tree/app" >/dev/null
    rm -f -- "$tree/publish.log"
    git worktree remove --force "$tree/app"
  done
  rmdir "$tree"
  trap - EXIT

  docker images --filter label=shop.step --format '{{.Repository}}:{{.Tag}} {{.ID}} {{.CreatedSince}}'
}

on_battery() {
  local online
  for online in /sys/class/power_supply/AC*/online /sys/class/power_supply/ADP*/online; do
    [[ -r $online ]] && [[ $(cat "$online") == 0 ]] && return 0
  done
  return 1
}

# A step's runs at the rate since this batch began: all of them, or the valid ones only
runs_since() {
  local label=$1 rate=$2 since=$3 filter=$4
  find "perf/results/$label" -name "$label-*Z.json" 2>/dev/null | xargs -r cat |
    jq -s --argjson rate "$rate" --arg since "$since" \
      "[.[] | select(.settings.rate == \$rate and .startedAt >= \$since) | select($filter)] | length"
}

run() {
  local rate=$1 since step label ref commit labels=() image
  [[ $rate =~ ^[1-9][0-9]*$ ]] || usage
  for step in "${STEPS[@]}"; do
    read -r label ref <<<"$step"
    docker image inspect "shop-api:$label" >/dev/null 2>&1 || { echo "No shop-api:$label image: perf/final.sh build" >&2; exit 1; }
    labels+=("$label")
  done
  for image in $(docker compose --profile replica config --images | sort -u); do
    [[ $image == shop-* ]] && continue
    docker image inspect "$image" >/dev/null 2>&1 && continue
    echo "The $image image isn't here, and pulling it in the middle of the runs would stall them: docker pull $image" >&2
    exit 1
  done

  local from=${FROM:-baseline} skipping=yes
  [[ " ${labels[*]} " == *" $from "* ]] || { echo "FROM must be one of: ${labels[*]}" >&2; exit 2; }

  since=$(date -u +%Y-%m-%dT%H:%M:%S)
  for step in "${STEPS[@]}"; do
    read -r label ref <<<"$step"
    [[ $label == "$from" ]] && skipping=no
    [[ $skipping == yes ]] && continue
    if on_battery; then
      echo "On battery: plug the charger in. On battery the CPU slows down and the runs collapse" >&2
      exit 1
    fi

    commit="$ref ($(git rev-parse --short "$ref^{commit}"))"
    echo
    echo "===== $label: $commit at $rate req/s"
    # Compose names the api's image shop-api; pointing that name at the step's image swaps the app and nothing else
    docker tag "shop-api:$label" shop-api:latest
    if [[ $label == after-replica ]]; then
      docker compose --profile replica up -d --no-build --wait db-replica
      READ_ROUTING_ENABLED=true docker compose up -d --no-build --wait
      READ_ROUTING_ENABLED=true docker compose up -d --no-build --no-deps --force-recreate --wait api
    else
      docker compose --profile replica stop db-replica
      READ_ROUTING_ENABLED=false docker compose up -d --no-build --wait
      READ_ROUTING_ENABLED=false docker compose up -d --no-build --no-deps --force-recreate --wait api
    fi

    RUNS=$KEEP COMMIT=$commit perf/run.sh "$label" "$rate" || true
    while (( $(runs_since "$label" "$rate" "$since" .valid) < KEEP && $(runs_since "$label" "$rate" "$since" true) < ATTEMPTS )); do
      echo "$label has $(runs_since "$label" "$rate" "$since" .valid) valid runs; one more"
      RUNS=1 COMMIT=$commit perf/run.sh "$label" "$rate" || true
    done
  done

  # Back to the default stack: today's app, with routing as .env says, and no replica
  docker compose --profile replica stop db-replica
  docker compose up -d --no-build --no-deps --force-recreate --wait api

  echo
  LABELS=$(IFS=,; echo "${labels[*]}") perf/compare.sh

  # The plan's other two exhibits, on today's app with every service up
  echo
  perf/proof-edit.sh >perf/results/edit-proof.txt && echo "Saved perf/results/edit-proof.txt" ||
    echo "The live-edit exhibit failed; run perf/proof-edit.sh by hand" >&2
  perf/proof-replica.sh >perf/results/replica-proof.txt && echo "Saved perf/results/replica-proof.txt" ||
    echo "The live-replica exhibit failed; run perf/proof-replica.sh by hand" >&2
}

case ${1:-} in
  build) build ;;
  run) [[ $# -eq 2 ]] || usage; run "$2" ;;
  *) usage ;;
esac
