using System.ComponentModel;

namespace Shop.Modules.Products.Catalog;

/// <summary>
/// A page of the catalog. A numbered page (<c>page</c>) carries <c>page</c> and <c>totalCount</c>; a keyset page
/// (<c>cursor</c>) leaves both null, because skipping the count is part of what keeps it cheap at any depth.
/// <c>nextCursor</c> continues right after this page, and is null when nothing follows. First pages are cached (task
/// R3-07), and immutable, so a cache hit reuses the instance.
/// </summary>
[ImmutableObject(true)]
internal sealed record ProductPageResponse(
    IReadOnlyList<ProductResponse> Items, int? Page, int PageSize, int? TotalCount, string? NextCursor);
