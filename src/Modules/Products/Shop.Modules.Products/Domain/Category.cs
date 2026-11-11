namespace Shop.Modules.Products.Domain;

internal sealed class Category
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    // Postgres's xmin, as on Product: product details show the category's name, so their ETag includes it too
    public uint Version { get; private set; }
}
