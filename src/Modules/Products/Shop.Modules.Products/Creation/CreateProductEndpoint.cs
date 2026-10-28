using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Domain;
using Shop.Modules.Products.Images;
using Shop.SharedKernel.Errors;

namespace Shop.Modules.Products.Creation;

internal static class CreateProductEndpoint
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/products", HandleAsync)
            .WithName("CreateProduct")
            .WithTags("Products")
            .WithSummary("Create a product with a JPEG, PNG or WebP image up to 2 MB")
            .RequireAuthorization("AdminOnly")
            .DisableAntiforgery()
            .WithMetadata(new ProductUploadMetadata())
            .WithFormOptions(multipartBodyLengthLimit: ImageUploadLimits.RequestBytes, valueCountLimit: 10)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

    public static async Task<Results<Created<CreatedProductResponse>, ProblemHttpResult>> HandleAsync(
        [FromForm] CreateProductRequest request, ProductsDbContext context, IImageStorage images,
        HttpContext httpContext, ILogger<IImageStorage> logger, CancellationToken ct)
    {
        var file = request.Image!;
        if (file.Length > ImageUploadLimits.ImageBytes)
        {
            return Problems.Create(StatusCodes.Status413PayloadTooLarge, ErrorCodes.Products.ImageTooLarge,
                "Image too large", "Product images must not exceed 2 MB.");
        }

        if (httpContext.Request.Form.Files.Count != 1)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["image"] = ["Upload exactly one image."] });
        }

        await using var input = file.OpenReadStream();
        var header = new byte[12];
        var read = await input.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        var extension = ImageSignatures.Extension(header.AsSpan(0, read));
        if (extension is null)
        {
            return Problems.Create(StatusCodes.Status415UnsupportedMediaType, ErrorCodes.Products.UnsupportedImageType,
                "Unsupported image", "Only JPEG, PNG and WebP file signatures are accepted.");
        }

        var category = await context.Categories.AsNoTracking().SingleOrDefaultAsync(category => category.Id == request.CategoryId, ct);
        if (category is null)
        {
            return UnknownCategory();
        }

        input.Position = 0;
        var path = await images.SaveAsync(input, extension, ct);
        var product = new Product
        {
            Name = request.Name.Trim(),
            Description = request.Description.Trim(),
            Price = request.Price!.Value,
            CategoryId = category.Id,
            ImagePath = path,
        };

        try
        {
            context.Products.Add(product);
            await context.SaveChangesAsync(ct);
        }
        catch (Exception error)
        {
            try
            {
                await images.DeleteAsync(path, CancellationToken.None);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(cleanupError, "Could not remove image {ImagePath} after product save failed", path);
            }

            if (error is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } })
            {
                return UnknownCategory();
            }

            throw;
        }

        var location = $"{httpContext.Request.PathBase}{httpContext.Request.Path.ToString().TrimEnd('/')}/{product.Id}";
        return TypedResults.Created(location, new CreatedProductResponse(
            product.Id, product.Name, product.Description, product.Price, "/images/" + path,
            category.Id, category.Name, product.CreatedAt));
    }

    private static ProblemHttpResult UnknownCategory() =>
        Problems.Validation(new Dictionary<string, string[]> { ["categoryId"] = ["The selected category does not exist."] });
}
