namespace Shop.Api.Security;

internal sealed class FrontendCorsOptions
{
    public const string Section = "Cors";
    public const string Policy = "Frontend";

    public string[] AllowedOrigins { get; init; } = [];

    public static bool IsValidOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && !string.IsNullOrEmpty(uri.Host)
        && !uri.Host.Contains('*')
        && string.IsNullOrEmpty(uri.UserInfo)
        && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && !origin.EndsWith('/');
}
