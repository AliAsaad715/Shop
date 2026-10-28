using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Shop.Modules.Users.Domain;
using Shop.SharedKernel.Errors;

namespace Shop.Modules.Users.Auth;

internal static class CurrentUserEndpoint
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/users/me", HandleAsync)
            .WithName("GetCurrentUser")
            .WithTags("Users")
            .WithSummary("Get the authenticated user's profile and roles")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

    public static async Task<Results<Ok<CurrentUserResponse>, ProblemHttpResult>> HandleAsync(
        ClaimsPrincipal principal, UserManager<User> users, HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(principal.FindFirstValue("sub"), out var id)
            || await users.FindByIdAsync(id.ToString()) is not { } user)
        {
            return Problems.Create(StatusCodes.Status401Unauthorized, ErrorCodes.Common.Unauthorized, "Unauthorized");
        }

        return TypedResults.Ok(new CurrentUserResponse(user.Id, user.Email!, await users.GetRolesAsync(user)));
    }
}
