using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Shop.SharedKernel.Errors;

namespace Shop.Modules.Products.Images;

internal static class ProductImageEndpoint
{
    public static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> HandleAsync(
        string fileName, IImageStorage storage, HttpContext context, ILogger<IImageStorage> logger, CancellationToken ct)
    {
        StoredImage? image;
        try { image = await storage.OpenReadAsync("products/" + fileName, ct); }
        catch (ImageStorageUnavailableException error)
        {
            logger.LogError(error, "Product image read failed for {ImagePath}", "products/" + fileName);
            return Problems.Create(StatusCodes.Status503ServiceUnavailable, ErrorCodes.Common.ServiceUnavailable,
                "Image storage unavailable", "Try again when product image storage is available.");
        }
        if (image is null)
        {
            return Problems.Create(StatusCodes.Status404NotFound, ErrorCodes.Common.NotFound, "Image not found");
        }

        context.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Stream(image.Content, image.ContentType);
    }
}
