namespace Shop.Modules.Products.Creation;

internal sealed record CreatedProductResponse(
    Guid Id, string Name, string Description, decimal Price, string ImageUrl,
    Guid CategoryId, string CategoryName, DateTimeOffset CreatedAt);
