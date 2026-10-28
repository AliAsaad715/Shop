using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Shop.Modules.Users.Domain;
using Shop.SharedKernel.Errors;

namespace Shop.Modules.Users.Auth;

internal static class LoginEndpoint
{
    private static readonly User DummyUser = new();
    private static readonly PasswordHasher<User> DummyHasher = new();
    private static readonly string DummyHash = DummyHasher.HashPassword(DummyUser, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/auth/login", HandleAsync)
            .WithName("Login")
            .WithTags("Auth")
            .WithSummary("Sign in with email and password to receive a JWT")
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimitPolicy.Name)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

    public static async Task<Results<Ok<LoginResponse>, ProblemHttpResult>> HandleAsync(
        LoginRequest request, UserManager<User> users, SignInManager<User> signIn,
        JwtTokenIssuer tokens, HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";

        // Login never re-applies registration's strength rules or reveals field/account-specific failures.
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > RegisterRequestValidator.MaxEmailLength
            || string.IsNullOrEmpty(request.Password))
        {
            return InvalidCredentials();
        }

        var user = await users.FindByEmailAsync(request.Email);
        if (user is null)
        {
            // Still perform a password hash check for an unknown email.
            DummyHasher.VerifyHashedPassword(DummyUser, DummyHash, request.Password);
            return InvalidCredentials();
        }

        // Identity persists failures per account, independently of the caller's IP, without issuing cookies.
        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded || user.TwoFactorEnabled)
        {
            return InvalidCredentials();
        }

        return TypedResults.Ok(tokens.Issue(user, await users.GetRolesAsync(user)));
    }

    private static ProblemHttpResult InvalidCredentials() =>
        Problems.Create(StatusCodes.Status401Unauthorized, ErrorCodes.Users.InvalidCredentials, "Invalid credentials");
}
