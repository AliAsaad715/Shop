namespace Shop.Modules.Users.Auth;

internal sealed record LoginResponse(string AccessToken, string TokenType, int ExpiresIn);
