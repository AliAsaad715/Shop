namespace Shop.Modules.Products.Contracts;

/// <summary>Whether every requested line was reserved; false means no lines were reserved.</summary>
public sealed record ReserveResult(bool Success);
