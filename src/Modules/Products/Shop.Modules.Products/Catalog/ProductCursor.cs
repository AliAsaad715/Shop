using System.Buffers.Binary;
using System.Buffers.Text;

namespace Shop.Modules.Products.Catalog;

/// <summary>
/// Where a page of products ends: the last product's creation time and ID. The next page starts right after it
/// (keyset pagination, task R3-05), found through the index at any depth. Opaque to clients: base64url of the time's
/// ticks and the ID's bytes, handed out as <c>nextCursor</c> and only ever passed back.
/// </summary>
internal readonly record struct ProductCursor(DateTimeOffset CreatedAt, Guid Id)
{
    private const int Length = 24;

    public static ProductCursor After(ProductResponse product) => new(product.CreatedAt, product.Id);

    public string Encode()
    {
        Span<byte> bytes = stackalloc byte[Length];
        BinaryPrimitives.WriteInt64BigEndian(bytes, CreatedAt.UtcTicks);
        Id.TryWriteBytes(bytes[8..]);
        return Base64Url.EncodeToString(bytes);
    }

    public static bool TryDecode(string? text, out ProductCursor cursor)
    {
        cursor = default;
        Span<byte> bytes = stackalloc byte[Length];
        if (string.IsNullOrEmpty(text) || text.Length > 64
            || !Base64Url.TryDecodeFromChars(text, bytes, out var written) || written != Length)
        {
            return false;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(bytes);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        cursor = new ProductCursor(new DateTimeOffset(ticks, TimeSpan.Zero), new Guid(bytes[8..]));
        return true;
    }
}
