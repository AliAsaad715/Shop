namespace Shop.Modules.Users.Auth;

internal sealed record CurrentUserResponse(Guid Id, string Email, IList<string> Roles);
