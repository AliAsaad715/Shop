using System.ComponentModel;

namespace Shop.Modules.Products.Catalog;

internal sealed record CategoryResponse(Guid Id, string Name);

/// <summary>The categories as they're cached (task R3-07); immutable, so a cache hit reuses the instance.</summary>
[ImmutableObject(true)]
internal sealed record CategoryList(IReadOnlyList<CategoryResponse> Items);
