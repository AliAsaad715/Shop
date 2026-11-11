# Redis and HybridCache (R3-07)

These measurements belong to the preserved `r3-before-redis` and `r3-after-redis` tags, before the later merge
with R3-09. The merge keeps the measured data and adds functional Redis/ETag integration tests; it does not
claim a new load-test measurement of the combined implementation.

## Prediction, written before measurement

At the initially planned 1,000 requests/s mix, PostgreSQL should execute fewer catalog queries and use less CPU. Warm product
details should avoid their SQL lookup but retain their warehouse RPC, so their gain should be smaller than categories'
or a warm first list page's. Numbered pages 1-3 are cached, while the current load script follows cursors after page 1;
about 60% of list requests are eligible. Order prices and customer orders still read PostgreSQL directly.

Expect catalog p95 to improve, with a smaller overall p95 change because order placement remains the slowest endpoint.
No target percentage is assumed: report the median of three valid runs and the existing run-to-run noise check.

## Measurement method

The original checkout could not compile: five Orders/Releases source files were excluded by the global Releases
ignore rule. Commit `3f36a42` reconstructs that existing contract, verified by 34 Orders unit tests and 36 database
integration tests. Both sides include this repair. `r3-before-redis` identifies the clean repaired checkout, with
R3-05/R3-06 and no caching. The old `r3-after-db` tag is preserved.

Both API images are published in Release by the same host .NET SDK and use the same official ASP.NET 10 runtime
image and runtime Dockerfile settings. This avoids a slow SDK image download during measurement. A temporary
worktree holds the pre-Redis version; the same Compose project, volumes, seeded snapshot and warehouse image are
used by both sides. Downloads, builds and other tests finish before measured runs begin. The machine is on AC power.

Snapshot creation initially triggered PostgreSQL crash recovery, logging an untracked child exiting with code 2.
This is consistent with an orphaned failed health probe being reaped by Postgres. Both database containers run
with `init: true` for both labels; snapshots then saved/restored successfully and recovery did not recur.
No query, connection-pool or PostgreSQL tuning is applied.

Each label restores the same snapshot before every run, uses five seeded customers, 30 seconds warm-up and three
minutes measured at **400 requests/s**. Redis is flushed before every after-Redis run; restarting the API clears L1.
The only workload change is the caching implementation: `perf/load.js` is unchanged.

The 1,000 requests/s attempt saturated PostgreSQL connections (`too many clients already`) and k6 exited with 137
without saving a result. The user chose 400 requests/s for both sides, keeping the database settings unchanged.
The current host has about 7.9 GiB RAM; Docker has 12 logical CPUs and 3.75 GiB RAM. The historical baseline had
8 Docker CPUs and ran at 1,000 requests/s. Its files/tags are preserved, but its deltas are not comparable with
this pair. Use [`comparison.redis.md`](../comparison.redis.md), whose reference is `after-db`; the general
[`comparison.md`](../comparison.md) retains the historical view and flags rate/CPU differences.

Both images use host SDK 10.0.114 and ASP.NET runtime image
`sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e`.
Redis is 8.10.3-alpine, with 128 MiB and `allkeys-lru`, no RDB/AOF. Windows reserved local port 6379, so this
checkout's ignored `.env` publishes Redis on 127.0.0.1:16379; API traffic uses redis:6379 inside Compose.
For a local `dotnet run` on this host, set `ConnectionStrings__Redis=localhost:16379`; `.env` is read by Compose.

On Windows, Git Bash used a wrapper that converts only Docker bind sources from `/c/...` to Windows paths and
disables MSYS argument conversion only for docker.exe. curl keeps its normal `/dev/null` conversion. `API_URL`
was explicitly `http://localhost:8080`. This changes neither the k6 script nor its scenarios.

Redis's reported hits/misses cover the complete run, including setup, warm-up and HybridCache tag metadata. L1 hits
never reach Redis, so the Redis keyspace hit ratio is not the total application cache hit ratio.

## Results

