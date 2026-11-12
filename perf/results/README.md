# Requirement 3 results (task R3-12)

The four exhibits the plan asks for: the results table, what the system did in each run, a live edit and a live
replica. Every number below comes from runs saved in this folder; each step's own folder has its runs, their system
files and its notes.

## How to read these numbers

Each step was measured against the step before it, back to back, on one machine at one rate, three valid runs a side
(the median, with `perf/compare.sh`'s noise check). The pairs ran on two machines and at three rates, though, so a
change within a row holds, but the rows don't compare with each other:

| Step | Measured at | Machine | Against |
| --- | --- | --- | --- |
| Baseline | 1,000 req/s | this laptop, 8 Docker CPUs | – |
| After database | 400 req/s | a teammate's machine, 12 Docker CPUs | nothing at that rate |
| After Redis | 400 req/s | the same machine | after database, same session |
| After ETag | 800 req/s, product details only | this laptop | the same app without ETags sent back |
| After replica | 200 req/s | the teammate's machine | the same stack without routing, same session |

Two gaps follow from that. "After database" has no baseline at its rate, so its gain shows in the database rather
than in p95. And no full-mix run measured the ETag step's latency.

## 1. The results table

Milliseconds, overall, median of three valid runs. A change in brackets is against the row's own reference above;
**≈** marks one within run-to-run variation.

| Run | p50 | p95 | p99 | Cache hit ratio | What the system did |
| --- | --- | --- | --- | --- | --- |
| Baseline | 2.8 | 11.3 | 23.2 | – | At 1,000 req/s Postgres used a whole core and the API 2.5; up to 69 connections, 4 waiting at once |
| After database (R3-05, R3-06) | 2.7 | 10.8 | 18.0 | – | At 400 req/s: Postgres 34% of a core, up to 52 connections. The deepest catalog page fell from 4.09 to 0.10 ms in the database, order details from 3 queries to 1 |
| After Redis (R3-07) | 1.9 (-30%) | 9.8 (-9%) | 13.3 (≈ -26%) | 73.7% (Redis keyspace) | Postgres 34% → 15% of a core, connections 52 → 8; catalog p95 -33% to -74% |
| After ETag (R3-09) | – | – | – | 45% 304s, details only | Product details alone: 28.5% less data. In the full mix only 1.5% were 304s: orders keep changing the stock in the ETag |
| After replica (R3-11) | 2.2 (≈ 0%) | 10.3 (+4%) | 13.1 (≈ +5%) | 66.9% (Redis keyspace) | Primary Postgres 8.8% → 6.5% of a core, replica 1.8% → 4.4%: about 6,850 catalog reads a run moved to the replica |

The cache column is Redis's keyspace hit ratio: it counts only reads that reached Redis, warm-up included, and none of
HybridCache's in-memory hits, so the app's own hit ratio is higher.

## 2. What the system did

**Baseline** ([`baseline/`](baseline), `r3-rebaseline`, 1,000 req/s). The database was the limit: Postgres at about
100% of a core, the API at 250%, the Npgsql pool climbing to 69 connections with up to 4 waiting at once. A catalog
page with `OFFSET` was the costliest query, 21% of all database time, and sessions waited mostly on clients and on
WAL syncs for commits. One run in four broke down: the pool reached 99 of Postgres's 100 connections with 10 waiting,
`LWLock:ProcArray` waits piled up, Postgres jumped to 275% of a core, and requests failed and were dropped. It is
left out.

**After database** ([`after-db/`](after-db), `r3-before-redis`, 400 req/s). Keyset pagination reads a deep page through
the index in 0.10 ms instead of 4.09 ([plans](../plans/README.md)), and order details, status changes and the current
user each make one or two round trips fewer. At 400 req/s the API used 108% of a core, Postgres 34%, with up to 52
connections. One attempt still saturated the pool at 99 connections and dropped requests; it is left out, and a fourth
run replaced it. There is no baseline at 400 req/s, so this step's latency change wasn't measured.

**After Redis** ([`after-redis/`](after-redis), `r3-after-redis`, 400 req/s, against after-db). HybridCache answers the
catalog from memory or Redis, so Postgres fell from 34% to 15% of a core, and peak connections from 52 to 8. Overall
p50 fell 30% and p95 9%; catalog p95 fell 33% to 74%. Orders stay uncached on purpose, which is why the overall gain is
smaller than the catalog's. Redis itself used 1.6% of a core and about 5 MB.

**After ETag** ([`etag/`](etag), 800 req/s, product details only). Clients that send their ETag back got a 304 for
45% of product views, and the data received fell 28.5%: a 304 costs about 357 bytes instead of 978. The database and
the warehouse work the same either way. In the full mix only 1.5% came back 304, because the ETag includes the stock
and the products viewed most are the ones ordered most; there, the two extra headers make a response 4.5% larger than
the baseline's.

**After replica** ([`after-replica/`](after-replica), 200 req/s, against the same stack without routing). Read routing
moved about 6,850 catalog statements a run to the replica: the primary's CPU fell from 8.8% to 6.5% of a core while
the replica's rose from 1.8% to 4.4%. Overall p95 rose 4% (every run with routing was slower than every run without),
p50 and p99 stayed within noise. On one laptop the replica adds no hardware, so the step spreads the work but can't
speed it up; its value comes with separate machines.

## 3. Live edit

[`edit-proof.txt`](edit-proof.txt), recorded with [`../proof-edit.sh`](../proof-edit.sh) on today's app: an admin raises
a product's price (`PUT`, R3-08); the next request, still sending the old ETag, gets the new price and a new ETag at
once (`v882` to `v183484`) although product details are cached, and one more request with the new ETag is a 304.
Redis couldn't be downloaded on this laptop when it was recorded, so HybridCache's in-memory layer held the cached
copy; the edit clears it the same way.

## 4. Live replica

[`replica-proof.txt`](replica-proof.txt), recorded with [`../proof-replica.sh`](../proof-replica.sh): with the replica
replaying every change 5 seconds late, a customer's new order was in "my orders" after 0.64 s while the replica still
had none of it, and the replica caught up after 5.45 s. Without Redis here, the writing API kept its own local pin; the
teammate's run with Redis, [`after-replica/read-your-writes-proof.json`](after-replica/read-your-writes-proof.json),
shows the same: 0.57 s through the API, 5.19 s on the replica.

## One table on one machine

[`../final.sh`](../final.sh) measures all five steps again on one machine at one rate, each app built from its tagged
commit (`perf/final.sh build`, then `perf/final.sh run 800`). It takes about 75 minutes on mains power with nothing else
running, and needs the Redis image, which this laptop couldn't download when these results were written. Running it
would replace the table above with rows that compare with each other.
