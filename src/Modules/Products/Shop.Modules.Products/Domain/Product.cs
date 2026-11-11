namespace Shop.Modules.Products.Domain;

internal sealed class Product
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public required string Description { get; set; }

    public decimal Price { get; set; }

    public required string ImagePath { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid CategoryId { get; set; }

    public Category Category { get; set; } = null!;

    // Postgres's xmin: the transaction that last wrote the row, so every update changes it. Product details build their
    // ETag from it (task R3-09)
    public uint Version { get; private set; }
}
