# Requirement 3: before and after

Built by `perf/compare.sh` on 2026-11-11 23:34 UTC from the latest 3 valid runs of each step. Each number is the median of those runs, in milliseconds, with the change from the baseline in brackets. **≈** marks a change within run-to-run variation: a change counts only when every run of the step was faster than every run of the baseline, or every one slower. **?** means one side has fewer than three runs, too few to tell. In the endpoint tables, Bytes/response is what a response cost on the wire, headers included, and 304s the share answered 304 Not Modified with no body (task R3-09).

## Overall

| Step | Runs | Commit | p50 | p95 | p99 | Errors | Redis keyspace hit ratio |
| --- | --- | --- | --- | --- | --- | --- | --- |
| before-routing | 3 | r3-before-read-routing | 2.2 | 9.9 | 12.5 | 0% | 66.9% |
| after-replica | 3 | r3-after-read-routing | 2.2 (≈ 0%) | 10.3 (+4%) | 13.1 (≈ +5%) | 0% | 66.9% |

## product_list: GET /api/v1/products

| Step | req/s | p50 | p95 | p99 | Errors | Bytes/response | 304s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| before-routing | 80.0 | 1.1 | 3.0 | 4.7 | 0% | 11012 | 0% |
| after-replica | 80.0 | 1.1 (≈ 0%) | 3.1 (≈ +3%) | 4.9 (≈ +4%) | 0% | 11003 | 0% |

## product_details: GET /api/v1/products/{id}

| Step | req/s | p50 | p95 | p99 | Errors | Bytes/response | 304s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| before-routing | 80.0 | 2.3 | 4.0 | 5.4 | 0% | 944 | 4.94% |
| after-replica | 80.0 | 2.3 (≈ 0%) | 4.1 (≈ +2%) | 5.6 (≈ +4%) | 0% | 941 | 5.36% |

## categories: GET /api/v1/categories

| Step | req/s | p50 | p95 | p99 | Errors | Bytes/response | 304s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| before-routing | 10.0 | 0.8 | 1.2 | 2.7 | 0% | 1606 | 0% |
| after-replica | 10.0 | 0.8 (≈ 0%) | 1.2 (≈ 0%) | 2.7 (≈ 0%) | 0% | 1606 | 0% |

## my_orders: GET /api/v1/orders

| Step | req/s | p50 | p95 | p99 | Errors | Bytes/response | 304s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| before-routing | 20.0 | 3.3 | 4.9 | 6.6 | 0% | 5236 | 0% |
| after-replica | 20.0 | 3.3 (≈ 0%) | 5.3 (≈ +8%) | 6.7 (≈ +2%) | 0% | 5236 | 0% |

## create_order: POST /api/v1/orders

| Step | req/s | p50 | p95 | p99 | Errors | Bytes/response | 304s |
| --- | --- | --- | --- | --- | --- | --- | --- |
| before-routing | 10.0 | 11.5 | 14.4 | 18.5 | 0% | 1120 | 0% |
| after-replica | 10.0 | 12.1 (≈ +5%) | 15.0 (≈ +4%) | 18.1 (≈ -2%) | 0% | 1120 | 0% |

## What the system did

Medians over each step's runs, during the measured part only. CPU is each container's average, with its peak in brackets; 100% is one core, and Docker had 12. Then the most connections to the app's database, the most of them waiting at once, and the wait seen most often.

| Step | api | postgres | db-replica | inventory | inventory-db | redis | k6 | Connections | Waiting | Top wait |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| before-routing | 46% (55%) | 9% (13%) | 2% (6%) | 9% (11%) | 4% (7%) | 1% (3%) | 16% (27%) | 4 | 0 | – |
| after-replica | 48% (52%) | 7% (13%) | 4% (11%) | 10% (11%) | 4% (9%) | 1% (2%) | 16% (27%) | 4 | 0 | – |

## Notes

- before-routing: 1 earlier run at 400 req/s left out; the step was measured again at 200 req/s.
