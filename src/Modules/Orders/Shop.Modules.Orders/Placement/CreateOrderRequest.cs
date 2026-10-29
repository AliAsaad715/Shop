namespace Shop.Modules.Orders.Placement;

// Only products and quantities: the customer comes from the token and every price from the catalog
internal sealed record CreateOrderRequest(IReadOnlyList<CreateOrderLineRequest>? Lines);

internal sealed record CreateOrderLineRequest(Guid? ProductId, int? Quantity);
