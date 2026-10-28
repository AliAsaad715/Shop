namespace Shop.Modules.Products.Contracts;

/// <summary>Server-owned product names and prices for order creation, without exposing persistence types.</summary>
public interface IProductCatalog
{
    /// <summary>Returns each known product once, keyed by ID. Unknown IDs are omitted.</summary>
    Task<IReadOnlyDictionary<Guid, ProductCatalogItem>> GetByIdsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct);
}
