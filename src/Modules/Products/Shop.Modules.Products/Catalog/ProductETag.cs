using System.Globalization;
using Microsoft.Net.Http.Headers;

namespace Shop.Modules.Products.Catalog;

/// <summary>
/// The ETag of a product's details (task R3-09), <c>"v{product}-c{category}-s{stock}"</c>. It changes whenever anything
/// in the body can: the product's and its category's row versions (Postgres's xmin) cover what the database holds, and
/// the stock covers what the warehouse said, which every order changes. <c>s?</c> is stock the warehouse couldn't give.
/// </summary>
internal static class ProductETag
{
    public static EntityTagHeaderValue For(uint productVersion, uint categoryVersion, int? available) =>
        new(string.Create(CultureInfo.InvariantCulture,
            $"\"v{productVersion}-c{categoryVersion}-s{(available is { } count ? count.ToString(CultureInfo.InvariantCulture) : "?")}\""));

    /// <summary>
    /// Whether the client's copy is still current. If-None-Match compares weakly (RFC 9110, section 13.1.2), so
    /// <c>W/"x"</c> matches <c>"x"</c>, as a proxy that compressed the response sends it back, and <c>*</c> matches any
    /// product that exists.
    /// </summary>
    public static bool Matches(IList<EntityTagHeaderValue> ifNoneMatch, EntityTagHeaderValue current) =>
        ifNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(current, useStrongComparison: false));
}
