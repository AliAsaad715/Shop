using System.Text.RegularExpressions;

namespace Shop.Api.Instances;

/// <summary>
/// Which API instance this is (task R5-03). Compose runs three, api1 to api3, and sets INSTANCE_NAME on each. It goes
/// into the X-Instance header of every response, so a load test through Nginx can count which instance answered, and
/// into the name of the instance's RabbitMQ connection. Unset, as in a local dotnet run or the tests, nothing changes.
/// Log lines need nothing more: Compose already starts each one with the container's name, such as api2-1.
/// </summary>
public static partial class InstanceName
{
    public const string Setting = "INSTANCE_NAME";
    public const string Header = "X-Instance";

    /// <summary>The configured name, or null; a name a header couldn't carry stops startup instead.</summary>
    public static string? From(IConfiguration configuration)
    {
        var name = configuration[Setting];
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return Allowed().IsMatch(name)
            ? name
            : throw new InvalidOperationException(
                $"{Setting} may hold only letters, digits, dots, dashes and underscores, at most 64 of them.");
    }

    /// <summary>
    /// Adds the header just before the response's headers are sent, so an error response that replaced them (the
    /// exception handler clears the response) still says which instance answered.
    /// </summary>
    public static IApplicationBuilder UseInstanceHeader(this IApplicationBuilder app, string? name)
    {
        if (name is null)
        {
            return app;
        }

        return app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[Header] = name;
                return Task.CompletedTask;
            });
            return next(context);
        });
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex Allowed();
}
