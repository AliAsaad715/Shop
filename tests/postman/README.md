# Postman and HTTP proof (task 25)

This directory contains an OpenAPI-derived Postman v2.1 collection and an ordered, hand-authored `Security proof` folder. No production code or module references change. The independent generated requests are examples; they are not an authorization test suite.

## Current prerequisite status

The checked-in OpenAPI snapshot was exported from the actual API at main `d86a5bc`, after catalog task 16 merged. It describes four operations: customer registration, product list/detail and categories. The generated folder and `../http/generated.http` reflect exactly that document, rather than inventing future operations. The collection's `x-shop-source` records the source hash, operation list and missing proof prerequisites.

Task 25 depends on product upload (15) and Orders (19-21). Login/authorization (10-11) and security headers (13) must also be integrated for this proof to pass; catalog task 16 is already on main. The script checks the live OpenAPI on every run and stops before creating data if any of the eleven required operations or local inputs are absent. The proof is **not yet verified against the complete real API**. Passing the protocol-harness tests below does not establish that the application's authorization is correct.

## Install and regenerate

Use Node.js 22 or later. From this directory, while the API is running:

```sh
npm ci --ignore-scripts
npm run generate -- http://localhost:8080/openapi/v1.json
```

This refreshes `openapi.v1.json`, `shop.postman_collection.json` and `../http/generated.http`. It generates request/response samples from schemas, preserving tags, bodies, query parameters and authentication metadata, with request URLs controlled by `baseUrl`. Schema placeholders need valid business values before sending; synthetic response sample values can vary between conversions. The source document currently declares Bearer globally, including anonymous registration; its generated requests inherit that declaration. The security scenarios explicitly set their own authorization.

Offline regeneration uses the checked-in document:

```sh
npm run generate -- openapi.v1.json
```

Regenerate from the live API after the prerequisite PRs merge, review the new request/response contracts, and commit the refreshed snapshot and artifacts in this task branch. In particular, adapt the proof configuration to the actual task 10 JWT response and task 19/21 request shapes before claiming acceptance.

## Configure the proof locally

Copy `local.postman_environment.example.json` to `proof.local.json` (ignored by Git) and fill its values:

| Variable | Value |
| --- | --- |
| `baseUrl` | API origin without a trailing path, e.g. `http://localhost:8080` |
| `adminEmail`, `adminPassword` | The admin account configured through the API's environment settings |
| `tokenProperty` | Dot-separated path to the JWT in the real login response; `accessToken` is an adjustable placeholder until task 10 lands |
| `orderBody` | JSON string matching the real create-order contract; use literal `{{productId}}` for the product this proof creates |
| `statusBody` | JSON string matching a valid admin transition from a newly created Pending order to Confirmed, including any required concurrency value |

For illustration only, if task 19 accepts `lines`, `orderBody` could be the JSON string `{"lines":[{"productId":"{{productId}}","quantity":1}]}`. This is not a claim about an unimplemented DTO. Choose the actual body from the regenerated OpenAPI. Similarly, `{"status":"Confirmed"}` is valid only if that is task 21's final contract; include its version field if required. Those two inputs deliberately have no guessed defaults.

The proof provisions two unique Customers per run, logs in as those Customers and the configured Admin, selects a seeded category, uploads `fixtures/product.png`, creates a product and then creates an order as CustomerA. UUIDs are captured from the actual `Location` headers; JWTs and customer passwords stay in the run's collection variables. No real credentials belong in committed files. Do not export the populated collection/environment after a Postman UI run.

## Run with Newman

Use a fresh disposable database: this suite creates accounts, an image, a product and an order, then changes its status. The API has no cleanup endpoints. Dispose of that test database and image volume after the demo; the runner does not delete them.

PowerShell:

```powershell
$env:SHOP_PROOF_ALLOW_WRITES = '1'
npm run proof -- proof.local.json
```

POSIX shell:

```sh
SHOP_PROOF_ALLOW_WRITES=1 npm run proof -- proof.local.json
```

`SHOP_BASE_URL` optionally overrides the environment's origin. The runner selects only the `Security proof` folder, one iteration, stops on failures, restricts fixture reads to this directory and returns a nonzero status on failures or incomplete execution. It leaves TLS certificate verification enabled. It does not export JSON reports containing request passwords and tokens.

The same collection can be imported into Postman with the local environment. Set Postman's working directory to `tests/postman` so the image's relative file reference resolves. Run the entire `Security proof` folder in order. The Node runner's write opt-in is a runner setting; importing the collection into Postman does not provide that guard.

## What the proof establishes when it passes on the real API

- Anonymous protected calls return 401; malformed bearer credentials are rejected.
- Customers cannot create products; Admin can. Anonymous catalog reads work.
- Only a Customer creates an order; Admin cannot use the Customer-only endpoint.
- The owner reads the newly created order. **Proof 1:** CustomerB gets 404 for that existing order; Admin can read the same UUID. CustomerB's order list does not disclose it.
- **Proof 2:** CustomerA gets 403 when changing their own order's status. Reading it again confirms the response body is unchanged. Admin can perform the intended transition, demonstrating that the transition request is usable.
- Error responses carry Problem Details with `code` and `traceId`; baseline security headers and no-store on bearer responses are asserted. Unsupported API version 999 is rejected.

These scenarios are task 25's demo proof, not task 22's complete authorization matrix, task 23's full conformance suite, lockout/rate-limit tests, or an assertion about security audit logs. Successful logins from one IP can meet task 12's rate limit; use its documented test configuration if needed. For the required ownership security-log entry, correlate the rejected request's `traceId` with the API log during the demo.

## HTTP files and tool validation

`../http/generated.http` is regenerated from the same OpenAPI conversion. `../http/security-proof.http` is a manual companion with expected statuses and local placeholders. Open these with a `.http`-capable client (for example Visual Studio or the VS Code REST Client extension); the executable assertions live in Postman/Newman.

```sh
npm test
```

The Node tests verify actual-snapshot conversion, request counts and URLs, and execute Newman against an explicitly test-only HTTP protocol harness. The harness checks captured IDs and a real multipart fixture, detects missing prerequisites/inputs before writes, and deliberately permits each forbidden action in turn to prove that the security assertions fail. These green tests validate the tooling only. Running `npm run proof` against current main is expected to fail at prerequisites; the end-to-end acceptance run remains pending the tasks listed above.

Tool interfaces follow the official [OpenAPI-to-Postman converter](https://github.com/postmanlabs/openapi-to-postman) and [Newman runner](https://github.com/postmanlabs/newman) documentation. Exact dependency versions are locked in `package-lock.json`.
