using Microsoft.AspNetCore.Http;
using Shop.SharedKernel.Errors;

namespace Shop.Modules.Products.Images;

internal sealed class ProductUploadLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ProductUploadMetadata>() is null)
        {
            await next(context);
            return;
        }

        var original = context.Request.Body;
        try
        {
            if (context.Request.ContentLength > ImageUploadLimits.RequestBytes)
            {
                throw new ImageUploadTooLargeException();
            }

            context.Request.Body = new UploadReadStream(original);
            await next(context);
        }
        catch (ImageUploadTooLargeException)
        {
            await Problems.Create(StatusCodes.Status413PayloadTooLarge, ErrorCodes.Products.ImageTooLarge,
                "Image too large", "Product images must not exceed 2 MB.").ExecuteAsync(context);
        }
        finally
        {
            context.Request.Body = original;
        }
    }
}
