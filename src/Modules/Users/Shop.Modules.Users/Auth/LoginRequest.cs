namespace Shop.Modules.Users.Auth;

internal sealed record LoginRequest(string? Email, string? Password);
