# Requirement 3: before and after

Built by `perf/compare.sh` on 2026-11-11 23:34 UTC from the latest 3 valid runs of each step. Each number is the median of those runs, in milliseconds, with the change from the baseline in brackets. **≈** marks a change within run-to-run variation: a change counts only when every run of the step was faster than every run of the baseline, or every one slower. **?** means one side has fewer than three runs, too few to tell. In the endpoint tables, Bytes/response is what a response cost on the wire, headers included, and 304s the share answered 304 Not Modified with no body (task R3-09).

## Overall

| Step | Runs | Commit | p50 | p95 | p99 | Errors | Redis keyspace hit ratio |
| --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 3 | r3-rebaseline | 2.8 | 11.3 | 23.2 | 0% | – |
| after-db | 3 | r3-before-redis | 2.7 (≈ -4%) | 10.8 (≈ -4%) | 18.0 (≈ -22%) | 0% | – |
| after-redis | 3 | r3-after-redis | 1.9 (-32%) | 9.8 (≈ -13%) | 13.3 (-43%) | 0% | 73.7% |
| before-routing | 3 | r3-before-read-routing | 2.2 (-21%) | 9.9 (≈ -12%) | 12.5 (-46%) | 0% | 66.9% |
| after-replica | 3 | r3-after-read-routing | 2.2 (-21%) | 10.3 (≈ -9%) | 13.1 (-44%) | 0% | 66.9% |

## product_list: GET /api/v1/products

| Step | req/s | p50 | p95 | p99 | Errors | Bytes/response | 304s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 400.0 | 2.8 | 6.9 | 18.0 | 0% | 10958 | – |
| after-db | 160.0 | 2.4 (-14%) | 4.9 (-29%) | 12.3 (-32%) | 0% | 11002 | – |
| after-redis | 160.0 | 1.1 (-61%) | 3.2 (-54%) | 5.2 (-71%) | 0% | 11003 | – |
| before-routing | 80.0 | 1.1 (-61%) | 3.0 (-57%) | 4.7 (-74%) | 0% | 11012 | 0% |
| after-replica | 80.0 | 1.1 (-61%) | 3.1 (-55%) | 4.9 (-73%) | 0% | 11003 | 0% |

## product_details: GET /api/v1/products/{id}

| Step | req/s | p50 | p95 | p99 | Errors | Bytes/response | 304s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 400.0 | 2.4 | 6.8 | 21.3 | 0% | 924 | – |
| after-db | 160.0 | 2.8 (+17%) | 5.7 (-16%) | 14.1 (-34%) | 0% | 924 | – |
| after-redis | 160.0 | 2.0 (-17%) | 3.8 (-44%) | 5.9 (-72%) | 0% | 924 | – |
| before-routing | 80.0 | 2.3 (≈ -4%) | 4.0 (-41%) | 5.4 (-75%) | 0% | 944 | 4.94% |
| after-replica | 80.0 | 2.3 (-4%) | 4.1 (-40%) | 5.6 (-74%) | 0% | 941 | 5.36% |

## categories: GET /api/v1/categories

| Step | req/s | p50 | p95 | p99 | Errors | Bytes/response | 304s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 50.0 | 1.7 | 4.9 | 12.4 | 0% | 1606 | – |
| after-db | 20.0 | 1.7 (≈ 0%) | 4.3 (≈ -12%) | 9.1 (≈ -27%) | 0% | 1606 | – |
| after-redis | 20.0 | 0.6 (-65%) | 1.1 (-78%) | 2.1 (-83%) | 0% | 1606 | – |
| before-routing | 10.0 | 0.8 (-53%) | 1.2 (-76%) | 2.7 (-78%) | 0% | 1606 | 0% |
| after-replica | 10.0 | 0.8 (-53%) | 1.2 (-76%) | 2.7 (-78%) | 0% | 1606 | 0% |

## my_orders: GET /api/v1/orders

