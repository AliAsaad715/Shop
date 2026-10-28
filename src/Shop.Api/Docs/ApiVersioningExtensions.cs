using Asp.Versioning;
using Scalar.AspNetCore;
using Shop.SharedKernel.Validation;

namespace Shop.Api.Docs;

public static class ApiVersioningExtensions
{
    /// <summary>
    /// Version in the URL (<c>/api/v1/...</c>) and one OpenAPI document per version.
    /// </summary>
    public static IServiceCollection AddVersionedApiDocs(this IServiceCollection services)
    {
        services.AddApiVersioning(options =>
            {
                options.ApiVersionReader = new UrlSegmentApiVersionReader();
                options.ReportApiVersions = true;
            })
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'VVV";
                options.SubstituteApiVersionInUrl = true;
            })
            .AddOpenApi(options =>
            {
                options.Document.AddDocumentTransformer((document, _, _) =>
                {
                    document.Info.Title = "Shop API";
                    return Task.CompletedTask;
                });
                options.Document.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
            });

        return services;
    }

    /// <summary>
    /// The group every module maps its endpoints into: <c>/api/v{version}</c>, request validation,
    /// and the errors any endpoint can return. Each endpoint still declares its own success and
    /// specific error responses (401, 403, 404, 409, ...) with <c>ProducesProblem</c>.
    /// </summary>
    public static RouteGroupBuilder MapVersionedApi(this WebApplication app)
    {
        var api = app.NewVersionedApi("Shop")
            .MapGroup("/api/v{version:apiVersion}")
            .HasApiVersion(1)
            .WithRequestValidation();

        api.ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return api;
    }

    /// <summary>
    /// The OpenAPI documents at <c>/openapi/v{n}.json</c> and the docs UI at <c>/docs</c>, both
    /// anonymous so they stay reachable once authorization denies by default.
    /// </summary>
    public static WebApplication MapApiDocs(this WebApplication app)
    {
        app.MapOpenApi().WithDocumentPerVersion().AllowAnonymous();

        app.MapScalarApiReference("/docs", options =>
            {
                options.WithTitle("Shop API");
                foreach (var description in app.DescribeApiVersions())
                {
                    options.AddDocument(description.GroupName, description.GroupName);
                }

                options.AddPreferredSecuritySchemes(BearerSecuritySchemeTransformer.SchemeName);
            })
            .AllowAnonymous();

        return app;
    }
}
