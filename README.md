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
`http://localhost:8080/docs`. Keep `.env`, signing keys and certificates out of Git.

For local HTTPS, start PostgreSQL with `docker compose up -d postgres`, configure a trusted
.NET development certificate with `dotnet dev-certs https --trust`, and run:

```powershell
$env:Jwt__SigningKey = '<your base64 signing key>'
dotnet run --project src/Shop.Api --launch-profile https
```

Open `https://localhost:7080/docs`; HTTP on port 5080 redirects to that HTTPS listener.
The `http` launch profile and default Compose setup support HTTP-only local demos.

## Browser security (task 13)

The API sends `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, and
`Referrer-Policy: no-referrer`, including on errors. Kestrel's `Server` header is disabled.
Authenticated responses use `Cache-Control: no-store`, including routing errors.

`/api/*` uses a strict Content Security Policy with `default-src 'none'`, no inline scripts
and no eval. `/docs` permits Scalar's local scripts, inline initialization and styles, and same-origin API calls. Scalar's external fonts are disabled so the UI does not require a CDN.

CORS permits only explicitly configured frontend origins. Development defaults to
`http://localhost:5173`. For Compose, change `FRONTEND_ORIGIN` in `.env`, then run
`docker compose up -d api`. For a direct API run, configure `Cors:AllowedOrigins` or:

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
a TLS proxy; the HTTP demo configuration does not provide production TLS. Configure trusted
forwarded headers before HSTS/redirection when deploying behind a proxy; raw forwarding
headers are not trusted by this application. See Microsoft's [HTTPS deployment guidance](https://learn.microsoft.com/aspnet/core/security/enforcing-ssl?view=aspnetcore-10.0).

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

All IDs start `019a2180-0000-7000-8000-000000000`. The app adds them when `DemoSeed__Enabled` is `true`,
which compose sets by default (`DEMO_SEED_ENABLED` in `.env`). Production and the tests leave it off.
Existing products are never changed on restart. `DemoSeedTests` fails if `002_seed.sql` and the app's
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

`db-replica` is a streaming replica of `postgres`, for the reads that may be a moment old, which R3-11 sends to it.
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

## Verify

```sh
dotnet build Shop.slnx -m:1
dotnet test Shop.slnx -m:1
```

Integration tests use PostgreSQL 18 containers. With Docker unavailable, configure separate
disposable databases using `SHOP_USERS_TEST_CONNECTION`, `SHOP_PRODUCTS_TEST_CONNECTION`,
and `SHOP_CONFORMANCE_TEST_CONNECTION`. Tests apply migrations and change data.
The conformance fixture creates its own temporary JWT signing key.

See [security-headers.http](tests/http/security-headers.http) for manual header and CORS checks.
