namespace Shop.Modules.Products.Catalog;

internal sealed record ProductResponse(
    Guid Id, string Name, string Description, decimal Price, string ImageUrl,
    Guid CategoryId, string CategoryName, DateTimeOffset CreatedAt);
