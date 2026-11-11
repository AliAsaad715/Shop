# R3-11 control: before read routing

Three valid runs at **200 requests/second** on `r3-before-read-routing` (`c36f81f`), with Redis, ETags and the
physical replica present. The API still sends its database reads to primary. This control includes the merged
R3-07, R3-08, R3-09 and R3-10 work; the older `r3-after-redis` measurements precede some of those changes.

The tag adds only performance instrumentation to `ea82044`. The before API binary was published from that same
application source at `r3-before-routing`; both source revisions are recorded in the
[measurement manifest](../after-replica/manifest.json). Both phases use SDK 10.0.115 and the same cached ASP.NET
runtime image. The two API images use the normal Dockerfile's runtime stage with the locally published files.

Each run restores the same Shop and Inventory snapshots, empties Redis, waits for replica replay, then restarts
the clients. The workload has 5,006 products, 20 categories and the same five seeded customers, with the existing
traffic mix, ETags enabled, a 30-second warm-up and three measured minutes. Docker has 12 logical CPUs and
4,031,959,040 bytes of memory. No builds or test suites run during the six matched measurements.

| Run file suffix (UTC) | p50 | p95 | p99 | Errors | Dropped |
| --- | --- | --- | --- | --- | --- |
| `20261111T230540Z` | 2.2 | 9.8 | 12.3 | 0% | 0 |
| `20261111T231004Z` | 2.4 | 10.0 | 13.8 | 0% | 0 |
| `20261111T231430Z` | 2.2 | 9.9 | 12.5 | 0% | 0 |

The earlier **400 req/s** JSON is retained as an invalid exploratory attempt. PostgreSQL exhausted connections,
requests timed out, and the run was stopped; it has dropped iterations and an incomplete measured window. It
does not establish a capacity limit and contributes nothing to the medians. Both matched phases use 200 req/s.

The standby's recorded statements in these control runs are health checks, with no catalog/order queries.
[The paired report](../comparison.replica.md) and [after-routing explanation](../after-replica/README.md) show
the change; the historical 400/1,000 req/s stages are not a controlled comparison for this experiment.
