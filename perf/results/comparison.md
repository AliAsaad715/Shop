# Requirement 3: before and after

Built by `perf/compare.sh` on 2026-11-10 18:45 UTC from the latest 3 valid runs of each step. Each number is the median of those runs, in milliseconds, with the change from the baseline in brackets. **≈** marks a change within run-to-run variation: a change counts only when every run of the step was faster than every run of the baseline, or every one slower. **?** means one side has fewer than three runs, too few to tell.

## Overall

| Step | Runs | Commit | p50 | p95 | p99 | Errors | Cache hit ratio |
| --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 3 | r3-rebaseline | 2.8 | 11.3 | 23.2 | 0% | – |

## product_list: GET /api/v1/products

| Step | req/s | p50 | p95 | p99 | Errors |
| --- | --- | --- | --- | --- | --- |
| baseline | 400.0 | 2.8 | 6.9 | 18.0 | 0% |

## product_details: GET /api/v1/products/{id}

| Step | req/s | p50 | p95 | p99 | Errors |
| --- | --- | --- | --- | --- | --- |
| baseline | 400.0 | 2.4 | 6.8 | 21.3 | 0% |

## categories: GET /api/v1/categories

| Step | req/s | p50 | p95 | p99 | Errors |
| --- | --- | --- | --- | --- | --- |
| baseline | 50.0 | 1.7 | 4.9 | 12.4 | 0% |

## my_orders: GET /api/v1/orders

| Step | req/s | p50 | p95 | p99 | Errors |
| --- | --- | --- | --- | --- | --- |
| baseline | 100.0 | 3.6 | 7.7 | 18.9 | 0% |

## create_order: POST /api/v1/orders

| Step | req/s | p50 | p95 | p99 | Errors |
| --- | --- | --- | --- | --- | --- |
| baseline | 50.0 | 8.5 | 23.2 | 54.9 | 0% |

## What the system did

Medians over each step's runs, during the measured part only. CPU is each container's average, with its peak in brackets; 100% is one core, and Docker had 8. Then the most connections to the app's database, the most of them waiting at once, and the wait seen most often.

| Step | api | postgres | inventory | inventory-db | k6 | Connections | Waiting | Top wait |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 250% (262%) | 101% (120%) | 29% (34%) | 11% (22%) | 52% (98%) | 69 | 4 | Client:ClientRead |

## Notes

- baseline: 1 invalid run left out.

