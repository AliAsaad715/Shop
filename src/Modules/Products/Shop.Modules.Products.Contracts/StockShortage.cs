namespace Shop.Modules.Products.Contracts;

/// <summary>A product the warehouse couldn't reserve: the units asked for and the units it has.</summary>
public sealed record StockShortage(Guid ProductId, int Requested, int Available);