| Step | req/s | p50 | p95 | p99 | Errors | Bytes/response | 304s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 100.0 | 3.6 | 7.7 | 18.9 | 0% | 5236 | – |
| after-db | 40.0 | 3.0 (-17%) | 6.3 (-18%) | 14.7 (-22%) | 0% | 5236 | – |
| after-redis | 40.0 | 3.1 (-14%) | 5.6 (-27%) | 8.2 (-57%) | 0% | 5236 | – |
| before-routing | 20.0 | 3.3 (≈ -8%) | 4.9 (-36%) | 6.6 (-65%) | 0% | 5236 | 0% |
| after-replica | 20.0 | 3.3 (-8%) | 5.3 (-31%) | 6.7 (-65%) | 0% | 5236 | 0% |

## create_order: POST /api/v1/orders

| Step | req/s | p50 | p95 | p99 | Errors | Bytes/response | 304s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 50.0 | 8.5 | 23.2 | 54.9 | 0% | 1120 | – |
| after-db | 20.0 | 11.9 (≈ +40%) | 20.4 (≈ -12%) | 65.7 (≈ +20%) | 0% | 1117 | – |
| after-redis | 20.0 | 11.4 (≈ +34%) | 15.9 (≈ -31%) | 24.8 (-55%) | 0% | 1121 | – |
| before-routing | 10.0 | 11.5 (≈ +35%) | 14.4 (≈ -38%) | 18.5 (-66%) | 0% | 1120 | 0% |
| after-replica | 10.0 | 12.1 (≈ +42%) | 15.0 (-35%) | 18.1 (-67%) | 0% | 1120 | 0% |

## What the system did

Medians over each step's runs, during the measured part only. CPU is each container's average, with its peak in brackets; 100% is one core, and Docker had 8. Then the most connections to the app's database, the most of them waiting at once, and the wait seen most often.

| Step | api | postgres | db-replica | inventory | inventory-db | redis | k6 | Connections | Waiting | Top wait |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 250% (262%) | 101% (120%) | – | 29% (34%) | 11% (22%) | – | 52% (98%) | 69 | 4 | Client:ClientRead |
| after-db | 108% (163%) | 34% (56%) | – | 18% (28%) | 6% (15%) | – | 31% (52%) | 52 | 1 | Client:ClientRead |
| after-redis | 85% (112%) | 15% (29%) | – | 17% (25%) | 6% (17%) | 2% (3%) | 29% (44%) | 8 | 1 | IO:WalSync |
| before-routing | 46% (55%) | 9% (13%) | 2% (6%) | 9% (11%) | 4% (7%) | 1% (3%) | 16% (27%) | 4 | 0 | – |
| after-replica | 48% (52%) | 7% (13%) | 4% (11%) | 10% (11%) | 4% (9%) | 1% (2%) | 16% (27%) | 4 | 0 | – |

## Notes

- after-db ran differently from the reference step baseline, so its numbers don't compare: rate 400, not 1000.
- after-redis ran differently from the reference step baseline, so its numbers don't compare: rate 400, not 1000.
- before-routing ran differently from the reference step baseline, so its numbers don't compare: rate 200, not 1000.
- after-replica ran differently from the reference step baseline, so its numbers don't compare: rate 200, not 1000.
- after-db used 12 Docker CPUs; reference step baseline used 8. CPU allocation also differs.
- after-redis used 12 Docker CPUs; reference step baseline used 8. CPU allocation also differs.
- before-routing used 12 Docker CPUs; reference step baseline used 8. CPU allocation also differs.
- after-replica used 12 Docker CPUs; reference step baseline used 8. CPU allocation also differs.
- after-db: 1 invalid run at 400 req/s left out.
- baseline: 1 invalid run at 1000 req/s left out.
- before-routing: 1 earlier run at 400 req/s left out; the step was measured again at 200 req/s.
- Left out of the tables: etag-off, etag-on, runs of only part of the mix (ONLY), which measure that part on its own.