Medians of three valid runs per side, in milliseconds. All six valid runs held 400 req/s, with zero errors and
zero dropped iterations. A fourth pre-Redis attempt was necessary because the third attempt dropped requests.

| Endpoint | Before p50 / p95 / p99 | After p50 / p95 / p99 | p95 change |
| --- | --- | --- | --- |
| All | 2.7 / 10.8 / 18.0 | 1.9 / 9.8 / 13.3 | -9% |
| Product list | 2.4 / 4.9 / 12.3 | 1.1 / 3.2 / 5.2 | -35% |
| Product details | 2.8 / 5.7 / 14.1 | 2.0 / 3.8 / 5.9 | -33% |
| Categories | 1.7 / 4.3 / 9.1 | 0.6 / 1.1 / 2.1 | -74% |
| My orders | 3.0 / 6.3 / 14.7 | 3.1 / 5.6 / 8.2 | approximately -11% |
| Create order | 11.9 / 20.4 / 65.7 | 11.4 / 15.9 / 24.8 | approximately -22% |

The catalog p95 gains and overall p95 gain pass the existing separation check: every after run was faster than
every valid before run. Overall p99 fell 26% as a point estimate, but remains within run-to-run variation (the
fastest before run was 13.5 ms and the slowest after run 13.6 ms). Orders' p95 changes also remain within variation.

## What the system did

Run timestamps below are UTC on 2026-11-11. CPU is percent of one core, during the measured window.

| Label / start | Valid | p50 / p95 / p99 ms | API / Postgres CPU | Max DB connections | Redis keyspace hit ratio |
| --- | --- | --- | --- | --- | --- |
| after-db / 16:57:43 | yes | 2.7 / 10.8 / 18.0 | 110.5% / 35.7% | 57 | - |
| after-db / 17:02:03 | yes | 2.6 / 9.9 / 13.5 | 100.0% / 33.2% | 52 | - |
| after-db / 17:06:25 | **no** | 3.7 / 3591.5 / 6790.2 | 149.5% / 121.5% | 99 | - |
| after-db / 17:12:35 | yes | 2.7 / 11.2 / 19.9 | 107.8% / 33.9% | 40 | - |
| after-redis / 17:20:10 | yes | 1.9 / 9.8 / 13.2 | 84.9% / 14.7% | 8 | 73.7% |
| after-redis / 17:24:24 | yes | 2.0 / 9.7 / 13.6 | 85.8% / 15.4% | 6 | 73.7% |
| after-redis / 17:28:41 | yes | 1.9 / 9.8 / 13.3 | 82.3% / 15.5% | 15 | 73.7% |

Before Redis, the three valid runs needed up to 40-57 database connections. The third attempt saturated the pool
at 99, expanded k6 to 1,226 VUs, and dropped 6,043 arrivals. Its raw JSON/system files remain in `after-db/`, but
it is excluded from medians. The replacement run held the rate without errors or drops.

After Redis, all three attempts passed without extra VUs. Median API CPU fell from 107.8% to 84.9% of one core,
Postgres from 33.9% to 15.4%, and the median of each run's peak connection count from 52 to 8. Redis cost 1.6% of one core and used about
5.25 MiB in the sample after the final run. The costliest database work shifted toward the customer's order count
and uncached cursor pages; order-line writes remain live. Inventory CPU stayed similar because every product
detail still fetches stock. Bandwidth stayed about 391 MB/run, as expected without ETags.

The original prediction was directionally correct: shared reads became faster and database work fell. The overall
p95 gain is smaller than the catalog gains because order creation remains uncached. Default TTLs were retained;
the cache fits comfortably within its memory budget, and the measured ratio does not justify a speculative change.

## Verification

- `dotnet build Shop.slnx --no-restore`: 0 warnings, 0 errors.
- `dotnet test Shop.slnx --no-build`: 404 passed, 0 failed, 0 skipped, including 27 cache integration cases.
- Final Compose services running, Redis/PostgreSQL/warehouse healthy, `/health` 200 and `/docs` 200 after redirect.
- No schema or warehouse source changes. Stock and order-price reads remain live; R3-08's edit endpoint is separate.
