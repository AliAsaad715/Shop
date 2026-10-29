using Shop.Modules.Orders.Domain;

namespace Shop.Modules.Orders.StatusChanges;

internal sealed record UpdateOrderStatusRequest(OrderStatus? Status);
