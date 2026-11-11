# R3-11: replica reads and read-your-writes

Three valid runs at **200 requests/second** on `r3-after-read-routing` (`28c1661`), paired with
`r3-before-read-routing` (`c36f81f`). Every matched run completed its measured window with **zero request errors
and zero dropped iterations**. The replica runs in both phases with its normal zero apply delay. Redis caching,
ETags, the dataset, clients, traffic mix, 30-second warm-up, three-minute measurement and Docker allocation stay
the same; routing is enabled only in the after phase, with a ten-second primary window.

The [control notes](../before-routing/README.md) describe the source/image relationship and the excluded 400 req/s
exploratory attempt. [manifest.json](manifest.json) records the commits, image IDs, SDK/runtime, hardware
allocation and effective settings. Run filenames use UTC; the experiment was completed on 2026-11-12 in Baghdad.

## What changed

| Median across three runs | Before | After |
| --- | --- | --- |
| Overall p50 | 2.2 ms | 2.2 ms |
| Overall p95 | 9.9 ms | 10.3 ms |
| Overall p99 | 12.5 ms | 13.1 ms |
| API CPU | 46.3% | 48.1% |
| Primary PostgreSQL CPU | 8.8% | 6.5% |
| Replica PostgreSQL CPU | 1.8% | 4.4% |
| Primary peak connections | 4 | 4 |
| Replica peak connections | 0 | 2 |
| Redis keyspace hit ratio | 66.9% | 66.9% |

CPU percentages are relative to one core (100% is one core), not the whole 12-CPU allocation. The paired
[comparison.replica.md](../comparison.replica.md) includes p50/p95/p99 for every endpoint and the existing noise
check. Every after-run's overall p95 (10.4, 10.2, 10.3 ms) is above every control run's (9.8, 10.0, 9.9 ms), so
the report records a **4% p95 regression**. The p99 difference overlaps run-to-run variation.

Reads did move: the replica's saved top statements include **6,899 / 6,830 / 6,846 catalog SQL calls**, compared
with none in the controls. These counts include setup and warm-up as well as measurement. A few order statements
also reach the replica before a customer first writes; the five active buyers then mostly stay on primary through
their refreshed pins. Cursor catalog pages provide most of the offloading. Cache fills and order pricing remain
on primary by design. Statement lists also contain driver/health traffic; they are not HTTP hit counters.

The primary's median CPU falls about 26%, with work now visible on the standby. Both databases share this laptop's
CPU and memory. The feature also adds a Redis write after each committed order and read-routing/context work;
these are possible contributors to the latency cost, but this experiment does not isolate their individual costs.
The result demonstrates workload distribution and consistency behavior, with a small latency regression on one
machine. It does not establish how separate database machines would perform.

Redis's number is **keyspace hit ratio**: it includes tag and consistency-marker activity, includes setup/warm-up,
and excludes L1 hits. Each `.system.json` keeps measured-window CPU and connection samples, primary top queries,
and `replica.database` / `replica.topQueries`. The overall historical report retains the 400/1,000 req/s stages
with its configuration warnings; use the paired report to assess R3-11.

## Edit/read and lag proof

[read-your-writes-proof.json](read-your-writes-proof.json) records a separate post-measurement demo using the
real API and Go warehouse with `recovery_min_apply_delay=5s`. A new Customer order was present in its HTTP list
and detail at **0.57 seconds**, while a direct standby query still returned zero rows. The standby contained it
at **5.19 seconds**. The demo restored apply delay to zero. `/health` returned 200/Healthy and an uncached catalog
read still returned 200 after the standby restart.

The **21 new routing integration cases** use their own physical standby, paused WAL replay, Redis and two real
API hosts. They prove primary-only cache fills/prices/Identity, cross-host pins and expiry, bypass of another
host's warm L1, order ownership, marking after successful product/order writes, failed-save behavior, and replica
and Redis outages. The full solution build has zero warnings/errors; the final suite passed **479/479**, with no
skips, using `dotnet test Shop.slnx --no-build --verbosity quiet -m:1`. Project serialization avoids timing
contention in an existing warehouse health test observed during the initial parallel build/test run.
Legacy hosts explicitly disable routing; the final isolation run also enables the developer's
`ReadRouting__Enabled=true` environment variable to verify it cannot redirect those tests.

The primary pin is bounded by its configured TTL. Redis failure makes authenticated routed reads choose primary;
the writer also retains a local fallback pin. A marker that could not be written is not durable across hosts or
restarts once Redis recovers. [The routing guide](../../../README.md#read-routing-and-read-your-writes-task-r3-11)
documents those limits and activation settings.

## Running the comparison

Start the replica profile and run each tagged application with the corresponding flag, using the SDK/runtime
recorded in the manifest for an exact environment match:

```sh
# On r3-before-read-routing
READ_ROUTING_ENABLED=false docker compose --profile replica up -d --build --wait
perf/run.sh before-routing 200

# On r3-after-read-routing
READ_ROUTING_ENABLED=true docker compose --profile replica up -d --build --wait
perf/run.sh after-replica 200
perf/compare.sh before-routing
```

The checked-in paired report restricts inputs to these two labels; the last command normally also shows other
historical labels, with their configuration warnings. Every run restores snapshots and clears Redis automatically.
