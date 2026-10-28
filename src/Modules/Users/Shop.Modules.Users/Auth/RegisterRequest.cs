namespace Shop.Modules.Users.Auth;

// No role property: a "role" in the body has nowhere to bind, so every account starts as a Customer
internal sealed record RegisterRequest(string Email, string Password);
