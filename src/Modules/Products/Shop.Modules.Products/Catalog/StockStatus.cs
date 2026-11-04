using System.Text.Json.Serialization;

namespace Shop.Modules.Products.Catalog;

/// <summary>Whether a product can be ordered now, as the warehouse answered for this request.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StockStatus>))]
internal enum StockStatus
{
    [JsonStringEnumMemberName("in_stock")]
    InStock,

    [JsonStringEnumMemberName("out_of_stock")]
    OutOfStock,

    // The warehouse is down or missed its deadline; the product still shows, without a count
    [JsonStringEnumMemberName("unknown")]
    Unknown,
}
