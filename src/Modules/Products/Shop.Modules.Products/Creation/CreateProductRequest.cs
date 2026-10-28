using Microsoft.AspNetCore.Http;

namespace Shop.Modules.Products.Creation;

internal sealed class CreateProductRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal? Price { get; set; }
    public Guid? CategoryId { get; set; }
    public IFormFile? Image { get; set; }
}
