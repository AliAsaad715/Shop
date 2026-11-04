using Shop.Modules.Products.Contracts;

namespace Shop.Modules.Orders.Placement;

/// <summary>One product the warehouse couldn't reserve, as the 409 response lists it under <c>shortages</c>.</summary>
internal sealed record ShortageResponse(Guid ProductId, int Requested, int Available)
{
    public static ShortageResponse From(StockShortage shortage) =>
        new(shortage.ProductId, shortage.Requested, shortage.Available);
}
