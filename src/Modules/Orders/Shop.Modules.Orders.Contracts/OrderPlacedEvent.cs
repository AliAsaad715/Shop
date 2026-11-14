namespace Shop.Modules.Orders.Contracts;

/// <summary>
/// Published as <c>order.placed</c> once an order is saved. It carries everything the confirmation email needs, so
/// the email worker never reads the Orders or Users tables. JSON with camelCase names, like the API.
/// </summary>
/// <param name="CustomerEmail">From the customer's token; null when the token had none, which the email worker treats
/// as an email it can never send.</param>
/// <param name="Status">Always <c>Pending</c>: the status a new order starts in.</param>
/// <param name="Version">The order's version, 0 for a new order; live pages ignore events older than what they show.</param>
public sealed record OrderPlacedEvent(
    Guid OrderId,
    Guid CustomerId,
    string? CustomerEmail,
    string Status,
    decimal Total,
    IReadOnlyList<OrderPlacedLine> Lines,
    int Version,
    DateTimeOffset PlacedAt);

/// <summary>One line of a placed order, with the name and price copied when it was placed.</summary>
public sealed record OrderPlacedLine(Guid ProductId, string ProductName, decimal UnitPrice, int Quantity, decimal LineTotal);
