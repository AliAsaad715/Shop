namespace Shop.Modules.Products.Domain;

internal sealed class Category
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }
}
