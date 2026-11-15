namespace Shop.Modules.Orders.Contracts;

/// <summary>
/// Published as <c>order.status-changed</c> each time an order moves to another status, for the live pages.
/// </summary>
/// <param name="Status">The status the order is in now, such as <c>Confirmed</c>.</param>
/// <param name="Version">The order's version after the change. Events can arrive out of order, so a page ignores one
/// whose version is older than what it shows.</param>
/// <param name="ChangedBy">The admin who changed it.</param>
public sealed record OrderStatusChangedEvent(
    Guid OrderId,
    Guid CustomerId,
    string FromStatus,
    string Status,
    int Version,
    Guid ChangedBy,
    DateTimeOffset ChangedAt);
