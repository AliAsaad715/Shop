# Orders integration tests

Run from the repository root with Docker available:

```sh
dotnet test tests/Shop.Modules.Orders.IntegrationTests
```

The tests start an isolated PostgreSQL 18 container with Testcontainers, apply the real Orders migration and call the endpoints through ASP.NET TestHost. Alternatively, set `SHOP_ORDERS_TEST_CONNECTION` to an existing **disposable** PostgreSQL database; the fixture deletes every order in it.

The host wires only the Orders module, with three stand-ins:

- A test authentication scheme replaces the Users module's JWT validation. `X-Test-User` becomes the `sub` claim and `X-Test-Role` the `role` claim, and the `AdminOnly` and `CustomerOnly` policies are registered as the real host does.
- `FakeProductCatalog` replaces `IProductCatalog` with two products (Book 19.95, Lamp 45.50).
- `RecordingInventory` replaces `IInventoryService`, records every reserve and release, and can be told to refuse reservations, which proves the 409 path that Requirement 1's placeholder can't trigger.

What the tests cover:

- `POST /orders`: the customer comes from the token and prices from the catalog, never from the body; the reservation happens before the save; 201 with `Location`; an unknown product or a bad quantity is 422 on that line; a refused reservation is 409 and saves nothing; anonymous is 401, an admin is 403.
- `GET /orders` and `GET /orders/{id}`: a customer sees only their own orders, newest first; an admin sees all and can filter by status; someone else's order is 404, exactly like a missing one, and a warning goes to the `Shop.Modules.Orders.Security` log (**required proof 1**).
- `PATCH /orders/{id}/status`: a customer is 403 even for their own order (**required proof 2**); an admin walks Pending → Confirmed → Shipped → Delivered and the customer sees each change in the history; invalid transitions are 409 `orders.invalid_status_transition`; cancelling calls `ReleaseAsync`; two saves based on the same row version can't both succeed.
