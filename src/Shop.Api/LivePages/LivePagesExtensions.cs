using Microsoft.Extensions.FileProviders;

namespace Shop.Api.LivePages;

/// <summary>
/// The two live pages (task R4-08), from wwwroot/app at /app: the customer's orders and the admin dashboard. They
/// hold no data, so they load without a token and sign in on the page; every API call and the SignalR connection then
/// carry the token. SecurityHeadersMiddleware gives everything under /app its own CSP.
/// </summary>
public static class LivePagesExtensions
{
    public const string Path = "/app";
    public const string CustomerPage = "/app/orders.html";

    // Before routing: a static file is served without an endpoint, so the authenticated fallback policy doesn't apply
    public static WebApplication UseLivePages(this WebApplication app)
    {
        var folder = System.IO.Path.Combine(app.Environment.WebRootPath ?? System.IO.Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "app");
        if (Directory.Exists(folder))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                RequestPath = Path,
                FileProvider = new PhysicalFileProvider(folder),
                // The browser keeps its copy but checks it every time (a 304 when unchanged), so a new version of a
                // page and its scripts takes effect at once instead of after the browser's own guess at freshness
                OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
            });
        }

        return app;
    }

    // /app and /app/ (one route: routing ignores a trailing slash) open the customer's page; the dashboard is
    // /app/admin.html
    public static IEndpointRouteBuilder MapLivePages(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Path, () => Results.Redirect(CustomerPage)).AllowAnonymous().ExcludeFromDescription();
        return endpoints;
    }
}
