# Requirement 3: before and after

Built by `perf/compare.sh` on 2026-11-08 18:33 UTC from the latest 3 valid runs of each step. Each number is the median of those runs, in milliseconds, with the change from the baseline in brackets. **≈** marks a change within run-to-run variation: a change counts only when every run of the step was faster than every run of the baseline, or every one slower. **?** means one side has fewer than three runs, too few to tell.

## Overall

| Step | Runs | Commit | p50 | p95 | p99 | Errors | Cache hit ratio |
| --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 3 | r3-baseline | 2.9 | 9.2 | 21.0 | 0% | – |

## product_list: GET /api/v1/products

| Step | req/s | p50 | p95 | p99 | Errors |
| --- | --- | --- | --- | --- | --- |
| baseline | 400.0 | 2.9 | 7.5 | 18.0 | 0% |

## product_details: GET /api/v1/products/{id}

| Step | req/s | p50 | p95 | p99 | Errors |
| --- | --- | --- | --- | --- | --- |
| baseline | 400.0 | 2.5 | 7.3 | 21.4 | 0% |

## categories: GET /api/v1/categories

| Step | req/s | p50 | p95 | p99 | Errors |
| --- | --- | --- | --- | --- | --- |
| baseline | 50.0 | 1.6 | 5.0 | 13.1 | 0% |

## my_orders: GET /api/v1/orders

| Step | req/s | p50 | p95 | p99 | Errors |
| --- | --- | --- | --- | --- | --- |
| baseline | 100.0 | 4.2 | 8.6 | 20.6 | 0% |

## create_order: POST /api/v1/orders

| Step | req/s | p50 | p95 | p99 | Errors |
| --- | --- | --- | --- | --- | --- |
| baseline | 50.0 | 8.2 | 17.5 | 39.4 | 0% |

## What the system did

Medians over each step's runs, during the measured part only. CPU is each container's average, with its peak in brackets; 100% is one core, and Docker had 8. Then the most connections to the app's database, the most of them waiting at once, and the wait seen most often.

| Step | api | postgres | inventory | inventory-db | k6 | Connections | Waiting | Top wait |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| baseline | 255% (269%) | 105% (118%) | 29% (34%) | 11% (23%) | 53% (99%) | 30 | 2 | Client:ClientRead |

## Notes

Nothing to flag: every step ran with the baseline's settings and data.

