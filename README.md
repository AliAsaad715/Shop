# Shop

## Run locally

The Go warehouse is its own repository, [AliAsaad715/Inventory](https://github.com/AliAsaad715/Inventory),
included here as the `warehouse` submodule. Clone with
`git clone --recurse-submodules https://github.com/AliAsaad715/Shop.git`; in an existing clone, run
`git submodule update --init`.

Copy `.env.example` to `.env`, configure the admin account, and set `JWT_SIGNING_KEY`
to the base64 encoding of at least 32 random bytes. Generate it in PowerShell 7:

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Run `docker compose up --build`. The local HTTP demo is available at
`http://localhost:8080/docs`, through Nginx in front of three API instances (task R5-03). Keep `.env`, signing keys
and certificates out of Git.

For local HTTPS, start PostgreSQL with `docker compose up -d postgres`, configure a trusted
.NET development certificate with `dotnet dev-certs https --trust`, and run:

```powershell
$env:Jwt__SigningKey = '<your base64 signing key>'
dotnet run --project src/Shop.Api -- --migrate-and-seed
dotnet run --project src/Shop.Api --launch-profile https
```

Open `https://localhost:7080/docs`; HTTP on port 5080 redirects to that HTTPS listener.
The `http` launch profile and default Compose setup support HTTP-only local demos. Database initialization is
explicit in every environment, including Development; a normal API or worker run never migrates or seeds (R5-04).

## Browser security (task 13)

The API sends `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, and
`Referrer-Policy: no-referrer`, including on errors. Kestrel's `Server` header is disabled.
Authenticated responses use `Cache-Control: no-store`, including routing errors.

`/api/*` uses a strict Content Security Policy with `default-src 'none'`, no inline scripts
and no eval. `/docs` permits Scalar's local scripts, inline initialization and styles, and same-origin API calls. Scalar's external fonts are disabled so the UI does not require a CDN.

CORS permits only explicitly configured frontend origins. Development defaults to
`http://localhost:5173`. For Compose, change `FRONTEND_ORIGIN` in `.env`, then run
`docker compose up -d api1 api2 api3`. For a direct API run, configure `Cors:AllowedOrigins` or:

```powershell
$env:Cors__AllowedOrigins__0 = 'http://localhost:5173'
```

An origin contains the scheme, host and optional port, with no trailing slash or path.
Wildcards are rejected at startup. Production configuration without any allowed origins
grants no cross-origin access. Bearer authorization and JSON preflight requests are supported;
cookies are not enabled. Frontend JavaScript can read `Location` and `Retry-After`.
CORS controls browser access and does not replace endpoint authentication or authorization.

HTTPS redirection uses the configured or discovered HTTPS port. HTTP-only setups have no
redirect target. Production deployments must provide HTTPS through a Kestrel certificate or
a TLS proxy; the HTTP demo configuration does not provide production TLS. R5-05 processes forwarded
headers before HSTS/redirection only from the configured Docker network; direct local runs ignore them by default.
See Microsoft's [HTTPS deployment guidance](https://learn.microsoft.com/aspnet/core/security/enforcing-ssl?view=aspnetcore-10.0).

HSTS is sent for HTTPS in non-Development environments, with a 180-day maximum age.
It is omitted over HTTP and on the framework's excluded localhost addresses. Local
development does not enable HSTS or change the machine's trusted certificates automatically.

## Inventory contract (task R2-01)

The app and the Go warehouse share one file,
[`contracts/inventory/v1/inventory.proto`](contracts/inventory/v1/inventory.proto).
Changes must be additive: new fields take new numbers, and existing numbers are never reused.

- **C#**: Grpc.Tools compiles the client into `Shop.Modules.Products` on every build, as
  internal types in the `Inventory.V1` namespace, so no other step is needed.
- **Go**: `warehouse/gen` is written by `buf generate` and committed in the Inventory repository. After
  changing the proto, regenerate it with [buf](https://buf.build/docs/cli/installation/) and Go 1.27, commit
  and push it in `warehouse/`, then commit the new submodule pointer in Shop with `git add warehouse`:

```sh
cd warehouse
buf generate
```

CI ([contracts.yml](.github/workflows/contracts.yml)) runs `buf lint`, runs `buf breaking`
against `main`, and fails when `warehouse/gen` doesn't match the proto. The same checks from the
repository root:

```sh
buf lint contracts
buf breaking contracts --against '.git#branch=main,subdir=contracts'
```

## Warehouse service (task R2-02)

[`warehouse/`](warehouse), the Inventory submodule, is the Go service that owns stock. It serves the
contract over gRPC on port 50051 and keeps a pgx connection pool to its own PostgreSQL database; R2-04
implements `GetStock`, `Reserve` and `Release` on it. Compose runs it as the `inventory` service (task R2-09).

| Variable | Default | Meaning |
| --- | --- | --- |
| `INVENTORY_DATABASE_URL` | required | The warehouse database, as a `postgres://` URL or a `key=value` string; pool settings such as `pool_max_conns` go here too |
| `INVENTORY_GRPC_ADDR` | `:50051` | Where the gRPC server listens |
| `INVENTORY_LOG_LEVEL` | `info` | `debug`, `info`, `warn` or `error`; logs are JSON lines on stdout |
| `INVENTORY_DELAY_MS` | `0` | Demos only: holds each inventory call this many milliseconds before handling it; health checks never wait |

- **Health**: the standard `grpc.health.v1.Health` service answers `SERVING` only while the database
  answers a ping, checked every 5 s, both for the server (`""`) and for `inventory.v1.InventoryService`.
  The image's `HEALTHCHECK` runs `/warehouse healthcheck`, since the distroless image has no shell or curl.
- **Stopping**: on SIGTERM, health turns `NOT_SERVING`, new calls are refused, and calls in flight get
  5 s to finish before they are cancelled, well before Docker's SIGKILL at 10 s.
- **Logs**: one line per call with the method, gRPC code, duration and the call's `order_id`, so an
  order can be followed from the app's logs. Health checks log only at `debug`.

Test it and build the image with Go 1.27 and Docker. `TestStorePostgres` (R2-05) starts a disposable PostgreSQL
with Testcontainers, so Docker must be running:

```sh
cd warehouse
go vet ./...
go test -race ./...
docker build -t shop-warehouse .
```

## Warehouse database and demo seed (task R2-03)

[`warehouse/db/`](warehouse/db) holds the warehouse's schema and seed as plain SQL. Compose mounts the
folder at `/docker-entrypoint-initdb.d`, so the `inventory-db` container runs them in name order the first
time it starts on an empty volume. Each file is one transaction. A volume that already holds a database
skips them; the R2-09 section shows how to start it again.

The six demo products have fixed IDs in both databases, so orders can name them in the app and
reserve them in the warehouse:

| ID ends in | Product | Warehouse stock |
| --- | --- | --- |
| `…0201` | Wireless Headphones | 25 |
| `…0202` | Cotton T-Shirt | 100 |
| `…0203` | Ceramic Mug | 40 |
| `…0204` | Field Guide to Distributed Systems | 15 |
| `…0205` | Limited Edition Desk Lamp (product X, for the race demo) | 1 |
| `…0206` | Trail Running Shoes (sold out) | 0 |

All IDs start `019a2180-0000-7000-8000-000000000`. The migrator adds them when `DemoSeed__Enabled` is `true`,
which compose sets by default (`DEMO_SEED_ENABLED` in `.env`). Production and the tests leave it off.
Existing products are never changed by repeated initialization. `DemoSeedTests` fails if `002_seed.sql` and the app's
list of demo products drift apart. Products created later through the API have no stock row, so the
warehouse counts them as 0 available.

## Warehouse client (task R2-06)

`IInventoryService` is a gRPC client of the warehouse, inside `Shop.Modules.Products`; the Requirement 1
placeholder is gone. Orders and other modules still call only the interface.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Inventory:Address` | `http://localhost:50051` in Development; required otherwise | The warehouse's gRPC address. Compose sets `http://inventory:50051`. |
| `Inventory:GetStockTimeout` | `00:00:00.500` | Deadline for one stock lookup |
| `Inventory:ReserveTimeout` | `00:00:02` | Deadline for one reservation |
| `Inventory:ReleaseTimeout` | `00:00:02` | Deadline for one release |

The app doesn't start without a valid address, or with a timeout outside 0 to 30 seconds. Each deadline
covers the whole call, and `UNAVAILABLE` is retried inside it, up to 3 attempts with backoff. Failures
reach callers as `InventoryUnavailableException` (warehouse down or unreachable), `InventoryTimeoutException`
(deadline passed, so a reservation may still have landed) or `InventoryException` (anything else).

## Warehouse failures (task R2-07)

An order is saved only after the warehouse reserved its stock, and every other outcome leaves the stock as it
was. Errors keep the Problem Details format with a `code`:

| Case | What the app does | Client sees |
| --- | --- | --- |
| Not enough stock | No order saved | 409 `orders.insufficient_stock`, with `shortages` listing `productId`, `requested` and `available` |
| Warehouse down | Releases in case the reserve landed; no order saved | 503 `orders.warehouse_unavailable` with `Retry-After: 5` |
| Warehouse slow (deadline passed) | Releases in case the reserve landed; no order saved | 504 `orders.warehouse_timeout` |
| Save fails after a reserve | Releases | 500 `common.internal_error` |
| Admin cancels an order | Releases | 200 |

A release the warehouse can't take is written to `orders.pending_releases`, and a background worker retries it
until the warehouse accepts. It looks every 5 s (`Orders:PendingReleases:PollInterval`), and after a failed
retry waits 5 s, then twice as long each time, up to 5 minutes.

`/health` also asks the warehouse's standard gRPC health service. When the warehouse is down or not serving it
reports `Degraded`, still with status 200, so browsing keeps working.

## Product stock (task R2-08)

`GET /api/v1/products/{id}` asks the warehouse for the product's stock and adds it to the product:

| `stockStatus` | `available` | When |
| --- | --- | --- |
| `in_stock` | units left | The warehouse has at least one |
| `out_of_stock` | `0` | None left, or a product the warehouse has never stocked |
| `unknown` | `null` | The warehouse is down or missed the 500 ms deadline; the product still loads with 200 |

The product list leaves stock out, so a page of products never makes one warehouse call per product.

## Compose services (task R2-09)

`docker compose up` runs four containers; add `--wait` to return once every health check passes.

| Service | Networks | From the host | Starts after |
| --- | --- | --- | --- |
| `api` | `default` | `localhost:8080` | `postgres` healthy, and `inventory` started but not awaited, since the app runs Degraded without it |
| `postgres` | `default` | `127.0.0.1:5433` | — |
| `inventory` | `default`, `inventory-data` | not published | `inventory-db` healthy |
| `inventory-db` | `inventory-data` (internal) | not published | — |

The app reaches the warehouse only over gRPC at `inventory:50051`. `inventory-db` is on a network the app
isn't on, so the app has no route to the warehouse's data. Its settings come from `.env`
(`INVENTORY_DB_NAME`, `INVENTORY_DB_USER`, `INVENTORY_DB_PASSWORD`), and the warehouse receives the password
as `PGPASSWORD`.

```sh
docker compose stop inventory    # warehouse down: orders answer 503, product stock is unknown
docker compose start inventory
docker compose exec inventory-db psql -U inventory -d inventory -c 'select * from stock_levels'
```

To reseed the warehouse, remove only its volume; the app's database is untouched:

```sh
docker compose rm -sf inventory-db && docker volume rm shop_inventory-db-data && docker compose up -d
```

## Proof kit (task R2-10)

[`tests/proof/README.md`](tests/proof/README.md) walks through the six interview steps: the k6 race for the last
unit of product X ([`order-race.js`](tests/proof/order-race.js)), the warehouse stopped, the warehouse slow
with `INVENTORY_DELAY_MS`, recovery, and the test suites.

## Requirement 3 dataset (task R3-01)

`perf/seed.sh` loads the data every Requirement 3 measurement runs on: 50,000 customers, 5,000 products in 20
categories, 500,000 orders and about 1.5 million order lines, skewed so a few customers and products take most of the
orders. `setseed()` makes it the same every time, and the warehouse stocks every product with 1,000,000 units, the
demo products included, which replaces the R2 proof's sold-out stock. Seeded customers are `customer1@shop.test` to
`customer50000@shop.test`, all with the password `Shopper123!`. [`perf/README.md`](perf/README.md#the-dataset-seedsh)
has the details.

```sh
perf/seed.sh                    # once, on a fresh stack: about 100 s
perf/snapshot.sh save
```

## Load test (task R3-02)

[`perf/load.js`](perf/load.js) is the k6 script for Requirement 3's measurements: an open model at a fixed rate,
200 requests per second by default, mostly catalog browsing with some "my orders" and a little ordering. Each run
prints p50, p95 and p99 per endpoint and saves them to `perf/results/`. [`perf/README.md`](perf/README.md) has the
settings, the traffic mix and how to read a run.

```sh
k6 run -e LABEL=baseline -e COMMIT=$(git describe --tags --always --dirty) perf/load.js
```

## Before/after tooling (task R3-02b)

Three scripts make every step of Requirement 3 start from the same data and record what the system did.
`perf/snapshot.sh save` keeps a copy of the seeded databases. `perf/run.sh <label> <rate>` restores it, runs the
load test three times, and saves each container's CPU, the database connections, the top queries and the Redis hits
beside each result. `perf/compare.sh` writes `perf/results/comparison.md`: per endpoint, the median of the three
runs, the change from the baseline, and a noise check. They need Docker and jq; [`perf/README.md`](perf/README.md)
explains each one.

```sh
perf/snapshot.sh save           # once, right after seeding
perf/run.sh baseline 1000       # on the tagged commit of each step
perf/compare.sh
```

## Baseline (task R3-03)

At 1,000 requests per second on the seeded data, the app as it is answers with a p50 of 2.9 ms, a p95 of 9.2 ms and a
p99 of 21.0 ms, the median of three runs on the `r3-baseline` tag; at 1,200 it collapses. Every later step runs at the
same 1,000 req/s. [`perf/results/baseline/README.md`](perf/results/baseline/README.md) has the runs, how the rate was
chosen, and what the system did: the api used 2.5 cores to Postgres's 1, much of it likely spent logging every SQL
command.

## Query statistics (task R3-04)

Two ways to see what a request costs the database:

- **pg_stat_statements.** Compose's Postgres loads it at startup and counts every statement's calls and time;
  `perf/top-queries.sh` lists the costliest, and `perf/run.sh` saves the top ten beside every run. A Postgres container
  created before this needs `docker compose up -d postgres` once; the data volume stays.
- **X-Db-Queries.** In Development, every response says how many SQL commands its request sent through EF Core,
  across all the modules' DbContexts, so an N+1 shows up as a number. A `DbCommandInterceptor` counts them; in other
  environments neither the counting nor the header exists, so production and the load tests never pay for it.

```sh
perf/top-queries.sh 5
curl -si http://localhost:5080/api/v1/products | grep -i x-db-queries   # dotnet run, Development: X-Db-Queries: 2
```

The product list sends 2 commands (the page and its total), product details 1, a login 2 and "my orders" 2.

## Catalog paging (task R3-05)

`GET /api/v1/products` pages two ways. A numbered page (`page`) comes with `totalCount`, as before. Every page also
returns `nextCursor`, and `cursor=<nextCursor>` asks for the page after it: keyset pagination, which finds that page
through the index at any depth and skips the count, so `page` and `totalCount` come back null. `nextCursor` is null on
the last page, and `page` with `cursor` is a 422.

```sh
curl -s "$API/api/v1/products?category=$CATEGORY&pageSize=20"              # page 1, totalCount, nextCursor
curl -s "$API/api/v1/products?category=$CATEGORY&pageSize=20&cursor=$NEXT"  # the next 20 products
```

Products are listed newest first, and those created at the same instant by descending ID, matching the indexes
`(category_id, created_at DESC, id DESC)` and `(created_at DESC, id DESC)`. The deepest catalog page went from 4.09 ms
in the database to 0.10 ms; [`perf/plans/README.md`](perf/plans/README.md) has the plans before and after, and why the
other top queries stayed as they are.

## Round trips per request (task R3-06)

No endpoint has an N+1: every request sends the same number of SQL commands however many lines, orders or products
it touches. Orders look up all their products in one batched `IProductCatalog.GetByIdsAsync`, and reads are projected
into their responses without tracking. Three endpoints still made round trips they didn't need:

| Request | Before | After | Change |
| --- | --- | --- | --- |
| `GET /api/v1/orders/{id}` | 3 | 1 | One query, projected into the response, instead of the order, its lines and its history one after another |
| `PATCH /api/v1/orders/{id}/status` | 4 | 2 | The order read in one query, then one save |
| `GET /api/v1/users/me` | 2 | 1 | One projection for the profile and its roles, which no longer reads the password hash |

Joining an order's lines to its history in one query multiplies rows, but the history never passes 4 rows (Pending,
Confirmed, Shipped, Delivered), so that costs far less than two more round trips. The other endpoints already sent the
fewest: 1 for categories, a product page by cursor and product details; 2 for a numbered product page and "my orders"
(the page and its total), and for a login. A registration sends 9, Identity's own checks before and after it adds the
role; it isn't in the hot path, so it stays Identity's. The pending-release worker saves once per release on purpose,
so a crash halfway keeps what already went back.

Tests in Development read `X-Db-Queries` and fail if a count grows with the data, which is what an N+1 looks like:
order details stays at 1 query with 1 line or 5 and a full history, "my orders" at 2 for 1 or 50 per page, a new order
at 1 save for 1 line or 5, a status change at 2, and the current user at 1.

## Catalog caching (task R3-07)

Catalog HTTP reads use HybridCache: a five-second in-process copy (L1), then Redis (L2), then PostgreSQL on a miss.
Redis runs with 128 MB, `allkeys-lru`, and no persistence. Product data without stock lasts 10 minutes, categories
one hour, and numbered product pages 1-3 last 60 seconds. The key includes the category, page and page size;
omitted defaults share a key with explicit defaults. Cursor pages and deeper numbered pages bypass the cache.
The current load test follows cursors after page 1, so about 60% of its list traffic is eligible.

Product metadata and the product/category row versions share one cache entry. Conditional requests reuse that
snapshot and still fetch live stock before checking the ETag. Redis keys use the `shop:r3:v2:` namespace so the
versioned payload cannot be confused with the older `shop:r3:v1:` product-only entries; old entries expire normally.

Stock is fetched from the warehouse on every detail request, including warm reads. Order creation's batched
`IProductCatalog` continues to read current prices directly from PostgreSQL. Orders, authentication state,
permissions, errors and HTTP headers are never stored in the catalog cache.

Compose supplies `ConnectionStrings__Redis=redis:6379`; local Development uses `localhost:6379`. Redis is published
only on localhost (`REDIS_PORT`, default 6379). Disable caching with `CatalogCache__Enabled=false`, or
`CATALOG_CACHE_ENABLED=false` in Compose. `CatalogCache` also configures `ProductTtl`, `CategoriesTtl`, `ListTtl`,
`LocalTtl` and `MaxCachedPage` (1-3). Enabling caching requires a Redis address. Durations must be positive, and
the local TTL must not exceed any distributed TTL. Redis outages fall back to database reads with short client
timeouts; database errors and cancellation still propagate.

After saving a new product, creation invalidates its category's pages and the unfiltered pages. The demo seeder
does the same only when it adds products. Invalidation has an independent two-second cancellation budget after
commit; failure is logged and doesn't report an already saved product as failed. TTL bounds stale data when
invalidation cannot reach Redis. Other app instances can retain their L1 copy for up to its short expiry.

Product updates call the internal `CatalogCacheInvalidator.ProductChangedAsync(id, oldCategory, newCategory)` after
the edit commits. This removes `product:{id}` and invalidates both category tags plus the unfiltered list tag.
`CategoriesChangedAsync()` removes categories and invalidates catalog bodies that contain category names.
Tags invalidate entries logically; Redis keys can remain until expiry.

`perf/run.sh` already flushes Redis on snapshot restore and resets its statistics before each run. The report's
**Redis keyspace hit ratio** is `hits / (hits + misses)` (null with no lookups), covering setup, warm-up and measured
traffic, including tag lookups. It does not measure L1 hits or the percentage of HTTP requests served from cache.
`CatalogCacheTests` use two hosts with isolated PostgreSQL and Redis to prove L1/L2 hits, expiry, one concurrent
fill, cancellation, live stock/prices, invalidation and outage behavior. Existing query-count tests disable caching
explicitly; cold cache fills still contribute to Development's `X-Db-Queries`.

The measured Redis comparison uses **400 req/s on both versions**, three valid runs each: overall p95 fell from
10.8 to 9.8 ms, catalog-list p95 from 4.9 to 3.2 ms, and categories from 4.3 to 1.1 ms. Redis's keyspace hit ratio
was 73.7%. [`perf/results/after-redis/README.md`](perf/results/after-redis/README.md) records all attempts, the
environment and the interpretation; [`comparison.redis.md`](perf/results/comparison.redis.md) isolates the Redis
change. The historical baseline is at 1,000 req/s with a different CPU allocation, so its deltas don't compare.

## Product updates (task R3-08)

`PUT /api/v1/products/{id}` is Admin-only and accepts JSON containing all four editable fields: `name`,
`description`, `price` and `categoryId`. It keeps the existing ID, image and creation time. Name and description
are trimmed, required and limited to 200 and 2000 characters respectively. Price is required, nonnegative and fits
`numeric(18,2)` without rounding; the selected category must exist. A successful update returns `204 No Content`
with `Cache-Control: no-store` and does not call the warehouse.

```sh
curl -i "$API/api/v1/products/$ID" # Save its ETag as OLD_ETAG; this also warms the product cache.
curl -i "$API/api/v1/products?category=$CATEGORY_ID" # Warm its category's list.
curl -i -X PUT "$API/api/v1/products/$ID" \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' \
  --data "{\"name\":\"Updated product\",\"description\":\"Updated description\",\"price\":29.95,\"categoryId\":\"$CATEGORY_ID\"}"
# 204 No Content
curl -i -H "If-None-Match: $OLD_ETAG" "$API/api/v1/products/$ID"
# 200 with the updated data and a new ETag; sending the new ETag returns 304.
curl -i "$API/api/v1/products?category=$CATEGORY_ID" # The list also shows the edit immediately on this host.
```

The handler reads and saves the tracked product directly in PostgreSQL, then invalidates the product and every
cached page/size of its old category, new category and the unfiltered list. An invalid request or failed save leaves
the cache intact. Invalidation keeps its independent two-second budget after commit, even if the client disconnects;
a Redis failure is logged without reporting a committed update as failed. Other hosts' warm L1 copies can remain
for up to five seconds; Redis and a host with empty L1 observe the invalidation.

Responses use the existing Problem Details contract: 400 for malformed inputs, 401/403 for authorization, 404
`products.not_found`, 415 for unsupported content, and 422 `common.validation_failed` for invalid fields or an
unknown category (including deletion between validation and save). An overlapping edit detected by `xmin` returns
409 `common.concurrency_conflict`. There is no `If-Match` precondition: a client editing an old screen after another
request has finished is not rejected on that basis. Order creation still reads the current price from PostgreSQL.

## Product ETags (task R3-09)

`GET /api/v1/products/{id}` labels its answer with an ETag and `Cache-Control: no-cache`: a client may keep the
product, but checks with the API each time it shows it. It sends the ETag back in `If-None-Match`, and while nothing in
the product has changed, the answer is `304 Not Modified` with no body, carrying the same ETag and `Cache-Control`.

```sh
curl -i "$API/api/v1/products/$ID"                                            # 200 OK, ETag: "v882-c882-s1000000"
curl -i -H 'If-None-Match: "v882-c882-s1000000"' "$API/api/v1/products/$ID"   # 304 Not Modified, no body
```

The ETag changes whenever anything in the body can:

- `v` is the product row's version, and `c` its category's, since the body shows the category's name. Both are
  Postgres's `xmin`, the system column holding the transaction that last wrote the row, so every update changes it.
  The migration only maps it; every row always had one. EF now also uses it as a concurrency token: an update or
  delete names the version it read. R3-08 returns 409 on overlapping saves; it does not implement `If-Match`.
- `s` is the stock the warehouse reported, which every order changes, or `s?` when the warehouse couldn't answer.

`If-None-Match` compares weakly, so the `W/"…"` a compressing proxy sends back matches too, as do lists and `*`.

A 304 reads product metadata and its row versions through HybridCache, then asks the warehouse for live stock.
Warm metadata avoids PostgreSQL; the ETag independently saves transfer and serialization. With caching disabled,
revalidation still reads the product once. The original R3-09 measurement reduced a response from 796 bytes to 338.

The security headers make every authenticated response `no-store`, unless its endpoint chose its own caching. Product
details are the same for everyone, so they choose `no-cache`, and a signed-in browser keeps and revalidates them
too. Every other endpoint still gets `no-store`.

In `perf/load.js`, product details send their ETags back as a browser would (`ETAGS=off` stops it). Run alone, with
nothing changing, 45% of the views came back as 304s, and the data received fell by 28.5%. In the full mix, only
1.5% did: an order changes the stock in the body, and the products viewed most are the ones ordered most.
[`perf/results/etag`](perf/results/etag/README.md) has the runs and the reasoning.

## Read replica (task R3-10)

`db-replica` is a streaming replica of `postgres`, for the reads that may be a moment old when R3-11 routing is enabled.
It runs only with its profile, so the default stack, and every step measured before R3-11, runs without it:

```sh
docker compose --profile replica up -d --wait   # the first start copies the primary: about 10 s for the perf dataset
perf/replica-lag.sh                             # how far behind the primary it is
perf/replica-lag.sh watch                       # a line a second, say during a load test
```

- **The copy.** The first start copies the primary with `pg_basebackup -R` ([`db/replica-entrypoint.sh`](db/replica-entrypoint.sh)).
  `-R` writes `standby.signal` and `primary_conninfo`, so Postgres starts as a hot standby: it replays the primary's
  WAL as it streams in and answers reads, and refuses writes ("cannot execute CREATE TABLE in a read-only
  transaction"). Later starts resume streaming where they stopped.
- **The role.** It streams as `replicator`, a role that may replicate and nothing else, created by the first copy
  with `REPLICATION_PASSWORD`. [`db/pg_hba.conf`](db/pg_hba.conf), which both servers read through `hba_file`, lets
  that role connect for replication; its other rules are the postgres image's own, so it works on an existing data
  volume too.
- **The slot.** A replication slot keeps on the primary the WAL the replica hasn't received yet, so a stopped
  replica catches up when it starts again: 21 MB written while it was stopped were replayed within seconds.
  `max_slot_wal_keep_size=1GB` caps that, so a replica stopped through more writes loses its slot instead of filling
  the primary's disk, and has to be copied again: `docker compose --profile replica rm -sf db-replica`,
  `docker volume rm shop_postgres-replica-data`, then start it. The copy replaces the old slot.
- **Long reads.** `hot_standby_feedback=on` tells the primary which rows the replica's reads still use, so vacuum
  there doesn't remove them and cancel the read.

`perf/replica-lag.sh` first writes a marker to the primary's WAL, a logical decoding message that changes no table,
so the numbers are fresh even when nothing else writes. Then it shows what the plan asks to watch, the primary's
`pg_stat_replication`: the WAL not yet replayed, and the write, flush and replay lag. On this laptop, idle, the replay
lag was about 1 ms, and a row written on the primary could be read on the replica 0.38 s later, most of it the
polling's `docker exec`.

`REPLICA_APPLY_DELAY=5s` (demos only) makes the replica replay every change 5 seconds late: the replay lag then reads
5001 ms, and the same row took 5.41 s to appear. That's the stale read R3-11 has to handle: without read-your-writes,
a new order would be missing from "my orders" for 5 seconds.

`perf/snapshot.sh restore` waits for a running replica to replay the restore before the app restarts (17 s in all),
and `perf/run.sh` and `perf/compare.sh` report its CPU with the other containers'.

The replica must allow at least as many connections as the primary (and a few other limits), or it won't start;
both keep the image's defaults. On one laptop a replica adds no hardware, so no speed-up is expected from it: its
value is spreading reads over machines and keeping heavy reads off the primary.

## Read routing and read-your-writes (task R3-11)

Read routing is opt-in: set `READ_ROUTING_ENABLED=true` in Compose and start with
`docker compose --profile replica up -d --build --wait`. It is off by default, so existing deployments and tests
keep using `ConnectionStrings:Shop`. Enable it only after R3-10's replica has copied the primary.

| Operation | Database/cache |
| --- | --- |
| Writes, explicit migrations/seeding and pending-release work | Primary |
| Login, registration, roles and current-user reads | Primary |
| Order creation's current-price catalog contract | Primary |
| Catalog cache hits | Existing L1/Redis; a recent writer bypasses both |
| Any catalog cache fill, including categories and numbered pages | Primary |
| Uncached catalog reads: cursor pages, numbered page 4+, or cache disabled | Prefer replica |
| Customer/admin order lists and order details | Prefer replica, with existing ownership filters |
| Any routed read by a recent writer, or an authenticated read whose marker cannot be checked | Primary |

`ConnectionStrings:ShopRead` lists `db-replica,postgres` with `Target Session Attributes=prefer-standby`.
[Npgsql's host selection](https://www.npgsql.org/doc/failover-and-load-balancing.html) falls back to the primary
when no standby is available. An eligible read that loses a pooled replica connection is retried once, in full,
on a new primary context. Cancellation and non-transient database errors still propagate; writes are never retried
by this read path. The normal module context always remains the primary/write context.

After product creation/edit or order creation/status change commits, the API records logical key `rw:{userId}`
in Redis, under the existing `shop:r3:v2:` prefix. `ReadRouting:PrimaryWindow` defaults to ten seconds, configurable
as `READ_YOUR_WRITES_WINDOW=00:00:10` in Compose. Every successful write refreshes it. Reads check Redis directly,
so a second API host sees the pin too; there is no local cache of a missing marker. Pinned catalog requests bypass
even a warm L1 and read primary metadata, while stock is still fetched live from the warehouse.

Marking has an independent two-second timeout after commit, and a failure is logged without failing the saved
write. The writer host also retains a local pin for the same window, covering a failed Redis write followed by
recovery. If Redis cannot answer a marker lookup, authenticated routed reads choose the primary. A failed marker
write is not durable across hosts or restarts: once Redis recovers, another host cannot recover that missing pin.
This is a bounded consistency window, not a guarantee for replication lag longer than the configured window.

For local `dotnet run`, configure `ReadRouting__Enabled=true` and a read connection such as
`Host=localhost:5434,localhost:5433;Database=shop;Username=shop;Password=shop;Target Session Attributes=prefer-standby;Timeout=2;Host Recheck Seconds=5`,
plus the existing Redis address. The default local connection remains primary-only. Enabling routing requires
both `ConnectionStrings:ShopRead` and `ConnectionStrings:Redis`, and a positive primary window.

To see the stale-read problem, start the profile with `REPLICA_APPLY_DELAY=5s` (demos only) and routing enabled.
Place an order as a Customer, then immediately GET `/api/v1/orders` and its Location with the same token: both
show the order through the primary pin. Querying that order's ID directly on `db-replica` still shows no row until
replay catches up. `perf/replica-lag.sh` shows the lag. Restore `REPLICA_APPLY_DELAY=0` after the demo.

`ReadRoutingTests` uses an isolated primary, a physical `pg_basebackup` standby, Redis and two real API hosts with
JWT authentication. Pausing WAL replay proves replica reads, primary-only cache fills, a writer's immediate
read across hosts, expiry back to stale replica reads, order ownership, current prices, primary-only identity,
replica failure and Redis failure. The tests leave the developer's Compose databases and snapshots alone.

The matched 200 req/s experiment has three valid runs per phase: primary PostgreSQL CPU fell from 8.8% to 6.5%
of one core, while overall p95 increased from 9.9 to 10.3 ms. The
[results and five-second lag proof](perf/results/after-replica/README.md) explain that tradeoff;
[comparison.replica.md](perf/results/comparison.replica.md) isolates these two tagged versions.

## Final results (task R3-12)

[`perf/results/README.md`](perf/results/README.md) has the plan's four exhibits: the results table, what the system
did in each run, a live edit and a live replica. In short: Redis took Postgres from 34% to 15% of a core and cut p95
by 9% (catalog p95 by a third or more); keyset paging made the deepest catalog page 40 times cheaper in the database;
ETags save 28.5% of the data when products don't change, but few revalidations match while orders keep changing
stock; the replica took about 6,850 catalog reads a run off the primary at a 4% p95 cost on one laptop.

The steps were measured as pairs on two machines and at three rates, so each row compares only with its own
reference. [`perf/final.sh`](perf/final.sh) remeasures all five on one machine at one rate (about 75 minutes).

## RabbitMQ and the messaging library (task R4-01)

Compose runs RabbitMQ 4.3 with its management UI at `http://localhost:15672` (user `RABBITMQ_USER`, password
`RABBITMQ_PASSWORD`, both `shop` by default). AMQP is published on `127.0.0.1:5672` for a local `dotnet run`. The
container has a fixed hostname: RabbitMQ stores its data under its node name, `rabbit@<hostname>`, so a re-created
container finds the volume's queues and messages again. The `guest` user only works from inside the broker's own
container, so the app signs in with its own user, created when the `rabbitmq-data` volume is first used.

[`src/Shop.Messaging`](src/Shop.Messaging) is shared by the API and, from R4-03, the email worker:

- `RabbitMqConnection` is the host's one connection. The first connect declares the topology below; after that the
  client reconnects by itself (every 5 seconds) and declares it again.
- `RabbitMqConnector` connects in the background at startup with backoff (1 s doubling to 30 s), so a missing
  broker never blocks startup. `/health` reports Degraded until it connects, as it does for the warehouse.
- `IEventPublisher` publishes persistent messages with publisher confirms, and returns one result per message. Only a
  confirmed message is stored; a nack, a timeout (`RabbitMq:ConfirmTimeout`, 5 s) or a broker that can't be reached
  fails that message, and nothing throws, so the outbox relay (R4-02) can retry exactly the failed rows. A batch is
  sent in list order and confirmed together.
- `order.placed` has two physical publishes with the same message ID: a mandatory copy through the built-in default
  exchange (`""`) addressed to `email.order-confirmation`, and a live copy through `shop.events`. The mandatory copy
  must reach that exact email queue: a live queue cannot mask its absence. Both publishes are confirmed before the
  logical event succeeds. A missing email queue leaves the event retryable in the outbox while live updates can still
  arrive. A status change with no watchers is confirmed and dropped; pages reload current state when they connect.

| Name | Kind | Arguments and binding |
| --- | --- | --- |
| `shop.events` | Topic exchange, durable | Every domain event, routed by its type |
| `""` (built-in default exchange) | Direct exchange | Routes the mandatory email copy by its exact queue name |
| `email.order-confirmation` | Quorum queue | Addressed directly through the default exchange; `x-delivery-limit` 4 (five deliveries in total), dead-letters to `shop.dlx` with at-least-once delivery and `x-overflow: reject-publish` |
| `shop.dlx` | Direct exchange, durable | Receives messages a queue gave up on |
| `email.order-confirmation.dlq` | Quorum queue | Bound to `shop.dlx` with key `email.order-confirmation` |

The logical message type remains `order.placed` in AMQP's `Type` property; its physical email routing key is now the
queue name. The consumer reads `Type`, falling back to the routing key for older untyped messages. At startup the
topology removes the legacy `shop.events` -> email binding without deleting the queue or its waiting messages, so
the live publish cannot enqueue a second email copy. Deploy the API and worker from the updated build together;
older API publishers that send only through `shop.events` must be stopped before the new topology is installed.
No queue deletion, database migration or volume reset is needed for this routing change.

A message is dead-lettered on its fifth failed delivery: a limit of 4 allows the first delivery plus four retries. Since
RabbitMQ 4.3 only two things count as a failed delivery: `basic.reject` with requeue, and a consumer losing its
connection with the message unacknowledged. `basic.nack` with requeue doesn't count, so a consumer that nacks a broken
email retries it forever; the email worker must reject (R4-05). The R4-07 consumer creates a separate temporary
live-update queue inside each API instance; the email worker never creates one.

Queue arguments can't change once a queue exists: a different `x-delivery-limit` fails with PRECONDITION_FAILED.
For the R4-09 change from 5 to 4, follow [the safe queue upgrade](#upgrading-the-email-queue-to-five-deliveries-r4-09)
before starting the updated API and worker against an existing broker.

```sh
docker compose up -d --wait rabbitmq
docker compose up -d --build api1 api2 api3 # then open http://localhost:15672 > Queues and Streams
curl -s localhost:8080/health             # Healthy, or Degraded while RabbitMQ or the warehouse is down
docker compose stop rabbitmq              # the API keeps serving; /health turns Degraded
docker compose start rabbitmq             # the API reconnects on its own within a few seconds
```

`tests/Shop.Messaging.IntegrationTests` start the same image with Testcontainers and check, through the management
API, the topology's types and arguments; that a second host declaring it changes nothing; that the client reconnects
and declares it again after the broker drops the connection; that confirmed messages are persistent and keep their
order; that an `order.placed` with no email queue fails even with a live queue bound, while the rest of its batch is
confirmed; that repairing the email destination lets the same ID succeed; that upgrading removes the old binding
without losing waiting emails or duplicating them; that a status change no queue takes is confirmed; that rejected
and over-limit messages reach the DLQ, and nacked ones never do; that a host
without a broker starts at once and reports Degraded; and that invalid settings stop it.

## Transactional outbox (task R4-02)

Placing an order never waits for RabbitMQ. The order and its event are saved in one transaction, and a background
relay publishes the event afterwards:

1. `Order.Place` and `Order.MoveTo` record an event inside the order (`OrderPlaced`, `OrderStatusChanged`), so no
   code path can place an order or change its status without one.
2. When the order is saved, `OrdersDbContext` turns those events into rows of `orders.outbox_messages` in the same
   `SaveChanges`. One transaction commits both or neither: a saved order always has its event, and a failed save (an
   error, or a status change that loses the version race with 409) leaves none. The row goes in the same batch, so
   placing an order is still one round trip and a status change two.
3. The save wakes this instance's relay at once; a poll every second covers rows another instance wrote and restarts.
4. One relay pass, in one transaction, locks up to 50 unpublished rows oldest first with `FOR UPDATE SKIP LOCKED`,
   publishes them, and sets `published_at` on the rows RabbitMQ confirmed. A row it didn't confirm stays unpublished
   with `attempts` and `last_error` updated, and a later pass retries it. Another instance's relay skips locked rows
   instead of waiting, so two relays never publish the same row at once.
5. While RabbitMQ or the database is down, the relay waits 1 s after a failed pass, doubling up to 30 s. Published
   rows are deleted after 7 days.

| Column | Meaning |
| --- | --- |
| `id` | Also the RabbitMQ message ID, so a consumer can drop a message it receives twice |
| `type` | The event type and routing key: `order.placed` or `order.status-changed` |
| `payload` | The public event from `Shop.Modules.Orders.Contracts` as camelCase JSON (jsonb), the exact message body |
| `occurred_at`, `published_at` | When it happened, and when RabbitMQ confirmed it (null while waiting) |
| `attempts`, `last_error` | Publish tries so far, and why the last one failed |

`order.placed` carries what the confirmation email needs: customer ID and email (from the token), status, total,
lines and version, so the email worker never reads the Orders or Users tables. `order.status-changed` carries the old
and new status, the new version and who changed it; live pages ignore a version older than the one they show.

Delivery is at least once. If the app stops after RabbitMQ confirmed a batch but before the commit, those rows go out
again after the restart; consumers drop repeats by message ID (R4-03). `Orders:Outbox` sets `PollInterval` (1 s),
`BatchSize` (50) and `Retention` (7 days).

An order placement succeeds only after its separately addressed email copy and its live-event publish are confirmed.
A partial success may resend a copy on the next pass; the message ID remains the outbox row ID. The real broker/database
test in `OutboxDeliveryTests` proves a saved order stays unpublished after email queue loss or broker downtime, then
publishes its confirmation after recovery. It also proves a live copy can arrive while its missing email destination
keeps the outbox row pending.

```sql
-- What is waiting, and why
SELECT type, attempts, last_error, occurred_at FROM orders.outbox_messages WHERE published_at IS NULL ORDER BY occurred_at;
```

`OutboxTests` cover the order and its event saved together, a failed save and a lost version race leaving no event,
a pass publishing the oldest rows with the row ID as message ID, an unconfirmed row keeping its error until a retry
succeeds, a second relay skipping locked rows without waiting, and cleanup keeping unpublished rows.
`OutboxWakeUpTests` show the relay publishing a new order at once, with the poll set to an hour.

## SignalR channel security (task R4-06)

The API exposes two server-to-client hubs outside the versioned REST routes:

| Hub | Who can connect | Delivery used by R4-07 |
| --- | --- | --- |
| `/hubs/orders` | Any authenticated user | `IHubContext<OrdersHub>.Clients.User(customerId)` |
| `/hubs/admin` | The existing `AdminOnly` policy | `IHubContext<AdminHub>.Clients.Group("admins")` |

The hubs live inside Notifications. The admin hub adds every authorized connection to `admins` on connect,
including a new connection after a reconnect. Clients have no subscribe or join-group methods. The registered
`IUserIdProvider` takes the user's ID from the validated JWT's `sub` claim; `MapInboundClaims` stays false, and
all of a user's connections receive messages addressed to that user. R4-07's live-update consumer can use the
internal hubs from inside Notifications; Orders does not depend on SignalR.

Use the normal JWT returned by login with the SignalR JavaScript client's `accessTokenFactory`, and open the
WebSocket straight away (task R5-10: behind Nginx, a negotiate request and the WebSocket after it can reach different
instances):

```js
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/orders", {
    accessTokenFactory: () => accessToken,
    skipNegotiation: true,
    transport: signalR.HttpTransportType.WebSockets,
  })
  .withAutomaticReconnect()
  .build();
await connection.start();
```

Use `/hubs/admin` with an admin token for the dashboard. The hubs accept WebSockets only. The client sends
`Authorization: Bearer` where it can; a browser's WebSocket sends `access_token` in the query string. The API accepts
that query parameter only under the `/hubs` path segment, and applies its existing signature, issuer, audience,
lifetime and claim validation. REST endpoints still require the bearer header.

Both hubs set `CloseOnAuthenticationExpiration`: an open connection closes when its token expires. Supply a
fresh token before reconnecting; login remains the way to obtain one. Automatic reconnect alone cannot renew
an expired token. R4-08's pages must reload their current state after a reconnect, since messages sent while
disconnected aren't replayed. That task also supplies the page CSP; this task keeps the existing REST CORS policy.

Serve these connections over HTTPS in production. Keep request URLs with query strings out of logs: the existing
`Logging:LogLevel:Microsoft.AspNetCore` setting is `Warning`, and should remain so in deployed configuration.
Proxies must also omit or redact `access_token`. These choices follow Microsoft's
[SignalR authentication](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-10.0)
and [token logging guidance](https://learn.microsoft.com/en-us/aspnet/core/signalr/security?view=aspnetcore-10.0#access-token-logging).

R4-06 provides the secured channels. R4-07 supplies RabbitMQ event consumption below; R4-08 supplies the browser pages.
The security tests use real JWT validation and WebSocket connections through TestServer, without PostgreSQL or RabbitMQ:

```sh
dotnet test tests/Shop.Api.IntegrationTests --filter FullyQualifiedName~SignalRSecurityTests
```

They cover query and header authentication, invalid tokens and claims, customer rejection by the admin hub,
path boundaries, delivery to all of customer A's connections without leaking to customer B, admin group membership
after reconnecting, and open connections closing at token expiry followed by delivery with a fresh token.

## Live order updates (task R4-07)

`AddNotificationsModule` starts `LiveUpdateConsumer` inside each API process. It shares that host's RabbitMQ
connection and owns one channel and one server-named classic queue: non-durable, exclusive, auto-delete, bound to
`shop.events` with `order.*`. Every API instance receives its own copy of every event and sends to its local SignalR
connections, so these pushes need no backplane. Orders publishes contracts through its outbox and has no SignalR dependency.

The public browser contracts are in `Shop.Modules.Notifications.Contracts.Realtime`, serialized as camelCase JSON:

| SignalR method | Destination | Arguments |
| --- | --- | --- |
| `OrderPlaced` | `/hubs/admin`, server-assigned `admins` group | One object: `messageId`, `orderId`, `version`, `customerId`, `status`, `total`, `itemCount`, `placedAt` |
| `OrderStatusChanged` | `/hubs/orders`, `Clients.User(customerId)` using the validated JWT `sub` | One object: `messageId`, `orderId`, `version`, `fromStatus`, `status`, `changedAt` |
| `ResyncRequired` | All local connections on both hubs | No arguments; reload current state through REST |

`itemCount` is the sum of quantities, not the number of lines. Payloads use the broker event without database reads;
email addresses, line details and the admin's ID are not forwarded. An admin row begins at version 0. REST create,
status-change, detail and list responses now also include `version`, using the existing order version column.

The consumer uses manual acknowledgements, prefetch 5 and sequential dispatch. It acknowledges after SignalR accepts
the send; this is not a browser receipt. Successfully sent message IDs are remembered locally for ten minutes, up to
10,000 IDs. A shared `processed_messages` table would suppress the other API instances' copies, so live updates never
use the email worker's inbox. Duplicate delivery outside that window remains possible.

A send has three attempts, each bounded to five seconds, with one and two seconds between attempts. Exhausting those
attempts replaces the temporary subscription and requests a fresh snapshot once it is ready. Invalid messages are
rejected without requeue; unknown `order.*` types are acknowledged and ignored. The temporary live queue has no DLQ.
Persistent confirmation emails continue to use their own durable queue and DLQ.

Starting without RabbitMQ never blocks the API. Initial connection failures retry with 1-to-30-second backoff.
The RabbitMQ client automatically restores the channel, queue, binding and consumer after a connection failure;
server-generated queue names can change. A deleted queue or a channel lost on its own is replaced by the service,
but only while the connection is open: after a connection failure the client restores the old queue too, and a new
subscription beside it would leave that queue without a consumer, keeping a copy of every order event until the API
stopped. The `realtime` health check reports Degraded until the consumer is registered, including during recovery.
Logs include queue names and rejected message IDs, never message bodies. Shutdown cancels consumption, drains the
current handler for at most twenty seconds (or the host's remaining budget), then disposes only this consumer's
channel.

Rules for pages that use these events:

- Load initial REST state, and merge snapshots and live updates by order ID and version; ignore an older or equal
  version. Register handlers before starting SignalR, and handle events that arrive while a snapshot is loading.
- Reload after both SignalR reconnect and `ResyncRequired`. The consumer emits the latter whenever its subscription
  becomes ready, including recovery while browser WebSockets remained connected. Temporary queues do not replay gaps.
- When read routing is enabled, resynchronization needs a current primary snapshot. A stale replica response must
  never replace a higher version already displayed. R4-07 tests pin `ReadRouting:Enabled=false` to isolate delivery.

R4-08's live pages (below) follow these rules, except that a reload doesn't ask for the primary database: see their
note on read routing.

No pages or page-specific CSP are added here. R4-09 retains the combined outbox/email failure scenarios and R4-10 the
interview proof kit. R4-07's focused tests publish through the real RabbitMQ publisher and receive through real
JWT-authenticated SignalR WebSockets, covering isolation, fan-out to two API instances, temporary topology,
duplicates/invalid messages, connection and queue recovery (never leaving a live queue without a consumer),
resynchronization, bounded send failures and shutdown.
R4-06's security tests remain independent of a broker.

```sh
dotnet build Shop.slnx -m:1 -nr:false
dotnet test tests/Shop.Api.IntegrationTests --no-build -m:1 -nr:false --filter FullyQualifiedName~RealtimeDeliveryTests
dotnet test tests/Shop.Modules.Notifications.IntegrationTests --no-build -m:1 -nr:false --filter FullyQualifiedName~LiveUpdateHandlerTests
dotnet test Shop.slnx --no-build -m:1 -nr:false
```

## Live pages (task R4-08)

Two plain pages, served by the API itself from [`src/Shop.Api/wwwroot/app`](src/Shop.Api/wwwroot/app):

| Page | For | Shows |
| --- | --- | --- |
| `/app/orders.html` (or `/app`) | Customers | Their orders, each order's lines and history, and each status change as it happens; a form to place an order, and to create an account |
| `/app/admin.html` | Admins | Every order, newest first: new ones appear as customers place them, and buttons move an order to its next status |

How a page works:

1. It loads without a token, as it holds no data, and you sign in on the page. The token stays in that browser tab
   only (`sessionStorage`), so an admin tab and two customer tabs can sit side by side.
2. It connects to its hub (`/hubs/orders` or `/hubs/admin`) with the SignalR JavaScript client, and only then loads
   the current orders from the API, so nothing that happens in between is missed. The token goes in the query
   string, because browsers can't add a header to a WebSocket; the API accepts it there only under `/hubs` (R4-06).
3. It listens for R4-07's methods (table above): the dashboard for `OrderPlaced`, a customer's page for
   `OrderStatusChanged`, and both for `ResyncRequired`. An event changes what a page shows only when its `version` is
   newer than what is shown, so one that arrives late or twice changes nothing; the page's event log marks it ignored.
   Status changes go to the order's customer only, so the dashboard shows one when this admin makes it, and after a
   reload.
4. If the connection drops, SignalR reconnects by itself (at once, then after 2, 10 and 30 seconds) and the page
   reloads its orders, since events sent meanwhile are lost; it reloads on `ResyncRequired` too. A reload merges into
   what is shown by version, so a list read before an event arrived never takes a row back to an older state. The
   pill in the header says Live, Reconnecting or Offline. When the token expires, the server closes the connection and
   the page asks you to sign in again.
5. A customer token is refused by `/hubs/admin`, and the dashboard says the page is for admins.

Everything under `/app` gets its own CSP: `default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self';
connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'`. Nothing on the pages is inline,
and `connect-src 'self'` covers the WebSocket to the same host. For the same reason the SignalR client is served by
the site: `wwwroot/app/lib/signalr/signalr.min.js` is `@microsoft/signalr` 10.0.11 (MIT, matching ASP.NET Core 10),
checked against npm's integrity value. Order data reaches the page only as text, never as HTML. Files under `/app`
go out with `Cache-Control: no-cache` and an ETag, so a browser checks its copy on every load (a cheap 304) and a new
version takes effect at once. Short IDs on the pages and in the email subject are the GUID's last 8 characters: our
IDs are version 7 GUIDs, whose first characters are a timestamp shared by everything created in the same minute.

`dotnet run --project src/Shop.Api` (or the Docker image) serves the pages; running `Shop.Api.dll` from `bin/` doesn't,
because a build leaves `wwwroot` in the project folder.

With read routing on (R3-11), a reload can read a replica a moment behind. A page still never goes back to an older
state, but a change that is still replicating shows only at the next event or reload; read routing is off by default.

`LivePagesTests` (conformance, through the real Program) check that every file loads without a token under that CSP,
that `/app` opens the customer's page, that the pages have no inline scripts, styles or handlers, and that their
scripts never write HTML or evaluate code. `LiveEventContractTests` check that the pages listen for R4-07's method
names (`RealtimeEvents`) and that each notification arrives with the fields the pages read; `OrdersApiTests` check the
version.

## Email worker (task R4-03)

[`src/Shop.Worker`](src/Shop.Worker) is a separate .NET Worker Service in its own container (`worker` in compose).
Stopping it never touches the API: orders still return 201, and their confirmation emails wait in RabbitMQ until it
starts again. It registers only the Notifications module (`AddNotificationsWorker`), so it has no route to the Orders
or Users tables; everything an email needs comes in the `order.placed` message.

For each message from `email.order-confirmation`:

1. If its message ID (the outbox row ID) is in `notifications.processed_messages`, it was handled before: acknowledge
   it without sending again.
2. Otherwise send the email, record the ID, then acknowledge. Recording after sending means a crash between the two
   costs one duplicate email, never a lost one.
3. A message that can never be sent (unreadable, no customer email, no message ID) is rejected without requeue and
   goes straight to the DLQ. Any other failure is rejected with requeue: RabbitMQ counts it, delivers it again, and
   moves it to the DLQ on the fifth failure. R4-05 supplies backoff and classification of permanent mail errors.

The consumer ([`QueueConsumer`](src/Shop.Messaging/Consuming/QueueConsumer.cs), in the messaging library) uses manual
acks, handles one message at a time, and has a prefetch of 5, so a crash returns at most 5 messages to the queue and
other workers can take the rest. After a dropped connection the client brings the consumer back by itself; if RabbitMQ
closes just its channel, or cancels it because the queue was deleted, it declares the topology again and starts over.

Stopping is graceful (`docker compose stop` sends SIGTERM; `stop_grace_period` is 30 s):

1. Cancel the consumer, so RabbitMQ sends nothing new.
2. Let the message in progress finish and acknowledge it (up to 20 s).
3. Give the prefetched messages that never started back with `basic.nack`. Since RabbitMQ 4.3 a nack doesn't count
   as a failed delivery, so a restart never uses up a message's retries. A failure uses `basic.reject`, which does.

The emails go out over SMTP (task R4-04, below). The migrator prepares the `notifications` schema before any worker
starts; workers never apply migrations themselves (R5-04).

```sh
docker compose stop worker      # place orders: each returns 201 at once
# RabbitMQ UI > Queues: email.order-confirmation shows them ready
docker compose start worker     # the queue drains, and Mailpit (http://localhost:8025) shows one email per order
```

`ConsumerTests` (RabbitMQ container) cover the ack after the handler, a counted retry ending in the DLQ, a reject going
straight there, a handler that throws, the prefetch limit, a graceful stop that finishes the current message and
returns the prefetched one uncounted, and recovery after a dropped connection or a deleted queue.
`OrderConfirmationHandlerTests` (Postgres container) cover one email per message even when delivered twice, nothing
recorded when sending fails, rejected messages, and the email's content.
`OrderConfirmationDeliveryTests` use the real worker registration, RabbitMQ and Postgres with a recording email sender:
three confirmations wait while the worker is stopped and are sent on restart; two more survive a broker application
stop/start (`rabbitmqctl stop_app` / `start_app`, keeping the endpoint fixed) while the worker is stopped, then are sent
and recorded with their original IDs and `order.placed` type. SMTP provider
delivery remains part of R4-04. A future DLQ redrive must publish the original body, message ID and type directly to
`email.order-confirmation` through the default exchange, with mandatory routing and publisher confirms.

## Sending email (task R4-04)

The worker sends each email over SMTP with MailKit. One setting, `Email:Provider` (`EMAIL_PROVIDER` in `.env`),
chooses where it goes:

| `EMAIL_PROVIDER` | Goes to | Sign-in | Use it for |
| --- | --- | --- | --- |
| `Mailpit` (default) | The `mailpit` container, inbox at `http://localhost:8025` | None | Local runs and every k6 run |
| `Smtp` | A real provider, such as Gmail | `SMTP_USER` and `SMTP_PASSWORD`, over TLS | Demos with real inboxes |

k6 runs must stay on Mailpit: their orders use made-up addresses, and a real account that bounces thousands of
emails gets blocked. Mailpit keeps the newest 5,000 emails.

To send with Gmail, turn on 2-Step Verification for the account, create an app password (Google Account > Security >
App passwords), and set in `.env`:

```sh
EMAIL_PROVIDER=Smtp
SMTP_HOST=smtp.gmail.com
SMTP_PORT=587
SMTP_USER=you@gmail.com
SMTP_PASSWORD=<the 16-letter app password>
EMAIL_FROM=Shop <you@gmail.com>   # Gmail sends from your own address
```

Then `docker compose up -d worker` recreates the worker with the new settings. `SMTP_SECURITY` is `StartTls` (port
587) by default, or `SslOnConnect` for port 465. With `StartTls` the connection fails if the server can't encrypt, so
the password never travels in plain text. `None` exists only for a server on the same machine, such as Mailpit.

Every email's Message-ID comes from its RabbitMQ message ID. If the worker sends an email twice (a crash between sending
it and recording it), both copies carry the same Message-ID, so the inbox can tell they are one message. A wrong
`Email` section (no sender, or `Smtp` without a host, user or password) stops the worker at startup. What happens when
a send fails is in the next section.

`SmtpEmailSenderTests` run the sender against a small SMTP server inside the test, so they need no container: Mailpit
mode sends without signing in, with the right sender, subject, body and Message-ID; Smtp mode signs in; a wrong
password is refused; the default `StartTls` refuses to sign in to a server that can't encrypt; and wrong settings fail
at startup.

## Retries and the dead-letter queue (task R4-05)

When an email can't be sent, the worker sorts the failure ([`EmailFailures`](src/Modules/Notifications/Shop.Modules.Notifications/Email/EmailFailures.cs))
by whether trying again can help:

| Kind | Examples | What the worker does |
| --- | --- | --- |
| Temporary | Mail server down or slow, a network error, any `4xx` reply | Tries again itself, 3 tries in all, waiting about 1 s and then 2 s. If all fail, `basic.reject` with requeue: RabbitMQ counts the failure and delivers it again |
| Permanent | A malformed address, `550 No such user`, content refused (a `5xx` after the email) | `basic.reject` without requeue: straight to the DLQ, as trying again sends the same thing |
| Our settings | A wrong password, a server without TLS, our sender address refused | Back to RabbitMQ at once, counted, without trying again in the worker: retrying can't fix it, and repeated failed sign-ins can get the account locked |

The email queue allows five deliveries, so a message that keeps failing ends up in `email.order-confirmation.dlq`, and
the worker's log says why for every try. The waits have a little randomness, so workers that failed together don't
retry in step. `Email:SendAttempts` (3) and `Email:RetryDelay` (1 s, doubling) tune the tries in the worker.
These are separate budgets: five RabbitMQ deliveries times three temporary SMTP failures per delivery means at most
15 failed SMTP attempts before the DLQ. A permanent refusal takes one SMTP attempt; a settings error takes one per
delivery. A crash with an unacknowledged message also spends a delivery, without necessarily attempting SMTP.

Stopping the worker never costs a message a try. A message that never started goes back with `basic.nack`, and so does
one still sending when the 20-second graceful stop runs out. Since RabbitMQ 4.3 a nack isn't counted.

### Looking at the DLQ and moving messages back

Compose switches on RabbitMQ's shovel plugin ([`rabbitmq/enabled_plugins`](rabbitmq/enabled_plugins)); an existing
RabbitMQ container picks it up with `docker compose up -d rabbitmq`.

1. **Look.** In the management UI (`http://localhost:15672`), open Queues and Streams, then
   `email.order-confirmation.dlq`, then Get messages. With Ack mode "Nack message requeue true" the messages stay in
   the queue. The `x-first-death-reason` header is `rejected` (refused for good) or `delivery_limit` (five failed
   tries); the worker's log has the details.
2. **Fix the cause**, such as the SMTP password in `.env`, followed by `docker compose up -d worker`.
3. **Move them back**, either in the UI (on the DLQ's page, Move messages, destination queue
   `email.order-confirmation`), or with one command:

   ```sh
   docker compose exec rabbitmq rabbitmqctl set_parameter shovel email-dlq-move-back \
     '{"src-protocol": "amqp091", "src-uri": "amqp://", "src-queue": "email.order-confirmation.dlq",
       "dest-protocol": "amqp091", "dest-uri": "amqp://", "dest-queue": "email.order-confirmation",
       "src-delete-after": "queue-length"}'
   ```

   The shovel moves the messages the DLQ holds when it starts, then deletes itself. A moved message keeps its message
   ID, so one whose success was recorded isn't emailed again, and RabbitMQ gives it five new deliveries. Moving messages back
   before fixing the cause only sends them round again.

The interview's mail failure, step by step:

1. Set `EMAIL_PROVIDER=Smtp` and a wrong `SMTP_PASSWORD` in `.env`, then `docker compose up -d worker`.
2. Place an order: it returns 201 at once. The worker logs a settings error five times, and the message moves to the DLQ.
3. Put the right password in `.env`, then `docker compose up -d worker`.
4. Move the message back: the email arrives, once.

`ConsumerTests` prove a message that failed five times gets five new deliveries after the move, counted from zero, and is
delivered once the cause is fixed, with the shovel gone afterwards; and that a message cut off by stopping goes back
uncounted. `OrderConfirmationHandlerTests` cover a temporary failure that succeeds on the next try, one that fails every
try and goes back to RabbitMQ, a refused recipient going straight to the DLQ, and a wrong password going back without
more tries. `EmailFailuresTests` and `SmtpEmailSenderTests` check the sorting, including real refusals from an SMTP
server.

### Upgrading the email queue to five deliveries (R4-09)

The queue created before R4-09 has `x-delivery-limit=5` (six total deliveries). The updated hosts declare
`x-delivery-limit=4` (five total deliveries). Upgrade the development queue before starting the new hosts:

1. Stop the **old API** with `docker compose stop api1 api2 api3`, so no relay publishes new messages.
2. Keep the **old worker** running until the email queue drains, then stop it with `docker compose stop worker`.
3. In the RabbitMQ management UI, verify that `email.order-confirmation` has `messages_ready=0`,
   `messages_unacknowledged=0` and `consumers=0`. If any is nonzero, stop the upgrade and handle those messages first.
4. Delete only that empty queue, with broker-side checks against a concurrent delivery or consumer:

   ```sh
   docker compose exec rabbitmq rabbitmqctl delete_queue email.order-confirmation --if-empty --if-unused
   ```

5. Start the updated hosts together with `docker compose up -d --build api1 api2 api3 worker`. Check that the queue is quorum
   with `x-delivery-limit=4`, its DLX is `shop.dlx`, and both hosts connect successfully.

Keep `email.order-confirmation.dlq` and the RabbitMQ volume. This preserves dead letters and database outbox rows.
This is an operator procedure for the existing development stack; integration tests use disposable containers.

## Messaging and real-time integration coverage (task R4-09)

Use Docker Desktop in Linux-container mode (or a Linux Docker Engine). The required service images match compose:

```sh
docker pull rabbitmq:4.3.7-management-alpine
docker pull postgres:18
```

Testcontainers starts disposable brokers/databases on random ports and downloads its Ryuk cleanup image itself.
Compose can remain stopped. The tests replace only external catalog, inventory and email boundaries; they never
send to a real SMTP provider or require its credentials.

| Behavior | Automated evidence |
| --- | --- |
| Order and outbox commit together; rollback leaves no event | Existing `OutboxTests` |
| HTTP order reaches RabbitMQ and the admin through the real background relay | `R409EndToEndTests.AnHttpOrderIsSavedAndPublishedByTheBackgroundRelayToRabbitMqAndTheAdmin` |
| HTTP returns 201 during an outage; the durable event publishes after recovery | `R409EndToEndTests.AnHttpOrderReturns201WhileRabbitMqIsDownAndItsOutboxPublishesAfterRecovery` |
| Status change over HTTP reaches A without leaking to B, whose own update acts as a barrier | `R409EndToEndTests.HttpStatusChangesReachTheirOwnerWithoutLeakingToAnotherWorkingConnection` |
| Stopped worker/broker retain waiting emails | Existing `OrderConfirmationDeliveryTests.StoppingTheWorkerAndRestartingTheBrokerKeepsEveryConfirmationEmail` |
| Five failed deliveries, three SMTP attempts each, then DLQ | `OrderConfirmationDeliveryTests.FiveFailedDeliveriesMakeFifteenSmtpAttemptsAndDeadLetterTheMessage` |
| Permanent recipient refusal goes straight to DLQ after one attempt | `OrderConfirmationDeliveryTests.APermanentRecipientRefusalDeadLettersAfterOneSmtpAttempt` |
| Connection lost after inbox commit and before ACK redelivers, without another email | `OrderConfirmationDeliveryTests.AConnectionLostAfterRecordingButBeforeAckRedeliversWithoutSendingAgain` |
| DLQ move-back preserves the ID/type/body and resets the failure budget | Existing `ConsumerTests.MovingTheDlqBackGivesTheMessageAllItsTriesAgain` |
| Customer refused at admin negotiate and direct WebSocket handshake | Existing `SignalRSecurityTests.CustomerCannotOpenTheAdminChannel`, strengthened for the direct handshake |

`R409ApiFixture` explicitly initializes the test database before starting the actual `Shop.Api.Program` host with
production JWT validation, outbox relay,
publisher and live consumer. Each test has one database for all module schemas; its temporary live queue and API
host are disposed between cases. Existing smaller module and SignalR tests remain in place.

During broker recovery the exclusive live queue can return after an event has been published. The outage test proves
durability using the email queue and reads an HTTP snapshot; existing live-consumer tests cover `ResyncRequired`.
These tests do not promise replay of every event to a disconnected live subscription.

The crash test holds the real confirmation handler after it records success, then forcibly closes only the worker's
AMQP connection. It cancels recovery before releasing that callback and starts a new worker. This tests inbox
deduplication at that boundary. Delivery remains **at least once**: SMTP accepting an email before its inbox record
commits can still produce a duplicate after a crash.

Run the four suites from the repository root:

```sh
dotnet build Shop.slnx -m:1
dotnet test tests/Shop.Messaging.IntegrationTests --no-build --logger "trx;LogFileName=messaging.trx" --results-directory artifacts/test-results/r4-09
dotnet test tests/Shop.Modules.Orders.IntegrationTests --no-build --logger "trx;LogFileName=orders.trx" --results-directory artifacts/test-results/r4-09
dotnet test tests/Shop.Modules.Notifications.IntegrationTests --no-build --logger "trx;LogFileName=notifications.trx" --results-directory artifacts/test-results/r4-09
dotnet test tests/Shop.Api.IntegrationTests --no-build --logger "trx;LogFileName=api.trx" --results-directory artifacts/test-results/r4-09
```

Docker is required for this coverage; an unavailable engine is a failed/unperformed verification, not a reason to
skip tests. New scenarios use bounded state waits and capture broker/database diagnostics on failure.
The [.NET integration tests workflow](.github/workflows/dotnet-integration-tests.yml) builds the solution and runs all
four suites on Ubuntu for pull requests, pushes to main and manual dispatch. It runs remaining suites after a failure
and uploads TRX reports even if a suite fails.

## Interview proof kit (task R4-10)

Three parts, for showing Requirement 4 in an interview:

- [`tests/proof/requirement-4`](tests/proof/requirement-4/README.md): the six interview steps on the running stack,
  one script for each part. They show orders not waiting for a stopped worker, a crash mid-send, a wrong mail
  password ending in the DLQ and coming back, and the live pages with the admin hub refusing a customer. Each
  script puts the stack back as it was, and `results/` has the output of a real run.
- [ADR 0001](docs/adr/0001-transactional-outbox-and-at-least-once-delivery.md): why the order and its event go
  through a transactional outbox, why delivery is at least once, and the alternatives we turned down.
- [The mock interview](docs/interview/requirement-4.md): likely questions with short answers, each pointing to
  the code, test or proof step that backs it.

## Local-state audit (task R5-01)

Everything one API instance keeps for itself, found before Requirement 5 runs three of them behind Nginx:
[the audit](docs/requirement-5/local-state-audit.md) lists 9 problems, each with the task that fixes it and a
before-and-after check, 8 things that are already safe, and where the Requirement 5 plan is wrong. The checks run
three instances side by side on a throwaway Postgres and Redis, never touching the Compose stack:

```sh
tests/proof/requirement-5/local-state-checks.sh   # about two minutes; Docker, .NET SDK, curl, jq; Linux or WSL
```

The first check starts three instances at once on an empty database; in the
[saved run](tests/proof/requirement-5/results), 6 of 9 first starts crashed while seeding the admin. The others show
15 login tries a minute instead of 5, an uploaded image found by 3 requests in 9, an edited product served stale for
up to 5 seconds, a SignalR WebSocket refused by the instance that didn't negotiate it, and warehouse retries run by
several instances at once.

## Nginx load balancer (task R5-02)

[`deploy/nginx/default.conf`](deploy/nginx/default.conf) is the load balancer that R5-03 puts in front of api1–api3:
`least_conn` over a shared `zone`, `resolve` so a changed or new address is picked up without a reload, passive health
checks (`max_fails=1 fail_timeout=10s`), WebSocket upgrades under `/hubs/`, and an `upstreams` access log that names
the instance behind each request. Compose runs it in front of api1–api3 since R5-03; its checks run it in front of
three API containers on a throwaway network, and CI runs `nginx -t` on every change to it:

```sh
tests/proof/requirement-5/nginx-checks.sh   # about two minutes; Docker, .NET SDK, curl, jq, nginx:1.30.7-alpine
```

In the [saved run](tests/proof/requirement-5/results), 300 requests split 100/100/100, and api3, started after Nginx,
got traffic 1 s after it began listening, without a reload. While api2 stopped and started again, all 246 requests
answered 200; the slowest took 2 s, the connect timeout before Nginx tried another instance.

What changed from the plan's sketch, each found by these checks:

- `Host $http_host`, not `$host`, which drops the port from the URLs the API builds (audit finding 8). The OpenAPI
  document now points at `http://127.0.0.1:58080/`, back through Nginx.
- The access log is set in the `server` block. At the `http` level it would add to the image's own log and write every
  request twice.
- `resolver_timeout 5s`. `valid=10s` only applies to lookups that succeed; after a failed one (Docker answers SERVFAIL
  for a stopped container), Nginx waits `resolver_timeout`, 30 seconds by default, and a new instance waited 25 s.
- `client_max_body_size 4m`: Nginx's default of 1 MB refuses product images, which may be 2 MB. Above 4 MB Nginx
  answers itself; between the two limits the API answers with its Problem Details.
- `server_tokens off`, so Nginx doesn't advertise its version where the API sends no `Server` header (task 13).
- SignalR's browser client puts the user's token in the URL; the access log hides it (`access_token=[hidden]`).
- `nginx:1.30.7-alpine`, the current stable release; the plan named 1.28.

**Why R5-10 comes before R5-03.** Through Nginx, a page that negotiates before opening its WebSocket, as the live
pages did, failed 10 times out of 10: with nothing else going on, `least_conn` takes turns, so the WebSocket always
reaches a different instance from the negotiation (audit finding 7). R5-10, below, makes the pages skip negotiation.

## SignalR behind Nginx (task R5-10)

The live pages open their WebSocket straight away, without the negotiate request first: `connectLive` in
[`session.js`](src/Shop.Api/wwwroot/app/js/session.js) sets `skipNegotiation` with the WebSockets transport, and the
hubs accept WebSockets only. Reconnecting already reloaded what the page shows (R4-08); it skips negotiation too, so a
page whose instance stops reconnects through Nginx to another one.

- A browser never tells a page why a WebSocket was refused, so an expired token would have looked like a network
  fault. When connecting fails, and only then, `connectLive` asks the hub's negotiate endpoint with the same token: an
  ordinary request, whose 401 or 403 the pages act on as before, signing out with a message.
- Long polling and server-sent events take several requests that must all reach the instance holding the connection,
  which Nginx without sticky sessions can't promise. The hubs now refuse them at once:
  `SignalRSecurityTests.HubsOfferOnlyWebSocketsAndRefuseTheOtherTransports`, which fails without the change.
- The .NET test client still negotiates: the integration tests run against one in-memory server, and they rely on the
  connection ID that only negotiating gives a client.

Step 8 of the Nginx checks runs the pages' own code in headless Chromium, through Nginx:

```sh
tests/proof/requirement-5/nginx-checks.sh   # step 8 needs Chromium or Chrome; without one it says so and skips
```

In the [saved run](tests/proof/requirement-5/results), `connectLive` connected 10 times out of 10 with no negotiate
request, where step 6 shows the old way failing 10 times out of 10. When the instance holding the page's WebSocket
stopped, the page went live, reconnecting, live within 3 seconds (its first retry can still reach the stopping
instance; the next comes 2 seconds later), then reloaded its orders.

Found while testing: since Nginx 1.29.7, Nginx keeps idle connections to each instance open for the next request
(keepalive is on by default, over HTTP/1.1), where the plan's 1.28 closed every one. Nginx drops an idle connection
after 60 seconds, before Kestrel would after 130; the config now says so.

## Three API instances behind Nginx (task R5-03)

`docker compose up` runs the API three times, as `api1`, `api2` and `api3`, from one `x-api` anchor in
[`compose.yaml`](compose.yaml), with Nginx in front (R5-02's config). Nginx is the only way in: it answers at
`http://localhost:8080` (`API_PORT`), and no instance publishes a port. The email worker runs three times too, and
RabbitMQ gives each message to one of them; `docker compose stop worker` stops all three.

- Every response carries `X-Instance`, errors included, so `curl -i` or a load test sees which instance answered. It
  comes from `INSTANCE_NAME`, which Compose sets per instance, and also names each instance's RabbitMQ connection
  (`shop-api api2`); a worker names its connection after its container. Compose already starts each log line with the
  container's name, such as `api2-1`.
- All three API instances and all three workers wait for `migrator` to exit successfully (R5-04); api2 and api3
  no longer depend on api1. The API healthcheck asks `/health` with bash, as the image has no curl. None of the
  runtime hosts applies migrations or seeds data.
- The three instances share the `images` volume, so an upload through one is found by all, as long as they share a
  machine (finding 4; R5-07 moves images to object storage).
- k6 (`perf/run.sh`), the perf proofs and the Requirement 4 proof kit reach the API through Nginx; commands that named
  the `api` service now name `api1 api2 api3`. YAML merges are shallow, so each instance merges the shared environment
  before adding its name; the migrator overrides its inherited dependencies to wait only for PostgreSQL.

The checks start the whole stack as a separate Compose project, on empty volumes and other ports, so the stack you run
is never touched:

```sh
tests/proof/requirement-5/compose-checks.sh   # about two minutes; Docker, .NET SDK, curl, jq, a .env with JWT_SIGNING_KEY
```

In the historical R5-03 [saved run](tests/proof/requirement-5/results/compose-checks-20261121T154951Z.txt), before the
migrator was introduced, everything was healthy after 17 s; api2 and api3 started 5 s
after api1, and only api1 applied migrations or seeded. 300 requests through Nginx split 100/100/100 by `X-Instance`,
and k6 with 10 users for 10 s got 10,504, 10,306 and 10,501 answers with no failures. An order placed through Nginx
returned 201, and one of the three workers sent its email.

An existing stack still has the old `api` container: `docker compose up -d --build --remove-orphans` removes it.

## Explicit migrations and seeding (task R5-04)

`docker compose up -d --build --wait` runs `migrator` from the same `shop-api` image as the three API instances.
It waits only for a healthy PostgreSQL, applies the Users, Products, Orders and Notifications migrations to the
primary connection (`ConnectionStrings:Shop`), then seeds the admin and the optional demo catalog. It never starts
an HTTP listener, a RabbitMQ connector or a background consumer. Redis cache invalidation remains best effort;
RabbitMQ or the warehouse being down cannot block initialization.

The migrator shares the `images` volume with the API instances, so the demo PNGs remain available after it exits.
It has no published port, no HTTP healthcheck and no restart policy. `Exited (0)` is its successful final state,
not an unhealthy API. Compose starts the API instances and workers only after that successful exit:

```sh
docker compose ps -a migrator
docker compose logs migrator
```

Seeding runs only on explicit initialization. An existing admin keeps its password, and existing demo products,
their edits and their images are preserved. Missing admin credentials log a warning and skip that seed;
`DEMO_SEED_ENABLED=false` skips the demo catalog. An invalid seed password or a failed migration returns exit code
1 and prevents new dependent hosts from starting. Completed migrations are retained if a later seed fails, so
correcting the cause and repeating initialization is safe.

For a failed Compose initialization, correct the configuration, ensure PostgreSQL is running, and recreate the
actual migrator service before starting the rest:

```sh
docker compose up -d postgres
docker compose up --no-deps --force-recreate --exit-code-from migrator migrator
docker compose up -d --wait
```

After code changes, build the updated image first (`docker compose build api1 worker`), then use the same sequence
to apply new migrations before starting the updated runtime hosts. An ordinary `docker compose restart api1 api2
api3 worker` does not initialize the database. A one-off `docker compose run --rm --no-deps migrator` can explicitly
rerun initialization against an existing database, but it does not repair the exit state of a failed Compose
service; use the recreate sequence above for that.

`MigratorTests` executes the real command against disposable PostgreSQL databases, checking all four schemas,
repeatability, seed opt-outs, failure/retry and absence of automatic API initialization in Development and Production.
The updated `compose-checks.sh` proves failure gating, successful retry, image availability on all three instances,
order/email delivery and runtime restarts. The local-state and Nginx proof scripts also initialize explicitly.

In the [R5-04 verification](tests/proof/requirement-5/results/migrator-verification-20261123.txt), 660 test cases
passed. The [cold-start audit](tests/proof/requirement-5/results/local-state-checks-r5-04-20261123.txt) had 0 crashes
in 9 starts, compared with 6 of 9 in the original audit. The
[Compose proof](tests/proof/requirement-5/results/compose-checks-r5-04-20261123.txt) showed the failed migrator blocking
all six hosts, then a successful retry before they started; none migrated or seeded, including on restart. Its 300
requests split 100/100/100, and k6 reported 0% failed requests. The
[Nginx proof](tests/proof/requirement-5/results/nginx-checks-r5-04-20261123.txt) also passed after explicit initialization;
its optional browser-only step was skipped because Chrome/Chromium was unavailable.

## Trusted proxy headers and real client addresses (task R5-05)

Compose uses `SHOP_NETWORK_SUBNET` (default `172.30.50.0/24`) both for its default network's IPAM and for the three
API instances' trusted forwarding range. All containers on that network are inside this trust boundary. The API
processes only `X-Forwarded-For` and `X-Forwarded-Proto`, for one proxy hop, before HTTPS redirection, HSTS and the
login limiter. Its default loopback trust is removed. `X-Forwarded-Host` is not processed: Nginx already preserves
the original `Host`, including its port, with `$http_host`.

Nginx is the public edge. In both `/` and `/hubs/` it replaces any supplied `X-Forwarded-For` chain with
`$remote_addr`, and any `X-Forwarded-Proto` with `$scheme`. Both header lists must have the same length. Direct
requests from outside the trusted range cannot select their client address or protocol using these headers.
The limiter reads the resulting `Connection.RemoteIpAddress`, normalizes IPv4-mapped IPv6, and logs one
`Login rate-limit partition {ClientIp} trace {TraceId}` entry per login request, including a rejected request.
Nginx's access log continues to record the caller and to hide SignalR access tokens. No credentials or request
bodies are added to the application log.

Direct `dotnet run` uses `ReverseProxy:Enabled=false` and an empty `ReverseProxy:TrustedNetworks` by default,
in Development and Production. To run behind a trusted proxy outside Compose, explicitly set:

```text
ReverseProxy__Enabled=true
ReverseProxy__TrustedNetworks__0=172.30.50.0/24
```

Enabling forwarding without a valid explicit CIDR fails startup; catch-all `/0` ranges are rejected. The app's
explicit policy also overrides the framework's permissive `ASPNETCORE_FORWARDEDHEADERS_ENABLED` shortcut, so that
variable cannot replace this configuration. Extra proxy hops or TLS termination in front of Nginx require a
separate deployment configuration; this setup has one public Nginx hop and serves the local demo over HTTP.

An existing Compose network cannot change its subnet in place. Choose a non-overlapping `SHOP_NETWORK_SUBNET`,
then recreate the project's containers and network, retaining its database/image/broker volumes:

```sh
docker compose down                 # without -v: retain data and images
docker compose up -d --build --wait  # migrator initializes first, then the runtime hosts start
```

`ForwardedHeadersTests` exercises the real Program pipeline with controlled TestServer connection addresses:
trusted/untrusted and IPv4-mapped peers, direct-run behavior, malformed/asymmetric headers, one-hop limits,
OpenAPI URLs, HTTPS/HSTS and independent login quotas with the same normalized key in logs. Configuration tests
cover invalid CIDRs, missing ranges, catch-all ranges and framework configuration precedence.

The isolated proof uses two client containers and one API, so their actual IPs are distinct even on Docker Desktop,
and distributed rate-limit counters (R5-06) cannot confuse the result:

```sh
tests/proof/requirement-5/forwarded-headers-checks.sh before # capture before changing the application
tests/proof/requirement-5/forwarded-headers-checks.sh        # verify the fixed application
```

Client 1 gets five 401s and then a 429. Before the fix, client 2's first request is also 429; after it, client 2
gets its own 401, and forged header changes cannot reset client 1's quota. The script compares both client IPs
with the application partition logs and Nginx's access log, and checks the external OpenAPI scheme/host/port.
Compose proofs use a separate test subnet; standalone Nginx proofs discover their disposable network's subnet.
The login counters remain per API instance until R5-06.

The [R5-05 verification](tests/proof/requirement-5/results/forwarded-headers-verification-20261123.txt) records 690
passing test cases, including 30 forwarding/configuration cases. The two-client
[before](tests/proof/requirement-5/results/forwarded-headers-before-20261123.txt) and
[after](tests/proof/requirement-5/results/forwarded-headers-after-20261123.txt) runs show client 2 changing from a
shared 429 to its independent 401. The
[Compose](tests/proof/requirement-5/results/compose-checks-r5-05-20261123.txt) and
[Nginx](tests/proof/requirement-5/results/nginx-checks-r5-05-20261123.txt) regressions passed; the optional
browser-only Nginx step was skipped because Chrome/Chromium was unavailable to the script.

## Verify

```sh
dotnet build Shop.slnx -m:1
dotnet test Shop.slnx -m:1
```

Integration tests use PostgreSQL 18 containers, and the messaging tests a RabbitMQ 4.3 container. With Docker unavailable, configure separate
disposable databases using `SHOP_USERS_TEST_CONNECTION`, `SHOP_PRODUCTS_TEST_CONNECTION`,
`SHOP_ORDERS_TEST_CONNECTION`, `SHOP_NOTIFICATIONS_TEST_CONNECTION` and `SHOP_CONFORMANCE_TEST_CONNECTION`. Tests apply migrations and change data.
The conformance fixture creates its own temporary JWT signing key.

See [security-headers.http](tests/http/security-headers.http) for manual header and CORS checks.
