namespace Shop.Modules.Products.Catalog;

internal sealed class CatalogCacheOptions
{
    public const string Section = "CatalogCache";
    public bool Enabled { get; set; }
    public TimeSpan ProductTtl { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan CategoriesTtl { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan ListTtl { get; set; } = TimeSpan.FromSeconds(60);
    public TimeSpan LocalTtl { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxCachedPage { get; set; } = 3;
}
