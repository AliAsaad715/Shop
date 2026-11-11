using System.ComponentModel;

namespace Shop.Modules.Products.Catalog;

/// <summary>
/// A product as its details are cached (task R3-07): everything but the stock, with the row versions its ETag is built
/// from (task R3-09). Immutable, so HybridCache hands out the same instance from memory instead of a fresh copy.
/// </summary>
[ImmutableObject(true)]
internal sealed record CachedProduct(ProductResponse Product, uint Version, uint CategoryVersion);
