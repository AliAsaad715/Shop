# Products schema integration test

Run from the repository root with Docker available:

```sh
dotnet test tests/Shop.Modules.Products.Tests
```

The test starts an isolated PostgreSQL 18 container with Testcontainers. It applies the actual Products migration, checks seeded categories, filtered pagination with tied timestamps, decimal price persistence, foreign-key and nonnegative-price constraints, and rollback followed by reapplication.

Alternatively, set `SHOP_PRODUCTS_TEST_CONNECTION` to an existing **disposable** PostgreSQL database. This skips Docker. The test rolls back the Products migration and deletes its product data, so never use a development, shared, or production database.

```powershell
$env:SHOP_PRODUCTS_TEST_CONNECTION = 'Host=127.0.0.1;Port=55432;Database=shop_products_tests;Username=postgres'
dotnet test tests/Shop.Modules.Products.Tests
```

The four initial categories are Electronics, Clothing, Home, and Books. Their fixed IDs prevent migration changes between runs. Product IDs are generated as UUIDv7 in the application, creation timestamps use UTC, and prices use `numeric(18,2)` with zero allowed. A category with products cannot be deleted. The indexes support `created_at DESC, id ASC`, both across the catalog and within a category.
