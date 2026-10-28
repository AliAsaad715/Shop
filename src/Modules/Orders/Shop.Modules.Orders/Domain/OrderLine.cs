namespace Shop.Modules.Orders.Domain;

// Name and price are copied at order time, so later catalog changes never rewrite a placed order
internal sealed class OrderLine
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid OrderId { get; set; }

    public Guid ProductId { get; set; }

    public required string ProductName { get; set; }

    public decimal UnitPrice { get; set; }

    public int Quantity { get; set; }

    public decimal LineTotal { get; set; }
}
