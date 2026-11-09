using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Shop.SharedKernel.Diagnostics;

public static class DbQueryCountHeaderExtensions
{
    public const string HeaderName = "X-Db-Queries";

    /// <summary>
    /// Adds X-Db-Queries to every response: how many SQL commands the request sent through EF Core (task R3-04).
    /// For development only; the commands are counted only where <c>AddModuleDbContext</c> runs in Development.
    /// </summary>
    public static IApplicationBuilder UseDbQueryCountHeader(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var count = new DbQueryCount();
            context.Features.Set(count);

            // Set as the headers go out, so every command before that counts, an error's included
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[HeaderName] = count.Value.ToString(CultureInfo.InvariantCulture);
                return Task.CompletedTask;
            });

            await next(context);
        });
}
