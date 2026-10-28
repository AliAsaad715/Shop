# Products integration tests

Run from the repository root with Docker available:

```sh
dotnet test tests/Shop.Modules.Products.IntegrationTests
```

The test starts an isolated PostgreSQL 18 container with Testcontainers. It applies the actual Products migration, checks seeded categories, filtered pagination with tied timestamps, decimal price persistence, foreign-key and nonnegative-price constraints, and rollback followed by reapplication.

Alternatively, set `SHOP_PRODUCTS_TEST_CONNECTION` to an existing **disposable** PostgreSQL database. This skips Docker. The test rolls back the Products migration and deletes its product data, so never use a development, shared, or production database.

```powershell
$env:SHOP_PRODUCTS_TEST_CONNECTION = 'Host=127.0.0.1;Port=55432;Database=shop_products_tests;Username=postgres'
dotnet test tests/Shop.Modules.Products.IntegrationTests
```

The four initial categories are Electronics, Clothing, Home, and Books. Their fixed IDs prevent migration changes between runs. Product IDs are generated as UUIDv7 in the application, creation timestamps use UTC, and prices use `numeric(18,2)` with zero allowed. A category with products cannot be deleted. The indexes support `created_at DESC, id ASC`, both across the catalog and within a category.

## Catalog endpoints and order contract

The catalog tests use ASP.NET TestHost and the real Products module registration against PostgreSQL. A fallback policy requires authentication, proving that the three catalog routes explicitly allow anonymous reads. The fixture replaces existing product data with four products, including two with identical timestamps. The schema and catalog test classes share a test collection so the schema rollback cannot race catalog reads when using the same disposable database.

Coverage includes category filtering, default paging (`page=1`, `pageSize=20`), the maximum page size of 50, stable tie-breaking across pages, empty results, image URLs, 404 Problem Details, invalid query values (422), malformed parameters (400), and the batched `IProductCatalog` contract used by Orders.

Product responses expose app-relative image URLs under `/images/`, escaping each stored path segment. Task 15 supplies image storage and serving at that route. Orders receives only product IDs, names and current prices from `IProductCatalog.GetByIdsAsync`; duplicate IDs are coalesced and unknown IDs are omitted so order creation can reject them.
