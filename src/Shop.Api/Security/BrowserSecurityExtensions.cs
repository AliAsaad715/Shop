using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace Shop.Api.Security;

public static class BrowserSecurityExtensions
{
    public static IServiceCollection AddBrowserSecurity(this IServiceCollection services)
    {
        services.AddOptions<FrontendCorsOptions>()
            .BindConfiguration(FrontendCorsOptions.Section)
            .Validate(options => options.AllowedOrigins.All(FrontendCorsOptions.IsValidOrigin),
                "Cors:AllowedOrigins must contain explicit HTTP(S) origins without paths, trailing slashes or wildcards.")
            .ValidateOnStart();

        services.AddCors();
        services.AddOptions<CorsOptions>().Configure<IOptions<FrontendCorsOptions>>((cors, frontend) =>
            cors.AddPolicy(FrontendCorsOptions.Policy, policy => policy
                .WithOrigins(frontend.Value.AllowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .WithExposedHeaders("Location", "Retry-After")));

        services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(180));
        services.AddHttpsRedirection(options => options.RedirectStatusCode = StatusCodes.Status307TemporaryRedirect);
        return services;
    }

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.UseMiddleware<SecurityHeadersMiddleware>();

    public static IApplicationBuilder UseFrontendCors(this IApplicationBuilder app) =>
        app.UseCors(FrontendCorsOptions.Policy);
}
