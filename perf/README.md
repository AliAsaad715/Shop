# Requirement 3 performance tests

These tools measure each step of Requirement 3 the same way, on the same data, so the before/after numbers compare:

| File | Task | What it does |
| --- | --- | --- |
| [`seed.sh`](seed.sh), [`seed.sql`](seed.sql) | R3-01 | Loads the dataset: 50,000 customers, 5,000 products, 500,000 orders, skewed and the same every time |
| [`load.js`](load.js) | R3-02 | The k6 load test: a fixed rate of mixed traffic, with p50, p95 and p99 per endpoint |
| [`snapshot.sh`](snapshot.sh) | R3-02b | Saves the seeded databases once, and restores them before every step |
| [`run.sh`](run.sh) | R3-02b | Runs a step: restores the snapshot, runs `load.js` three times, and records what the system did |
| [`compare.sh`](compare.sh) | R3-02b | Builds [`results/comparison.md`](results), the before/after report with a noise check |

They need Docker, bash and jq (`sudo apt install jq`, or `brew install jq`); `run.sh` runs k6 from its Docker image.

## A step from start to finish

From the repository root, with the stack up (`docker compose up --build --wait`):

```sh
perf/seed.sh                 # once, on a fresh stack: about 100 s
perf/snapshot.sh save        # once, right after seeding

perf/run.sh baseline 1000    # done: see results/baseline/README.md
# change one thing, commit it, then
git tag r3-after-db
perf/run.sh after-db 1000    # three runs, about 12 minutes
perf/compare.sh              # writes perf/results/comparison.md
```

