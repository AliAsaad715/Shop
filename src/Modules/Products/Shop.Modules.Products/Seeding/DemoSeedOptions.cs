namespace Shop.Modules.Products.Seeding;

// Bound from the DemoSeed section, e.g. DemoSeed__Enabled=true, which compose sets for local demos.
// Off by default, so production and the tests start with an empty catalog.
internal sealed class DemoSeedOptions
{
    public const string Section = "DemoSeed";

    public bool Enabled { get; init; }
}
