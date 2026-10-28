namespace Shop.Api.Security;

internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string ApiPolicy = "default-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
    // Scalar serves its JavaScript locally and requires inline initialization and runtime styles.
    private const string DocsPolicy = "default-src 'self'; script-src 'self' 'unsafe-inline'; "
        + "style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; "
        + "connect-src 'self'; worker-src 'self' blob:; base-uri 'self'; frame-ancestors 'none'; form-action 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        // HSTS runs first. Preserve its value when the exception handler clears a failed response.
        var hsts = context.Response.Headers.StrictTransportSecurity;
        // Register before the exception handler so clearing an error response cannot remove these headers.
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers.Remove("Server");
            if (hsts.Count > 0)
            {
                headers.StrictTransportSecurity = hsts;
            }

            if (context.Request.Path.StartsWithSegments("/api"))
            {
                headers.ContentSecurityPolicy = ApiPolicy;
            }
            else if (context.Request.Path.StartsWithSegments("/docs"))
            {
                headers.ContentSecurityPolicy = DocsPolicy;
            }

            if (context.User.Identities.Any(identity => identity.IsAuthenticated))
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        });

        return next(context);
    }
}
