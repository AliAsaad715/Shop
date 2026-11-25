#!/usr/bin/env bash
# Run the same behavioural tests on the tagged pre-fix files and on HEAD. Requires a clean source tree and Docker.
# Only the two named source files are temporarily replaced; a trap restores their exact original bytes on every exit.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../../.."
RESULTS=${RESULTS_DIR:-"$PWD/artifacts/requirement-5"}
mkdir -p "$RESULTS"
WORK=$(mktemp -d "${TMPDIR:-/tmp}/shop-r5-before.XXXXXX")
cache=src/Modules/Products/Shop.Modules.Products/Catalog/CachedCatalogReader.cs
worker=src/Modules/Notifications/Shop.Modules.Notifications/NotificationsModule.cs
git diff --quiet HEAD -- "$cache" "$worker" || { echo 'Commit source changes before the before/after proof' >&2; exit 1; }
cp "$cache" "$WORK/cache.cs"; cp "$worker" "$WORK/worker.cs"
restore() { cp "$WORK/cache.cs" "$cache"; cp "$WORK/worker.cs" "$worker"; }
trap restore EXIT
check() {
  local label=$1 tag=$2 source=$3 project=$4 filter=$5 expected=$6
  git show "$tag:$source" >"$source"
  if dotnet test "$project" -m:1 -nr:false --filter "FullyQualifiedName~$filter" \
    --logger "trx;LogFileName=$label-before.trx" --results-directory "$RESULTS" >"$RESULTS/$label-before.txt" 2>&1; then
    echo "FAILED: $label unexpectedly passed before the fix" >&2; exit 1
  fi
  grep -q 'Assert.Equal() Failure' "$RESULTS/$label-before.txt" && grep -q "$expected" "$RESULTS/$label-before.txt" ||
    { cat "$RESULTS/$label-before.txt" >&2; echo 'The baseline failed for an unexpected reason' >&2; exit 1; }
  restore
  dotnet test "$project" -m:1 -nr:false --filter "FullyQualifiedName~$filter" \
    --logger "trx;LogFileName=$label-after.trx" --results-directory "$RESULTS" >"$RESULTS/$label-after.txt" 2>&1 ||
    { cat "$RESULTS/$label-after.txt" >&2; exit 1; }
  echo "$label: the pre-fix implementation fails the behavioural check; HEAD passes."
}
check cache r5-before-cache-coherence "$cache" tests/Shop.Modules.Products.IntegrationTests \
  NineReadsAcrossThreeAlreadyWarmHostsSeeTheEditAndRejectTheOldEtag NotModified
check workers r5-before-worker-prefetch "$worker" tests/Shop.Modules.Notifications.IntegrationTests \
  ThreeProductionWorkersShareThirtyEmailsWithPrefetchOneAndDeduplicateReplays 'Actual:   5'
git diff --exit-code -- "$cache" "$worker"
