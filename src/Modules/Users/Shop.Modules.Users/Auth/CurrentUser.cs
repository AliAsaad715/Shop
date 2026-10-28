using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Shop.SharedKernel.Auth;

namespace Shop.Modules.Users.Auth;

internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId => IsAuthenticated && Guid.TryParse(Principal?.FindFirstValue("sub"), out var id) && id != Guid.Empty ? id : null;

    public string? Email => IsAuthenticated ? Principal?.FindFirstValue("email") : null;

    public IReadOnlyCollection<string> Roles => IsAuthenticated
        ? Principal!.FindAll("role").Select(claim => claim.Value).Distinct(StringComparer.Ordinal).ToArray()
        : [];

    public bool IsInRole(string role) => IsAuthenticated && Principal!.IsInRole(role);
}
