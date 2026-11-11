# API conformance tests (task 23)

Run from the repository root with Docker available:

```sh
dotnet test tests/Shop.Api.ConformanceTests
```

The full-host tests use WebApplicationFactory against the actual Shop.Api Program, real module registrations, startup migrations, versioned routes and error middleware. PostgreSQL 18 is provisioned with Testcontainers. Alternatively, set `SHOP_CONFORMANCE_TEST_CONNECTION` to a dedicated disposable database. These tests apply migrations and create customer accounts; do not point them at shared or production data.

The fixture is scoped to this conformance project while task 07's shared test setup is unavailable. It creates a random signing key for the test host and does not provide production JWT token helpers. A test-only authentication scheme supplies an authenticated identity solely for Cache-Control checks. It does not inject security headers or replace the production error/versioning middleware. HTTPS requests use `shop.test` so HSTS is not accidentally exempted as localhost; TestServer simulates the HTTPS request scheme and does not test TLS itself.

## Coverage

- The production error pipeline is exercised with test-only bare status producers for 400, 401, 403, 404, 405, 409, 413, 415, 422, 429 and 503, plus actual binding and unhandled-exception failures. These cases verify formatting, not authentication, quota or upload enforcement.
- Actual API requests check malformed JSON, validation field errors, duplicate email, unknown routes/methods, unsupported API versions, absent version segments and a sanitized 500. The 500 case temporarily changes the test host's connection setting after startup to a nonexistent database and restores it in finally; API test classes share a serialized collection.
- Unsupported URL-segment versions correctly return 404, as documented by [API Versioning](https://dotnet.github.io/aspnet-api-versioning/aspnet-core/errors.html). The docs redirect and its rendered HTML target are both checked for security headers.
- Success, docs and error responses must include nosniff, X-Frame-Options: DENY and Referrer-Policy: no-referrer, with no Server header.
- API responses need a strict CSP; HTTPS needs HSTS; HTTP must not send HSTS; authenticated responses need Cache-Control: no-store.
- The real Kestrel AddServerHeader option must be disabled, because a TestServer wire response alone cannot prove what Kestrel emits.

Task 13 supplies the production security headers, CSP, HSTS and cache policy. All assertions
run without filters or skipped tests. Additional host integration tests cover exact CORS
origins, preflight requests, headers on 429/500 responses, actual Bearer cache behavior on
routing errors, and HTTPS redirects.

## Replica routing (R3-11)

`ReadRoutingTests` has its own isolated primary, a physical streaming standby copied with `pg_basebackup`, Redis,
and two real Shop.Api hosts using valid signed JWTs and the production authorization policies. It always requires
Docker; `SHOP_CONFORMANCE_TEST_CONNECTION` does not replace these databases. The warehouse boundary is a stub.

Pausing WAL replay proves eligible replica reads, primary-only cache fills and Identity/current-price reads,
cross-host read-your-writes pins and expiry, bypass of another host's warm L1, preserved order ownership, and
fallback after a pooled replica connection fails. Product/order creation and edits pin only after a successful save;
failed database writes do not create a marker. Redis failure sends authenticated reads to primary and cannot
turn a committed write into a failed response. Test replay barriers wait for the primary's inserted WAL position,
including when the test database uses asynchronous commit. No developer Compose data or performance snapshots
are modified by this fixture.

Legacy test hosts explicitly disable routing so developer environment variables cannot change their primary-only
query assertions. The R3-11 fixture enables it explicitly and exercises both database paths.
