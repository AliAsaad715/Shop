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
contract over gRPC on port 50051 and keeps a pgx connection pool to its own PostgreSQL database. Until
R2-04, `GetStock`, `Reserve` and `Release` answer `UNIMPLEMENTED`; compose starts it with its database
in R2-09.

| Variable | Default | Meaning |
| --- | --- | --- |
| `INVENTORY_DATABASE_URL` | required | The warehouse database, as a `postgres://` URL or a `key=value` string; pool settings such as `pool_max_conns` go here too |
| `INVENTORY_GRPC_ADDR` | `:50051` | Where the gRPC server listens |
| `INVENTORY_LOG_LEVEL` | `info` | `debug`, `info`, `warn` or `error`; logs are JSON lines on stdout |

- **Health**: the standard `grpc.health.v1.Health` service answers `SERVING` only while the database
  answers a ping, checked every 5 s, both for the server (`""`) and for `inventory.v1.InventoryService`.
  The image's `HEALTHCHECK` runs `/warehouse healthcheck`, since the distroless image has no shell or curl.
- **Stopping**: on SIGTERM, health turns `NOT_SERVING`, new calls are refused, and calls in flight get
  5 s to finish before they are cancelled, well before Docker's SIGKILL at 10 s.
- **Logs**: one line per call with the method, gRPC code, duration and the call's `order_id`, so an
  order can be followed from the app's logs. Health checks log only at `debug`.

Test it and build the image with Go 1.27 and Docker:

```sh
cd warehouse
go vet ./...
go test -race ./...
docker build -t shop-warehouse .
```

## Warehouse database and demo seed (task R2-03)

[`warehouse/db/`](warehouse/db) holds the warehouse's schema and seed as plain SQL. The `inventory-db`
container runs them in name order the first time it starts on an empty volume; compose mounts the folder
at `/docker-entrypoint-initdb.d` in R2-09. Each file is one transaction. A volume that already holds a
database skips them, so remove the volume to start again. Until R2-09, try them with:

```sh
docker run --rm -e POSTGRES_DB=inventory -e POSTGRES_USER=inventory -e POSTGRES_PASSWORD=inventory \
  -v "$PWD/warehouse/db":/docker-entrypoint-initdb.d:ro postgres:18
```

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

Until the warehouse joins compose in task R2-09, placing an order through compose answers 503.

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
reports `Degraded`, still with status 200, so browsing keeps working. Product details without stock while the
warehouse is down come with task R2-08.

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
