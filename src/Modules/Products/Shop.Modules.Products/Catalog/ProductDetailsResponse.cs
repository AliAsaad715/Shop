namespace Shop.Modules.Products.Catalog;

/// <summary>
/// One product with its stock, as <c>GET /products/{id}</c> returns it. <see cref="Available"/> is null
/// when <see cref="StockStatus"/> is unknown. Lists leave stock out, so a page never asks the warehouse once per product.
/// </summary>
internal sealed record ProductDetailsResponse(
    Guid Id, string Name, string Description, decimal Price, string ImageUrl,
    Guid CategoryId, string CategoryName, DateTimeOffset CreatedAt,
    StockStatus StockStatus, int? Available)
{
    public static ProductDetailsResponse From(ProductResponse product, StockStatus stockStatus, int? available) => new(
        product.Id, product.Name, product.Description, product.Price, product.ImageUrl,
        product.CategoryId, product.CategoryName, product.CreatedAt, stockStatus, available);
}
