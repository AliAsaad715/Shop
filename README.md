# Shop

## Run locally

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
- **Go**: `warehouse/gen` is written by `buf generate` and committed. After changing the proto,
  regenerate it with [buf](https://buf.build/docs/cli/installation/) and Go 1.27, then commit the result:

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

Until the warehouse joins compose in task R2-09, placing an order through compose fails.

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
