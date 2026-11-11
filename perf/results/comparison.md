# Requirement 3: before and after

Built by `perf/compare.sh` on 2026-11-11 18:11 UTC from the latest 3 valid runs of each step. Each number is the median of those runs, in milliseconds, with the change from the baseline in brackets. **≈** marks a change within run-to-run variation: a change counts only when every run of the step was faster than every run of the baseline, or every one slower. **?** means one side has fewer than three runs, too few to tell. In the endpoint tables, Bytes/response is what a response cost on the wire, headers included, and 304s the share answered 304 Not Modified with no body (task R3-09).

## Overall

| Step | Runs | Commit | p50 | p95 | p99 | Errors | Redis keyspace hit ratio |
| --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 3 | r3-rebaseline | 2.8 | 11.3 | 23.2 | 0% | – |
| after-db | 3 | r3-before-redis | 2.7 (≈ -4%) | 10.8 (≈ -4%) | 18.0 (≈ -22%) | 0% | – |
| after-redis | 3 | r3-after-redis | 1.9 (-32%) | 9.8 (≈ -13%) | 13.3 (-43%) | 0% | 73.7% |

## product_list: GET /api/v1/products

| Step | req/s | p50 | p95 | p99 | Errors | Bytes/response | 304s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 400.0 | 2.8 | 6.9 | 18.0 | 0% | 10958 | – |
| after-db | 160.0 | 2.4 (-14%) | 4.9 (-29%) | 12.3 (-32%) | 0% | 11002 | – |
| after-redis | 160.0 | 1.1 (-61%) | 3.2 (-54%) | 5.2 (-71%) | 0% | 11003 | – |

## product_details: GET /api/v1/products/{id}

| Step | req/s | p50 | p95 | p99 | Errors | Bytes/response | 304s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 400.0 | 2.4 | 6.8 | 21.3 | 0% | 924 | – |
| after-db | 160.0 | 2.8 (+17%) | 5.7 (-16%) | 14.1 (-34%) | 0% | 924 | – |
| after-redis | 160.0 | 2.0 (-17%) | 3.8 (-44%) | 5.9 (-72%) | 0% | 924 | – |

## categories: GET /api/v1/categories

| Step | req/s | p50 | p95 | p99 | Errors | Bytes/response | 304s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 50.0 | 1.7 | 4.9 | 12.4 | 0% | 1606 | – |
| after-db | 20.0 | 1.7 (≈ 0%) | 4.3 (≈ -12%) | 9.1 (≈ -27%) | 0% | 1606 | – |
| after-redis | 20.0 | 0.6 (-65%) | 1.1 (-78%) | 2.1 (-83%) | 0% | 1606 | – |

## my_orders: GET /api/v1/orders

| Step | req/s | p50 | p95 | p99 | Errors | Bytes/response | 304s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 100.0 | 3.6 | 7.7 | 18.9 | 0% | 5236 | – |
| after-db | 40.0 | 3.0 (-17%) | 6.3 (-18%) | 14.7 (-22%) | 0% | 5236 | – |
| after-redis | 40.0 | 3.1 (-14%) | 5.6 (-27%) | 8.2 (-57%) | 0% | 5236 | – |

## create_order: POST /api/v1/orders

| Step | req/s | p50 | p95 | p99 | Errors | Bytes/response | 304s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 50.0 | 8.5 | 23.2 | 54.9 | 0% | 1120 | – |
| after-db | 20.0 | 11.9 (≈ +40%) | 20.4 (≈ -12%) | 65.7 (≈ +20%) | 0% | 1117 | – |
| after-redis | 20.0 | 11.4 (≈ +34%) | 15.9 (≈ -31%) | 24.8 (-55%) | 0% | 1121 | – |

## What the system did

Medians over each step's runs, during the measured part only. CPU is each container's average, with its peak in brackets; 100% is one core, and Docker had 8. Then the most connections to the app's database, the most of them waiting at once, and the wait seen most often.

| Step | api | postgres | inventory | inventory-db | redis | k6 | Connections | Waiting | Top wait |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 250% (262%) | 101% (120%) | 29% (34%) | 11% (22%) | – | 52% (98%) | 69 | 4 | Client:ClientRead |
| after-db | 108% (163%) | 34% (56%) | 18% (28%) | 6% (15%) | – | 31% (52%) | 52 | 1 | Client:ClientRead |
| after-redis | 85% (112%) | 15% (29%) | 17% (25%) | 6% (17%) | 2% (3%) | 29% (44%) | 8 | 1 | IO:WalSync |

## Notes

- after-db ran differently from the reference step baseline, so its numbers don't compare: rate 400, not 1000.
- after-redis ran differently from the reference step baseline, so its numbers don't compare: rate 400, not 1000.
- after-db used 12 Docker CPUs; reference step baseline used 8. CPU allocation also differs.
- after-redis used 12 Docker CPUs; reference step baseline used 8. CPU allocation also differs.
- after-db: 1 invalid run at 400 req/s left out.
- baseline: 1 invalid run at 1000 req/s left out.
- Left out of the tables: etag-off, etag-on, runs of only part of the mix (ONLY), which measure that part on its own.
