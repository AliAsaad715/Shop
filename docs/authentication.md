# Login and current user (task 10)

## Configure the signing key

JWTs use HS256 with a random key of at least 32 bytes. The configuration value is the
**base64 encoding** of those bytes. No signing key is committed or generated silently at startup.
A missing, malformed or short key stops startup with a configuration error. Keep the key
stable across restarts if existing tokens should keep working.

Generate a key in PowerShell 7:

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

For Docker Compose, copy `.env.example` to `.env`, set `JWT_SIGNING_KEY` to the generated
value, configure your `ADMIN_EMAIL` and `ADMIN_PASSWORD`, then run:

```sh
docker compose up --build
```

Compose passes the key to the API as `Jwt__SigningKey`. For a local `dotnet run`, set that
environment variable directly; .NET does not automatically read Compose's `.env`:

```powershell
$env:Jwt__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
docker compose up -d postgres
dotnet run --project src/Shop.Api --launch-profile http
```

If Compose is used to start PostgreSQL alone, it still needs the configured `.env` for
the later API run. The local API uses port 5080; Compose exposes port 8080.

Other JWT settings are `Jwt__Issuer` (default `shop`), `Jwt__Audience` (default `shop-api`)
and `Jwt__LifetimeMinutes` (default 15; allowed range 15-30). Both token issuance and
Bearer validation use the same settings. Validation checks the signature, HS256 algorithm,
issuer, audience and lifetime with 30 seconds of clock skew. Claims keep their original names.

## Sign in

`POST /api/v1/auth/login` is anonymous and accepts:

```json
{
  "email": "customer@example.com",
  "password": "your account password"
}
```

A successful response is `200 OK`:

```json
{
  "accessToken": "<signed JWT>",
  "tokenType": "Bearer",
  "expiresIn": 900
}
```

The token contains `sub` (user ID), `email`, `role`, `jti`, `iss`, `aud`, `iat`, `nbf` and
`exp`. Roles come from Identity, never from the login body. No cookies are issued.
Responses carrying tokens use `Cache-Control: no-store`.

Unknown email, wrong password, locked account, unsupported two-factor account, and
missing credential fields all return `401` Problem Details with title `Invalid credentials`
and code `users.invalid_credentials`. Invalid JSON returns the API's standard `400`
Problem Details. Every error includes `traceId`.

## Read your profile

Call `GET /api/v1/users/me` with `Authorization: Bearer <accessToken>`. It returns:

```json
{
  "id": "<user UUID>",
  "email": "customer@example.com",
  "roles": ["Customer"]
}
```

The user is selected by the validated token's `sub`; query/body IDs cannot select another
account. Email and roles are loaded from Identity and no password hashes or security
stamps are returned. Responses use `Cache-Control: no-store`. Missing/invalid tokens,
an invalid subject, or a deleted account return `401` Problem Details.

The API's `/docs` supports entering the token in its Bearer authorization control.
`/health`, `/docs`, registration and login remain anonymous. Global fallback authorization,
role policies and `ICurrentUser` belong to task 11; per-IP rate limiting and counting failed
logins toward account lockout belong to task 12.

## Verify

```sh
dotnet build Shop.slnx
dotnet test Shop.slnx
```

Integration tests use disposable PostgreSQL 18 containers when Docker is available. To
use existing **disposable** databases instead, set `SHOP_USERS_TEST_CONNECTION` and
`SHOP_PRODUCTS_TEST_CONNECTION` before testing. Never use shared or production databases.
The HTTP tests exercise real registration, login, JWT validation, profile lookup, malformed
JSON, generic credential failures, and expired/tampered/misconfigured tokens. JWT unit
tests verify claims, lifetime, unique token IDs, signatures and configuration validation.
