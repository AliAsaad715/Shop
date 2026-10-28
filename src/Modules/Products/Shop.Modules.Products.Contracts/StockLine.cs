namespace Shop.Modules.Products.Contracts;

/// <summary>A product and quantity to reserve, without exposing the Products data model.</summary>
public sealed record StockLine(Guid ProductId, int Quantity);
