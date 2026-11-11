using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Shop.Modules.Users.Data;
using Shop.SharedKernel.Auth;
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
        ICurrentUser currentUser, UsersDbContext context, HttpResponse response, CancellationToken ct)
    {
        response.Headers.CacheControl = "no-store";

        // The profile and its roles in one round trip, projected straight into the response: only the columns it
        // shows leave the database, never the password hash or security stamp that loading the whole user would read
        var profile = currentUser.UserId is { } id
            ? await context.Users
                .Where(user => user.Id == id)
                .Select(user => new CurrentUserResponse(user.Id, user.Email!,
                    context.UserRoles.Where(link => link.UserId == user.Id)
                        .Join(context.Roles, link => link.RoleId, role => role.Id, (_, role) => role.Name!)
                        .OrderBy(name => name)
                        .ToList()))
                .SingleOrDefaultAsync(ct)
            : null;

        return profile is null
            ? Problems.Create(StatusCodes.Status401Unauthorized, ErrorCodes.Common.Unauthorized, "Unauthorized")
            : TypedResults.Ok(profile);
    }
}
