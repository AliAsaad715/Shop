namespace Shop.Modules.Users.Auth;

internal sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "shop";
    public string Audience { get; set; } = "shop-api";
    public string SigningKey { get; set; } = string.Empty;
    public int LifetimeMinutes { get; set; } = 15;

    public static bool HasValidSigningKey(JwtOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(options.SigningKey).Length >= 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
