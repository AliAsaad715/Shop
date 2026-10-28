using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Shop.Api.Docs;

// Declares the JWT bearer scheme, so the docs UI shows an Authorize button that sends the token
internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public const string SchemeName = "Bearer";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "The access token from POST /api/v1/auth/login",
        };

        // Deny by default (task 11), so every operation needs the token; anonymous endpoints still work without one
        document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(SchemeName, document)] = [] }];

        return Task.CompletedTask;
    }
}
