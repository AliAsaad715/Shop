namespace Shop.Modules.Products.Catalog;

// Keep the ETag inputs in the same cache snapshot as the body they describe. Stock is added per request.
internal sealed record VersionedProduct(ProductResponse Product, uint Version, uint CategoryVersion);
