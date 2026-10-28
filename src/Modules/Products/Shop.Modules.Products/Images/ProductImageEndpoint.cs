using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Shop.SharedKernel.Errors;

namespace Shop.Modules.Products.Images;

internal static class ProductImageEndpoint
{
    public static async Task<Results<FileStreamHttpResult, ProblemHttpResult>> HandleAsync(
        string fileName, IImageStorage storage, HttpContext context, CancellationToken ct)
    {
        var image = await storage.OpenReadAsync("products/" + fileName, ct);
        if (image is null)
        {
            return Problems.Create(StatusCodes.Status404NotFound, ErrorCodes.Common.NotFound, "Image not found");
        }

        context.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Stream(image.Content, image.ContentType);
    }
}