Copy each step's overall row from `comparison.md` into the plan's results table, and commit `perf/results/`: the
plan uses the files as exhibits. Every step runs at 1,000 req/s, the rate the baseline barely handled
([how it was chosen](results/baseline/README.md#the-rate)).

## The dataset: seed.sh

`perf/seed.sh` loads `perf/seed.sql` into the app's database and stocks the warehouse, in about 100 seconds. The data
is about 900 MB:

| Rows | Count | Skew |
| --- | --- | --- |
| Customers | 50,000 | `customer1@shop.test` has about 2,300 orders, `customer100` about 90, the median customer 7 |
| Categories | 20 | The 4 every database has, and 16 more, from about 680 products down to about 170 |
| Products | 5,000 | Product 1 is on about 21,000 order lines, product 100 on about 1,000, product 5,000 on about 150 |
| Orders | 500,000 | Over the two years before 2026-11-01; one in twenty cancelled, the rest moved on by age |
| Order lines | about 1,500,000 | One to five per order, mostly one unit each |

- `setseed()` makes every run produce the same rows, checked by seeding twice and comparing checksums.
- Product IDs are `md5('product-' || n)`, the IDs the warehouse stocks (`tests/proof/app-seed.sql`). The six demo
  products get the same 1,000,000 units, so the load test never fails on stock; that undoes the R2 proof's sold-out
  products, which [`tests/proof/README.md`](../tests/proof/README.md) shows how to reseed.
- Every seeded customer signs in with `Shopper123!`: one hash for all, made by the app's own `PasswordHasher`.
- Orders, their lines and their status history follow the app's own rules: totals match the lines, and every status
  change is one the app allows.
- It refuses to run on a database that's already seeded.

## The snapshot: snapshot.sh

`perf/snapshot.sh save` copies the app's database and the warehouse's together, since orders and reservations must
match. Each copy stays inside its own Postgres as `<database>_snapshot` (`CREATE DATABASE ... TEMPLATE`), so saving
or restoring takes seconds and nothing leaves the Docker volumes. `perf/snapshot.sh status` shows each snapshot's
size and when and at which commit it was saved.

`perf/snapshot.sh restore` puts the seeded data back, so test orders from one step never leak into the next:

1. It stops `api` and `inventory`, because a database can only be copied while nobody is connected. Any other client,
   such as an IDE, is disconnected.
2. It copies each snapshot to a temporary database, and only then swaps it in for the live one.
3. It empties Redis, once there is one (R3-07), so no step inherits another's cache.
4. It starts the warehouse and the app, and waits until both answer.

Each snapshot takes as much disk as its database. Database-level settings (`ALTER DATABASE ... SET`) aren't copied;
the project has none.

## A step: run.sh

`perf/run.sh <label> <rate>` restores the snapshot, then runs `load.js` three times. Runs 2 and 3 continue from run
1's data, as the plan's restore before each label intends. Run 1 starts with a freshly restarted app, which is what
the 30 s warm-up is for.

| Variable | Default | Meaning |
| --- | --- | --- |
| `RUNS` | `3` | How many runs |
| `CUSTOMER_EMAILS`, `CUSTOMER_PASSWORD` | five seeded customers, `Shopper123!` | Who reads "my orders" and places orders: `customer1` and `customer2` (the heaviest), `customer100`, `customer5000` and `customer40000` |
| `WARMUP`, `DURATION`, `CUSTOMERS` | as in `load.js` | Passed on to `load.js` |

While each run is measured, `run.sh` samples every container's CPU and the app's database connections every two
seconds. The database sampler keeps one session open for the whole run, so it still sees the database when the app's
pool has taken every connection Postgres allows. It saves what it saw beside the k6 result, in
`perf/results/<label>/<label>-<start time>.system.json`:

| Field | Meaning |
| --- | --- |
| `cpu.containers.<service>` | `avgPercent` and `maxPercent` of one core, during the measured part; `cpu.cores` is how many cores Docker has |
| `database` | `maxConnections`, `avgActive`, `maxActive` and `maxWaiting` connections to the app's database, and `waitEvents`: how often each wait was seen |
| `topQueries` | The ten queries with the most total time, from `pg_stat_statements` (task R3-04), counted over the whole run |
| `redis` | `hits`, `misses` and `hitRatio` from Redis (task R3-07); hits in HybridCache's in-memory layer never reach Redis |
| `notes` | What couldn't be recorded yet, such as top queries before R3-04 |

Ctrl+C stops k6, which still saves the run, marked invalid because it was cut short, and `run.sh` starts no more
runs. It exits with 0 when every run was valid.

## The report: compare.sh

`perf/compare.sh` reads every k6 result under `perf/results/` and writes `perf/results/comparison.md`. For each
label it takes the latest three valid runs: their median p50, p95 and p99, overall and per endpoint, and the change
from the baseline. `perf/compare.sh <label>` picks another label as the baseline.

The noise check marks a change **≈** unless every run of the step was faster than every run of the baseline, or
every one slower. With three runs each, no change does that by chance more than one time in ten; anything less is
within run-to-run variation. A step with fewer than three valid runs shows **?** instead.

The report also says what the system did in each step: CPU per container, database connections and waits, and the
Redis hit ratio. Its notes flag what makes numbers not compare: a different rate, duration or dataset than the
baseline, invalid runs left out, or customers registered for the run instead of seeded ones.

## The load test: load.js

`run.sh` is the usual way to run it. To run it once by hand:

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
| `WARMUP` | `30s` | Climbs from a tenth of the rate to all of it, left out of every number, since the JIT, connection pools and Postgres's cache start cold. `0` turns it off |
| `DURATION` | `3m` | The measured part |
| `LABEL` | `run` | Starts the results file's name: the step, such as `baseline` or `after-redis` |
| `COMMIT` | `unknown` | The commit under test, recorded in the results |
| `CUSTOMER_EMAILS` | none | Seeded customers, comma-separated, who read "my orders" and place the orders |
| `CUSTOMER_PASSWORD` | none | Their shared password; required with `CUSTOMER_EMAILS` |
| `CUSTOMERS` | `5` | Without `CUSTOMER_EMAILS`, how many fresh customers setup registers |
| `RESULTS_DIR` | `perf/results` | Where the results file goes; it must exist, because k6 doesn't create folders |

### The traffic

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

### Customers

"My orders" only measures something for customers with real order histories, so list some of the seed's heavy
customers and some typical ones in `CUSTOMER_EMAILS`; `run.sh` does that for you. Without it, setup registers fresh
customers whose order lists start empty. That's fine for trying the script, and the results say so.

The API allows 5 logins a minute from one address, so setup waits a minute for every five customers past the first
five. Set `LOGIN_RATE_LIMIT_PERMIT_LIMIT=1000` in `.env` and run `docker compose up -d api` to skip the wait. Tokens
last 15 minutes, and setup stops if they would expire before the run ends.

### Results

Each run prints a table and writes the same numbers to `<RESULTS_DIR>/<LABEL>-<start time>.json`:

| Field | Meaning |
| --- | --- |
| `overall`, `endpoints.<tag>` | `requests`, `rps`, `p50Ms`, `p95Ms`, `p99Ms`, `maxMs`, `errorRate` (0 to 1) and `bytesReceived`, for the measured part only |
| `settings`, `dataset` | The rate, warm-up and duration, and how many categories, products and customers the run used |
| `window` | When the measured part started and ended |
| `droppedIterations` | Requests k6 couldn't start on time because every VU was busy |
| `vus` | VUs allocated, preallocated and the cap; more allocated than preallocated means responses slowed enough that k6 added VUs |
| `valid`, `problems` | Whether the run counts, and why not |

A run counts only with under 1% errors, no dropped iterations, and its full length measured. Otherwise fast
failures are mixed into the latencies, k6 didn't hold the rate, or the run was stopped early. k6 exits with 99 when a
threshold fails, and with 105 when it is stopped; either way it saves the file, marked `"valid": false`. A failed
setup exits with 107 and saves nothing.

k6 shares the machine with the stack. If the `k6` container sits near 100% CPU, the load generator is the limit,
not the shop.
