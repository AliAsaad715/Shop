# Requirement 3: before and after

Built by `perf/compare.sh` on 2026-11-11 17:38 UTC from the latest 3 valid runs of each step. Each number is the median of those runs, in milliseconds, with the change from the baseline in brackets. **≈** marks a change within run-to-run variation: a change counts only when every run of the step was faster than every run of the baseline, or every one slower. **?** means one side has fewer than three runs, too few to tell.

## Overall

| Step | Runs | Commit | p50 | p95 | p99 | Errors | Redis keyspace hit ratio |
| --- | --- | --- | --- | --- | --- | --- | --- |
| after-db | 3 | r3-before-redis | 2.7 | 10.8 | 18.0 | 0% | – |
| baseline | 3 | r3-rebaseline | 2.8 (≈ +4%) | 11.3 (≈ +5%) | 23.2 (≈ +29%) | 0% | – |
| after-redis | 3 | r3-after-redis | 1.9 (-30%) | 9.8 (-9%) | 13.3 (≈ -26%) | 0% | 73.7% |

## product_list: GET /api/v1/products

| Step | req/s | p50 | p95 | p99 | Errors |
| --- | --- | --- | --- | --- | --- |
| after-db | 160.0 | 2.4 | 4.9 | 12.3 | 0% |
| baseline | 400.0 | 2.8 (+17%) | 6.9 (+41%) | 18.0 (+46%) | 0% |
| after-redis | 160.0 | 1.1 (-54%) | 3.2 (-35%) | 5.2 (-58%) | 0% |

## product_details: GET /api/v1/products/{id}

| Step | req/s | p50 | p95 | p99 | Errors |
| --- | --- | --- | --- | --- | --- |
| after-db | 160.0 | 2.8 | 5.7 | 14.1 | 0% |
| baseline | 400.0 | 2.4 (-14%) | 6.8 (+19%) | 21.3 (+51%) | 0% |
| after-redis | 160.0 | 2.0 (-29%) | 3.8 (-33%) | 5.9 (-58%) | 0% |

## categories: GET /api/v1/categories

| Step | req/s | p50 | p95 | p99 | Errors |
| --- | --- | --- | --- | --- | --- |
| after-db | 20.0 | 1.7 | 4.3 | 9.1 | 0% |
| baseline | 50.0 | 1.7 (≈ 0%) | 4.9 (≈ +14%) | 12.4 (≈ +36%) | 0% |
| after-redis | 20.0 | 0.6 (-65%) | 1.1 (-74%) | 2.1 (-77%) | 0% |

## my_orders: GET /api/v1/orders

| Step | req/s | p50 | p95 | p99 | Errors |
| --- | --- | --- | --- | --- | --- |
| after-db | 40.0 | 3.0 | 6.3 | 14.7 | 0% |
| baseline | 100.0 | 3.6 (+20%) | 7.7 (+22%) | 18.9 (+29%) | 0% |
| after-redis | 40.0 | 3.1 (≈ +3%) | 5.6 (≈ -11%) | 8.2 (≈ -44%) | 0% |

## create_order: POST /api/v1/orders

| Step | req/s | p50 | p95 | p99 | Errors |
| --- | --- | --- | --- | --- | --- |
| after-db | 20.0 | 11.9 | 20.4 | 65.7 | 0% |
| baseline | 50.0 | 8.5 (≈ -29%) | 23.2 (≈ +14%) | 54.9 (≈ -16%) | 0% |
| after-redis | 20.0 | 11.4 (≈ -4%) | 15.9 (≈ -22%) | 24.8 (-62%) | 0% |

## What the system did

Medians over each step's runs, during the measured part only. CPU is each container's average, with its peak in brackets; 100% is one core, and Docker had 12. Then the most connections to the app's database, the most of them waiting at once, and the wait seen most often.

| Step | api | postgres | inventory | inventory-db | redis | k6 | Connections | Waiting | Top wait |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| after-db | 108% (163%) | 34% (56%) | 18% (28%) | 6% (15%) | – | 31% (52%) | 52 | 1 | Client:ClientRead |
| baseline | 250% (262%) | 101% (120%) | 29% (34%) | 11% (22%) | – | 52% (98%) | 69 | 4 | Client:ClientRead |
| after-redis | 85% (112%) | 15% (29%) | 17% (25%) | 6% (17%) | 2% (3%) | 29% (44%) | 8 | 1 | IO:WalSync |

## Notes

- baseline ran differently from the reference step after-db, so its numbers don't compare: rate 1000, not 400.
- baseline used 8 Docker CPUs; reference step after-db used 12. CPU allocation also differs.
- after-db: 1 invalid run at 400 req/s left out.
- baseline: 1 invalid run at 1000 req/s left out.
