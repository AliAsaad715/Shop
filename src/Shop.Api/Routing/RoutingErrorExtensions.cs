namespace Shop.Api.Routing;

public static class RoutingErrorExtensions
{
    /// <summary>Completes routing errors after UseRouting and before authentication/authorization.</summary>
    public static IApplicationBuilder UseRoutingErrors(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var endpoint = context.GetEndpoint();
            if (endpoint is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // Routing generates bare rejection endpoints (e.g. 405 with Allow).
            // Mapped routes and endpoints with metadata continue through authorization.
            if (endpoint is not RouteEndpoint && endpoint.Metadata.Count == 0 && endpoint.RequestDelegate is { } rejection)
            {
                await rejection(context);
                return;
            }

            await next(context);
        });
}
