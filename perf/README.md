# Requirement 3 load test (task R3-02)

[`load.js`](load.js) is the k6 script behind every Requirement 3 measurement. It runs an open model: requests
start at a fixed rate whether or not earlier ones have answered, so a slower system can't receive fewer requests
and hide its slowness. Each run reports p50, p95 and p99 per endpoint and saves them to [`results/`](results).

## Run it

From the repository root, against a running stack (`docker compose up --build --wait`) with the R3-01 seed loaded:

```sh
k6 run -e LABEL=baseline -e COMMIT=$(git describe --tags --always --dirty) perf/load.js
# or without installing k6; -u lets the container write to perf/results on Linux
docker run --rm -i --network host -u "$(id -u):$(id -g)" -v "$PWD/perf:/shop/perf" -w /shop grafana/k6:2.3.0 \
  run -e LABEL=baseline -e COMMIT=$(git describe --tags --always --dirty) perf/load.js
```

A run takes about four minutes with the defaults: setup, a 30 s warm-up, then 3 minutes measured.

| Variable | Default | Meaning |
| --- | --- | --- |
| `API_URL` | `http://localhost:8080` | The API under test |
| `RATE` | `200` | Requests per second, all endpoints together. Pick one the baseline barely handles, then keep it for every later run |
| `WARMUP` | `30s` | The same traffic first, left out of every number, since the JIT, connection pools and Postgres's cache start cold. `0` turns it off |
| `DURATION` | `3m` | The measured part |
| `LABEL` | `run` | Starts the results file's name: the step, such as `baseline` or `after-redis` |
| `COMMIT` | `unknown` | The commit under test, recorded in the results |
| `CUSTOMER_EMAILS` | none | Seeded customers, comma-separated, who read "my orders" and place the orders |
| `CUSTOMER_PASSWORD` | none | Their shared password; required with `CUSTOMER_EMAILS` |
| `CUSTOMERS` | `5` | Without `CUSTOMER_EMAILS`, how many fresh customers setup registers |
| `RESULTS_DIR` | `perf/results` | Where the results file goes; it must exist, because k6 doesn't create folders |

## The traffic

| Endpoint tag | Request | Share | At 200 req/s |
| --- | --- | --- | --- |
| `product_list` | `GET /api/v1/products` | 40% | 80/s |
| `product_details` | `GET /api/v1/products/{id}` | 40% | 80/s |
| `categories` | `GET /api/v1/categories` | 5% | 10/s |
| `my_orders` | `GET /api/v1/orders` | 10% | 20/s |
| `create_order` | `POST /api/v1/orders` | 5% | 10/s |

- Each endpoint is its own `constant-arrival-rate` scenario, so each one holds its own rate and a slow endpoint
  can't take VUs from the others.
- Popularity follows Zipf: the product or category at rank r gets requests in proportion to 1/r, so a few take
  most of the traffic, as a cache will see in R3-07. Ranks go by ID, so the same products are popular in every run.
- A list request shows all products one time in ten, otherwise a category. It asks for page 1 60% of the time,
  page 2 20%, page 3 10%, and a deeper page up to the last 10%, so deep `OFFSET`s show up too.
- `my_orders` asks for page 1, or page 2 one time in ten. A new order holds one to three products, one unit each.
- Setup reads every category and product through the API first, so the script needs no IDs from the seed.

## Customers

"My orders" only measures something for customers with real order histories, so list some of the seed's heavy
customers and some typical ones in `CUSTOMER_EMAILS`. Without it, setup registers fresh customers whose order lists
start empty. That's fine for trying the script, and the results say so.

The API allows 5 logins a minute from one address, so setup waits a minute for every five customers past the first
five. Set `LOGIN_RATE_LIMIT_PERMIT_LIMIT=1000` in `.env` and run `docker compose up -d api` to skip the wait. Tokens
last 15 minutes, and setup stops if they would expire before the run ends.

## Results

Each run prints a table and writes the same numbers to `perf/results/<LABEL>-<start time>.json`:

| Field | Meaning |
| --- | --- |
| `overall`, `endpoints.<tag>` | `requests`, `rps`, `p50Ms`, `p95Ms`, `p99Ms`, `maxMs`, `errorRate` (0 to 1) and `bytesReceived`, for the measured part only |
| `settings`, `dataset` | The rate, warm-up and duration, and how many categories, products and customers the run used |
| `droppedIterations` | Requests k6 couldn't start on time because every VU was busy |
| `vus` | VUs allocated, preallocated and the cap; more allocated than preallocated means responses slowed enough that k6 added VUs |
| `valid`, `failedThresholds` | Whether the run counts, and why not |

A run counts only with under 1% errors and no dropped iterations. Otherwise fast failures are mixed into the
latencies, or k6 didn't hold the rate. k6 then exits with 99 instead of 0 and still saves the file, marked
`"valid": false`. A failed setup exits with 107 and saves nothing.

Run each step three times on its tagged commit, and put the median of the three runs in the plan's results table.
Commit the result files you keep; the plan uses them as exhibits.

While it runs, watch which container sits at 100% CPU and whether Postgres connections wait:

```sh
docker stats
docker compose exec postgres psql -U shop -d shop -c "select state, wait_event_type, count(*) from pg_stat_activity group by 1, 2"
```

k6 shares the machine with the stack. If k6 itself sits near 100% CPU, the load generator is the limit, not the shop.
