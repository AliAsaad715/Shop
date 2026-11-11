#!/usr/bin/env bash
# Builds perf/results/comparison.md, the before/after report of Requirement 3 (task R3-02b). For every label it takes
# the latest three valid runs and shows, per endpoint and overall, their median p50, p95 and p99 and the change from
# the baseline, with a noise check: a change counts only when every run of the step was faster than every run of the
# baseline, or every one slower. Anything less is within run-to-run variation and is marked ≈.
#
#   perf/compare.sh [baseline label]      "baseline" unless given
#
# RUNS sets how many of each label's latest valid runs count (3). Needs jq.
set -euo pipefail
cd "$(dirname "$0")/.."

command -v jq >/dev/null || { echo "perf/compare.sh needs jq: sudo apt install jq, or brew install jq" >&2; exit 1; }
baseline=${1:-baseline}
keep=${RUNS:-3}
report=perf/results/comparison.md
if [[ ! $keep =~ ^[1-9][0-9]*$ ]]; then
  echo "RUNS must be a whole number above 0" >&2
  exit 2
fi

# Every k6 result under perf/results, each with the system file perf/run.sh wrote beside it, if any
runs=$(find perf/results -name '*.json' ! -name '*.system.json' | sort | while read -r file; do
  system=${file%.json}.system.json
  [[ -f $system ]] || system=/dev/null
  jq -c --arg file "$file" --slurpfile system "$system" \
    'select(type == "object" and has("endpoints") and has("overall")) | {file: $file, result: ., system: $system[0]}' "$file"
done | jq -s .)

if [[ $(jq 'map(select(.result.valid)) | length' <<<"$runs") -eq 0 ]]; then
  echo "No valid k6 results in perf/results yet; perf/run.sh <label> <rate> makes them" >&2
  exit 1
fi

