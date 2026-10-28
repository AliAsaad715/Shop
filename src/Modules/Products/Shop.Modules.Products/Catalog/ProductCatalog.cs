using Microsoft.EntityFrameworkCore;
using Shop.Modules.Products.Contracts;
using Shop.Modules.Products.Data;

namespace Shop.Modules.Products.Catalog;

internal sealed class ProductCatalog(ProductsDbContext context) : IProductCatalog
{
    public async Task<IReadOnlyDictionary<Guid, ProductCatalogItem>> GetByIdsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(productIds);
        ct.ThrowIfCancellationRequested();

        if (productIds.Count == 0)
        {
            return new Dictionary<Guid, ProductCatalogItem>();
        }

        var ids = productIds.Distinct().ToArray();
        return await context.Products.AsNoTracking()
            .Where(product => ids.Contains(product.Id))
            .Select(product => new ProductCatalogItem(product.Id, product.Name, product.Price))
            .ToDictionaryAsync(product => product.Id, ct);
    }
}
