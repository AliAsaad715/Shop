namespace Shop.Modules.Users.Auth;

internal sealed class LoginRateLimitOptions
{
    public const string Section = "LoginRateLimit";

    public int PermitLimit { get; init; } = 5;
    public int WindowSeconds { get; init; } = 60;
}
