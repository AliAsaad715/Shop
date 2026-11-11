# Redis and HybridCache (R3-07)

## Prediction, written before measurement

At the unchanged 1,000 requests/s mix, PostgreSQL should execute fewer catalog queries and use less CPU. Warm product
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

Snapshot creation initially triggered PostgreSQL crash recovery: a timed-out health probe was reaped by Postgres
as an untracked child exiting with code 2. Both database containers run with `init: true` for both measurement
labels, so an init process reaps probe children. No query, connection-pool or PostgreSQL tuning is applied.

Each label restores the same snapshot before every run, uses five seeded customers, 30 seconds warm-up and three
minutes measured at 1,000 requests/s. Redis is flushed before every after-Redis run; restarting the API clears L1.
The only workload change is the caching implementation: `perf/load.js` is unchanged.

Redis's reported hits/misses cover the complete run, including setup, warm-up and HybridCache tag metadata. L1 hits
never reach Redis, so the Redis keyspace hit ratio is not the total application cache hit ratio.

Measured results and interpretation will be recorded here after both sets of valid runs finish.
