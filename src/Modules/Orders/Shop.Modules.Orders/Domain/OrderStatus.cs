using System.Text.Json.Serialization;

namespace Shop.Modules.Orders.Domain;

// Written as names in JSON and in the database, so reordering the members never changes stored data
[JsonConverter(typeof(JsonStringEnumConverter<OrderStatus>))]
internal enum OrderStatus
{
    Pending,
    Confirmed,
    Shipped,
    Delivered,
    Cancelled,
}
