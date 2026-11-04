namespace Shop.Modules.Products.Seeding;

// The demo catalog. The warehouse stocks exactly these IDs in warehouse/db/002_seed.sql, with a
// single unit of product X; DemoSeedTests fails when the two lists drift apart.
internal static class DemoProducts
{
    // The pattern of the seeded categories (…0001 to …0004) and roles (…0101 and …0102)
    public static readonly Guid ProductX = Guid.Parse("019a2180-0000-7000-8000-000000000205");

    // Seeded by CategoryConfiguration
    private static readonly Guid Electronics = Guid.Parse("019a2180-0000-7000-8000-000000000001");
    private static readonly Guid Clothing = Guid.Parse("019a2180-0000-7000-8000-000000000002");
    private static readonly Guid Home = Guid.Parse("019a2180-0000-7000-8000-000000000003");
    private static readonly Guid Books = Guid.Parse("019a2180-0000-7000-8000-000000000004");

    public static IReadOnlyList<DemoProduct> All { get; } =
    [
        new(Guid.Parse("019a2180-0000-7000-8000-000000000201"), "Wireless Headphones",
            "Over-ear Bluetooth headphones with 30 hours of battery. Demo product.", 89.99m, Electronics),
        new(Guid.Parse("019a2180-0000-7000-8000-000000000202"), "Cotton T-Shirt",
            "Plain crew-neck T-shirt in organic cotton. Demo product.", 19.99m, Clothing),
        new(Guid.Parse("019a2180-0000-7000-8000-000000000203"), "Ceramic Mug",
            "A 350 ml stoneware mug, dishwasher safe. Demo product.", 12.50m, Home),
        new(Guid.Parse("019a2180-0000-7000-8000-000000000204"), "Field Guide to Distributed Systems",
            "A paperback on consensus, retries and idempotency. Demo product.", 34.00m, Books),
        new(ProductX, "Limited Edition Desk Lamp",
            "The warehouse holds one unit: the product the 20-order race demo competes for.", 59.00m, Home),
        new(Guid.Parse("019a2180-0000-7000-8000-000000000206"), "Trail Running Shoes",
            "Sold out in the warehouse, for the not-enough-stock demo.", 120.00m, Clothing),
    ];
}

internal sealed record DemoProduct(Guid Id, string Name, string Description, decimal Price, Guid CategoryId);
