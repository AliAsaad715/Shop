using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Users.Auth;
using Shop.Modules.Users.Data;
using Shop.Modules.Users.Domain;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;
using Testcontainers.PostgreSql;
using Xunit;

namespace Shop.Modules.Users.IntegrationTests;

public sealed class RegisterTests : IAsyncLifetime
{
    private const string Password = "Customer123!";

    private PostgreSqlContainer? _postgres;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        // An explicit connection must point to a disposable database: these tests create users.
        var connectionString = Environment.GetEnvironmentVariable("SHOP_USERS_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _postgres = new PostgreSqlBuilder("postgres:18").Build();
            await _postgres.StartAsync();
            connectionString = _postgres.GetConnectionString();
        }

        _services = UsersTestServices.Build(connectionString);
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<UsersDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [Fact]
    public async Task NewAccountIsACustomerEvenWhenTheBodyAsksForAdmin()
    {
        var email = UniqueEmail();

        // The body as a client would send it; "role" has no property to bind to
        var request = JsonSerializer.Deserialize<RegisterRequest>(
            $$"""{"email":"{{email}}","password":"{{Password}}","role":"Admin"}""", JsonSerializerOptions.Web)!;

        var created = Assert.IsType<Created<RegisterResponse>>((await RegisterAsync(request)).Result);
        Assert.Equal("/api/v1/users/me", created.Location);
        Assert.Equal(Roles.Customer, created.Value!.Role);

        await using var scope = _services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await users.FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.Equal(created.Value.Id, user.Id);
        Assert.Equal([Roles.Customer], await users.GetRolesAsync(user));
        Assert.True(await users.CheckPasswordAsync(user, Password));
    }

    [Fact]
    public async Task DuplicateEmailReturns409WhateverItsCase()
    {
        var email = UniqueEmail();
        Assert.IsType<Created<RegisterResponse>>((await RegisterAsync(new RegisterRequest(email, Password))).Result);

        var duplicate = Assert.IsType<ProblemHttpResult>((await RegisterAsync(new RegisterRequest(email.ToUpperInvariant(), Password))).Result);

        Assert.Equal(StatusCodes.Status409Conflict, duplicate.StatusCode);
        Assert.Equal(ErrorCodes.Users.DuplicateEmail, duplicate.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task WeakPasswordReturns422AndCreatesNothing()
    {
        var email = UniqueEmail();

        var rejected = Assert.IsType<ProblemHttpResult>((await RegisterAsync(new RegisterRequest(email, "weak"))).Result);

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, rejected.StatusCode);
        Assert.Equal(ErrorCodes.Common.ValidationFailed, rejected.ProblemDetails.Extensions["code"]);
        Assert.Contains("password", Assert.IsType<HttpValidationProblemDetails>(rejected.ProblemDetails).Errors.Keys);

        await using var scope = _services.CreateAsyncScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByEmailAsync(email));
    }

    // Each call gets its own scope, as each HTTP request would
    private async Task<Results<Created<RegisterResponse>, ProblemHttpResult>> RegisterAsync(RegisterRequest request)
    {
        await using var scope = _services.CreateAsyncScope();
        return await RegisterEndpoint.HandleAsync(
            request,
            scope.ServiceProvider.GetRequiredService<UserManager<User>>(),
            scope.ServiceProvider.GetRequiredService<UsersDbContext>(),
            CancellationToken.None);
    }

    private static string UniqueEmail() => $"customer-{Guid.NewGuid():N}@shop.test";
}
