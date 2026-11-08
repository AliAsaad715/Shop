# Baseline (task R3-03)

The app as it is holds 1,000 requests per second on the seeded data, with a p50 of 2.9 ms, a p95 of 9.2 ms and a p99
of 21.0 ms, the median of three runs. At 1,200 requests per second it collapses.

## What was measured

| | |
| --- | --- |
| Commit | `r3-baseline` (`91901e5`): the app as merged in PR #30, with the seed and fixes to the load tooling; no performance change yet |
| Data | [`perf/seed.sh`](../../seed.sh), restored from the snapshot before run 1: 50,006 customers, 5,006 products, 500,004 orders, 1.5 million order lines |
| Load | 1,000 req/s in the R3-02 mix, a 30 s warm-up then 3 minutes measured, three runs back to back |
| Customers | `customer1` (2,276 orders) and `customer2` (963), the heaviest; `customer100` (89), `customer5000` (12) and `customer40000` (8) |
| Machine | One laptop: Intel i7-6820HQ, 4 cores and 8 threads, 15 GB RAM. The stack and k6 all ran in Docker |
| When | 2026-11-08, 18:21 to 18:32 UTC |

## Results

| Run | Started (UTC) | p50 ms | p95 ms | p99 ms | max ms | Errors | Dropped |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 18:21 | 2.8 | 8.9 | 19.2 | 85 | 0 | 0 |
| 2 | 18:25 | 2.9 | 9.2 | 21.0 | 98 | 0 | 0 |
| 3 | 18:28 | 3.0 | 13.6 | 24.4 | 337 | 0 | 0 |
| **Median** | | **2.9** | **9.2** | **21.0** | | | |

Per endpoint, the median of the three runs:

| Endpoint | req/s | p50 ms | p95 ms | p99 ms |
| --- | --- | --- | --- | --- |
| `product_list` | 400 | 2.9 | 7.5 | 18.0 |
| `product_details` | 400 | 2.5 | 7.3 | 21.4 |
| `categories` | 50 | 1.6 | 5.0 | 13.1 |
| `my_orders` | 100 | 4.2 | 8.6 | 20.6 |
| `create_order` | 50 | 8.2 | 17.5 | 39.4 |

The per-run files are in this folder; [`../comparison.md`](../comparison.md) is the report every later step adds a row
to. The row for the plan's results table:

| Run | p50 (ms) | p95 (ms) | p99 (ms) | Cache hit ratio | What the system did |
| --- | --- | --- | --- | --- | --- |
| Baseline | 2.9 | 9.2 | 21.0 | – | api at 2.5 cores, Postgres at 1; p99 flat until a checkpoint stalled WAL writes in run 3 |

## The rate

Every step runs at 1,000 req/s, the highest rate the baseline held. Each probe was one run, a 20 s warm-up then 60 s
measured:

| Rate (req/s) | p50 ms | p95 ms | p99 ms | max ms | Dropped | api CPU | Postgres CPU | Connections |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 250 | 2.8 | 10.2 | 17.9 | 72 | 0 | 0.85 cores | 0.26 | 6 |
| 300 | 2.7 | 8.3 | 12.8 | 31 | 0 | 0.97 | 0.30 | 6 |
| 400 | 2.7 | 7.0 | 9.9 | 39 | 0 | 1.28 | 0.40 | 7 |
| 500 | 2.7 | 7.3 | 10.7 | 50 | 0 | 1.60 | 0.48 | 9 |
| 800 | 2.6 | 7.7 | 13.4 | 138 | 0 | 2.25 | 0.78 | 22 |
| 900 | 2.8 | 8.5 | 15.4 | 66 | 0 | 2.42 | 0.88 | 19 |
| **1,000** | **2.9** | **8.9** | **20.4** | **113** | **0** | **2.71** | **0.99** | **38** |
| 1,200 | 3,542 | 9,016 | 10,001 | 10,015 | 27,371 | 2.81 | 2.91 | 93 |

Up to 900 req/s, latency stays flat and CPU grows with the rate. At 1,000 the p99 has doubled from about 10 ms, the
connections jump and Postgres reaches a full core: the system still holds, but it's working for it. At 1,200 the
machine runs out of CPU, about 7 of its 8 threads busy: requests queue, the connections climb to 93 of the 100
Postgres allows, Postgres starts waiting on its own locks (`LWLock:LockManager`, `LWLock:ProcArray`), and only 766
of the 1,200 requests a second are sent at all.

## What the system did

- **Run 1.** The api was the busiest container at 2.6 cores (100% is one core). Postgres used 1.0 core, k6 half a
  core, the warehouse and its database 0.4 between them. At most 25 connections, about one active on average, and at
  most two waiting. The p99 stayed at 19 ms.
- **Run 2.** The same picture: api 2.5 cores, Postgres 1.05. A checkpoint ran through most of it (18:26 to 18:29)
  without visible harm; the slowest request took 98 ms.
- **Run 3.** Its slow moment lines up with a checkpoint that started at 18:31:18, inside the measured part. Commits
  waited for WAL writes and flushes (`LWLock:WALWrite`, `IO:WalSync`), as they do when a checkpoint starts and
  Postgres writes whole pages into the WAL on their first change. Requests queued and the connections jumped to 73.
  Writes took the hit: `create_order`'s p99 rose to 89 ms against 38 in the other runs, and the slowest request took
  337 ms. Postgres averaged 1.1 cores.

What that adds up to:

- **The app does most of the work, not the database.** The api uses 2.5 cores to Postgres's 1. A likely reason: EF
  Core logs every SQL command at Information level. In run 1's measured three minutes that was 278,651 commands, 2.8
  million log lines and 183 MB of text, written to the console at 15,700 lines a second.
- **The database has room at this rate.** One core, about one active connection on average, and 1.55 SQL commands per
  request: no N+1 in this mix.
- **Writes are the most fragile.** `create_order` has twice the others' p99, and it's what a checkpoint hurts.
- **One stall while probing.** A 300 req/s probe right after a snapshot save stalled for about two seconds. The app's
  pool grew to all 100 connections Postgres allows, so even the sampler couldn't connect. It didn't happen again in
  the 11 runs at up to 1,000 req/s that followed, and `run.sh` now keeps its database session from the start.

## Already in place

Some fixes the plan expects to make are already in the code, so R3-05 and R3-06 should start from `EXPLAIN` and
`pg_stat_statements` rather than from these:

- The plan's three index candidates: `orders (customer_id, created_at DESC, id DESC)`,
  `orders (status, created_at DESC, id DESC)` and `products (category_id, created_at DESC, id)`.
- The batched catalog call: an order looks up all its products with one `IProductCatalog.GetByIdsAsync`.
- `AsNoTracking` and DTO projections on the read endpoints.

What's left to look at: a `COUNT(*)` on every list page (`product_list`, `my_orders`), deep `OFFSET` pages, and the
`sum(quantity)` subquery `my_orders` runs for every order on its page.

## For the next steps

- **Prediction to test:** logging SQL commands at `Warning` instead of `Information` cuts the api's CPU by a large
  share and lowers the p99. It's a step of its own, with its own row, not part of the plan's four.
- **pg_stat_statements is off** until R3-04, so there are no top queries yet. Later steps run with it on, which costs a
  little; run the baseline again after R3-04 for a strict comparison.
- **Checkpoints** come every 5 minutes (`checkpoint_timeout`), so any run can catch one; that's run-to-run variation,
  and `perf/compare.sh` only counts a change when all three runs agree.
- **Docker keeps the api's log without rotation**, roughly 300 MB a run at this rate. `docker compose up -d
  --force-recreate api` clears it.
