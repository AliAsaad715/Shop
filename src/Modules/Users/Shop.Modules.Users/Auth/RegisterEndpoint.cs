using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shop.Modules.Users.Data;
using Shop.Modules.Users.Domain;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;

namespace Shop.Modules.Users.Auth;

internal static class RegisterEndpoint
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/auth/register", HandleAsync)
            .WithName("Register")
            .WithTags("Auth")
            .WithSummary("Create a customer account")
            .AllowAnonymous()
            .ProducesProblem(StatusCodes.Status409Conflict);

    public static async Task<Results<Created<RegisterResponse>, ProblemHttpResult>> HandleAsync(
        RegisterRequest request, UserManager<User> users, UsersDbContext context, CancellationToken cancellationToken)
    {
        var user = new User { UserName = request.Email, Email = request.Email };

        // The account and its role land together, so there is never a user without a role
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        IdentityResult created;
        try
        {
            created = await users.CreateAsync(user, request.Password);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent registration passed Identity's check first; the unique email index caught it
            return DuplicateEmail();
        }

        if (!created.Succeeded)
        {
            return created.Errors.Any(error => error.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName))
                ? DuplicateEmail()
                : Problems.Validation(ToValidationErrors(created.Errors));
        }

        var roleAdded = await users.AddToRoleAsync(user, Roles.Customer);
        if (!roleAdded.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not add a new user to the {Roles.Customer} role: {string.Join("; ", roleAdded.Errors.Select(error => error.Description))}");
        }

        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Created("/api/v1/users/me", new RegisterResponse(user.Id, request.Email, Roles.Customer));
    }

    private static ProblemHttpResult DuplicateEmail() =>
        Problems.Create(StatusCodes.Status409Conflict, ErrorCodes.Users.DuplicateEmail,
            "Email already registered", "An account with this email already exists.");

    // Identity's password rules become field errors, like every other 422
    private static Dictionary<string, string[]> ToValidationErrors(IEnumerable<IdentityError> errors) =>
        errors
            .GroupBy(error => error.Code.StartsWith("Password", StringComparison.Ordinal) ? "password" : "email")
            .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray());
}