program() {
  cat <<'JQ'
def median: sort | if length == 0 then null elif length % 2 == 1 then .[(length - 1) / 2 | floor]
  else (.[length / 2 - 1] + .[length / 2]) / 2 end;
def one: if . == null then "–" else (. * 10 | round / 10 | tostring) as $s
  | if ($s | test("\\.")) then $s else $s + ".0" end end;
def percent: if . == null then "–" else (. * 10000 | round / 100 | tostring) + "%" end;
def signed: if . > 0 then "+\(.)" else "\(.)" end;
def separated($a; $b): ($a | max) < ($b | min) or ($b | max) < ($a | min);
def vals($step; $path): [$step.runs[].result | getpath($path) | select(. != null)];
def sys($step; $path): [$step.runs[].system | select(. != null) | getpath($path) | select(. != null)];
def row: "| " + join(" | ") + " |";
def setup: {rate: .settings.rate, warmup: .settings.warmup, duration: .settings.duration,
  "page size": .settings.pageSize, products: .dataset.products, categories: .dataset.categories,
  customers: "\(.dataset.customers) \(.dataset.customerSource)"};

# Each label at the rate it was last run at, so a step measured again at another rate never mixes the two: its latest
# valid runs there, the invalid ones, and how many runs at other rates are left out
(group_by(.result.label) | map(sort_by(.result.startedAt) as $all
  | ($all[-1].result.settings.rate) as $rate
  | ($all | map(select(.result.settings.rate == $rate))) as $current
  | {label: $all[0].result.label, rate: $rate,
     runs: ($current | map(select(.result.valid)) | .[-$keep:]),
     invalid: ($current | map(select(.result.valid | not)) | length),
     others: ($all | map(select(.result.settings.rate != $rate)))})) as $labels
# The steps with valid runs: the baseline first, then the steps in the order they were run
| ($labels | map(select(.runs | length > 0))
  | sort_by(if .label == $baseline then "" else .runs[0].result.startedAt end)) as $steps
| ($steps | map(select(.label == $baseline)) | .[0]) as $base
| (reduce ($steps[].runs[].result.endpoints | keys_unsorted[]) as $name ([];
    if any(.[]; . == $name) then . else . + [$name] end)) as $endpoints
| ([$steps[].runs[].system | select(. != null) | .cpu.containers | keys[]] | unique) as $found
| ["api", "postgres", "inventory", "inventory-db", "redis", "k6"] as $known
| (($known | map(select(IN($found[])))) + ($found - $known)) as $services

# A step's median at a path, with the change from the baseline and the noise check
| def cell($step; $path):
    vals($step; $path) as $v | ($v | median) as $m
    | if $base == null or $step.label == $baseline then $m | one
      else vals($base; $path) as $b | ($b | median) as $bm
        | if $m == null or $bm == null or $bm == 0 then $m | one
          else (($m - $bm) / $bm * 100 | round) as $change
            # With two runs a side, a clean split happens by chance one time in three; with three, one in ten
            | if ($v | length) < 3 or ($b | length) < 3 then "\($m | one) (\($change | signed)% ?)"
              elif separated($v; $b) then "\($m | one) (\($change | signed)%)"
              else "\($m | one) (≈ \($change | signed)%)"
              end
          end
      end;

  "# Requirement 3: before and after",
  "",
  "Built by `perf/compare.sh` on \($built) from the latest \($keep) valid runs of each step. Each number is the median of those runs, in milliseconds, with the change from the baseline in brackets. **≈** marks a change within run-to-run variation: a change counts only when every run of the step was faster than every run of the baseline, or every one slower. **?** means one side has fewer than three runs, too few to tell.",
  "",
  (if $base == null then "There is no \"\($baseline)\" step yet, so no changes are shown.\n" else empty end),
  "## Overall",
  "",
  (["Step", "Runs", "Commit", "p50", "p95", "p99", "Errors", "Redis keyspace hit ratio"] | row),
  ([range(8) | "---"] | row),
  ($steps[] | . as $step | [
    .label, (.runs | length | tostring), ([.runs[].result.commit] | unique | join(", ")),
    cell($step; ["overall", "p50Ms"]), cell($step; ["overall", "p95Ms"]), cell($step; ["overall", "p99Ms"]),
    (vals($step; ["overall", "errorRate"]) | median | percent),
    (sys($step; ["redis", "hitRatio"]) | median | percent)
  ] | row),
  "",
  ($endpoints[] as $name
    | ([$steps[].runs[].result.endpoints[$name].route | select(. != null)] | .[0]) as $route
    | "## \($name): \($route)",
      "",
      (["Step", "req/s", "p50", "p95", "p99", "Errors"] | row),
      ([range(6) | "---"] | row),
      ($steps[] | . as $step | select(any(.runs[].result.endpoints; has($name))) | [
        .label, (vals($step; ["endpoints", $name, "rps"]) | median | one),
        cell($step; ["endpoints", $name, "p50Ms"]), cell($step; ["endpoints", $name, "p95Ms"]),
        cell($step; ["endpoints", $name, "p99Ms"]),
        (vals($step; ["endpoints", $name, "errorRate"]) | median | percent)
      ] | row),
      ""),
  "## What the system did",
  "",
  (if ($services | length) == 0 then
     "No system files yet: perf/run.sh saves them beside each run.\n"
   else
     ([$steps[].runs[].system.cpu.cores | select(. != null)] | .[0]) as $cores
     | "Medians over each step's runs, during the measured part only. CPU is each container's average, with its peak in brackets; 100% is one core, and Docker had \($cores). Then the most connections to the app's database, the most of them waiting at once, and the wait seen most often.",
       "",
       (["Step"] + $services + ["Connections", "Waiting", "Top wait"] | row),
       ([range(($services | length) + 4) | "---"] | row),
       ($steps[] | . as $step | [
         .label,
         ($services[] as $service
           | (sys($step; ["cpu", "containers", $service, "avgPercent"]) | median) as $avg
           | (sys($step; ["cpu", "containers", $service, "maxPercent"]) | median) as $max
           | if $avg == null then "–" else "\($avg | round)% (\($max | round)%)" end),
         (sys($step; ["database", "maxConnections"]) | median | if . == null then "–" else round | tostring end),
         (sys($step; ["database", "maxWaiting"]) | median | if . == null then "–" else round | tostring end),
         ([$step.runs[].system.database.waitEvents // {} | to_entries[]] | group_by(.key)
           | map({key: .[0].key, count: (map(.value) | add)}) | max_by(.count) | .key // "–")
       ] | row),
       ""
   end),
  "## Notes",
  "",
  ([
    ($steps[] | select(.label != $baseline and $base != null)
      | (.runs[0].result | setup) as $mine | ($base.runs[0].result | setup) as $theirs
      | .label as $label
      | [$mine | keys_unsorted[] | select($mine[.] != $theirs[.]) | "\(.) \($mine[.]), not \($theirs[.])"]
      | select(length > 0)
      | "- \($label) ran differently from the reference step \($baseline), so its numbers don't compare: \(join("; ")).\n"),
    ($steps[] | select(.label != $baseline and $base != null)
      | ([.runs[].system.cpu.cores // empty] | unique) as $mine
      | ([$base.runs[].system.cpu.cores // empty] | unique) as $theirs
      | select(($mine | length) > 0 and ($theirs | length) > 0 and $mine != $theirs)
      | "- \(.label) used \($mine | join(", ")) Docker CPUs; reference step \($baseline) used \($theirs | join(", ")). CPU allocation also differs.\n"),
    ($steps[] | select(.runs | length < $keep) | (.runs | length) as $n
      | "- \(.label) has only \($n) valid run\(if $n == 1 then "" else "s" end).\n"),
    ($labels[] | select(.invalid > 0)
      | "- \(.label): \(.invalid) invalid run\(if .invalid == 1 then "" else "s" end) at \(.rate) req/s left out.\n"),
    ($labels[] | select(.others | length > 0) | (.others | length) as $n
      | "- \(.label): \($n) earlier run\(if $n == 1 then "" else "s" end) at \([.others[].result.settings.rate] | unique
        | map(tostring) | join(" and ")) req/s left out; the step was measured again at \(.rate) req/s.\n"),
    ($steps[] | select(any(.runs[].result.dataset.customerSource; . == "registered"))
      | "- \(.label) used customers registered for the run, so my_orders read empty order lists.\n")
  ] | if length == 0 then "Nothing to flag: every step ran with the baseline's settings and data.\n" else join("") end)
JQ
}

jq -r --arg baseline "$baseline" --argjson keep "$keep" --arg built "$(date -u '+%Y-%m-%d %H:%M UTC')" \
  "$(program)" <<<"$runs" >"$report"

awk '/^## / { show = ($0 == "## Overall") } show' "$report"
echo "Wrote $report"
