## Task

<!-- One task per PR, numbered as in the Requirement 1 plan, e.g. "02 · Solution skeleton" -->
Task:
Stream: <!-- A platform, B identity and security, C products and inventory, D orders -->

## What changed and why

<!-- A few lines. Call out any decision the reviewer should question. -->

## How to test

<!-- Steps a reviewer can run: a test, a request from the .http files, or a Postman request -->

## Checklist

<!-- Tick what applies; write N/A next to the rest -->
- [ ] Tests cover the change and `dotnet test` passes
- [ ] New endpoints have validation, OpenAPI metadata (including `ProducesProblem`) and an explicit authorization rule (`AdminOnly`, `CustomerOnly` or `AllowAnonymous`)
- [ ] Errors are Problem Details with `code` and `traceId`
- [ ] The module references only `Shop.SharedKernel` and other modules' `*.Contracts`, and never queries another module's schema
- [ ] Schema changes include a migration for this module only
- [ ] No secrets committed: the JWT key and admin password come from environment variables
- [ ] The reviewer is from another stream, following the rotation A → B → C → D → A
