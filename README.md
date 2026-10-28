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
and no eval. `/docs` permits Scalar's local scripts, inline initialization and styles, and
same-origin API calls. Scalar's external fonts are disabled so the UI does not require a CDN.

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
