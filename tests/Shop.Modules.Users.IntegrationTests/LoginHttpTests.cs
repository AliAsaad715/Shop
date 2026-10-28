using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shop.Modules.Users.Auth;
using Shop.Modules.Users.Domain;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;
using Xunit;

namespace Shop.Modules.Users.IntegrationTests;

[Collection("Users HTTP")]
public sealed class LoginHttpTests(AuthApi api) : IClassFixture<AuthApi>
{
    private const string Password = "Customer123!";

    [Theory]
    [InlineData(Roles.Customer)]
    [InlineData(Roles.Admin)]
    public async Task LoginAndMeReturnTheAuthenticatedAccountAndStoredRoles(string role)
    {
        var account = await RegisterAsync();
        if (role == Roles.Admin)
        {
            await using var scope = api.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await users.FindByIdAsync(account.Id.ToString());
            Assert.NotNull(user);
            Assert.True((await users.RemoveFromRoleAsync(user, Roles.Customer)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, Roles.Admin)).Succeeded);
        }

        using var login = await api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = account.Email.ToUpperInvariant(), password = Password, role = "Ignored" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(login.Headers.CacheControl?.NoStore);
        Assert.Empty(login.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : []);
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.Equal("Bearer", token.TokenType);
        Assert.Equal(900, token.ExpiresIn);
        Assert.Equal([role], new JsonWebToken(token.AccessToken).GetPayloadValue<string[]>("role"));

        using var me = await GetMeAsync(token.AccessToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.True(me.Headers.CacheControl?.NoStore);
        var profile = (await me.Content.ReadFromJsonAsync<CurrentUserResponse>())!;
        Assert.Equal(account.Id, profile.Id);
        Assert.Equal(account.Email, profile.Email);
        Assert.Equal([role], profile.Roles);
        var json = await me.Content.ReadAsStringAsync();
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stamp", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownEmailWrongPasswordAndUnavailableAccountsHaveTheSameFailure()
    {
        var account = await RegisterAsync();
        using var unknown = await api.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest($"missing-{Guid.NewGuid():N}@shop.test", Password));
        using var wrong = await api.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(account.Email, "Wrong123!"));

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await users.FindByIdAsync(account.Id.ToString());
            Assert.NotNull(user);
            Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(5))).Succeeded);
        }

        using var locked = await api.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(account.Email, Password));
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await users.FindByIdAsync(account.Id.ToString());
            Assert.NotNull(user);
            Assert.True((await users.SetLockoutEndDateAsync(user, null)).Succeeded);
            Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
        }

        using var twoFactor = await api.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(account.Email, Password));
        foreach (var response in new[] { unknown, wrong, locked, twoFactor })
        {
            var problem = await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Users.InvalidCredentials);
            Assert.Equal("Invalid credentials", problem.GetProperty("title").GetString());
            Assert.False(problem.TryGetProperty("accessToken", out _));
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task LoginAcceptsAnExistingAdminPasswordLongerThanTheRegistrationLimit()
    {
        // Bootstrap accounts go directly through Identity and need not share the registration DTO's length cap.
        var email = $"long-password-{Guid.NewGuid():N}@shop.test";
        var password = Password + new string('x', RegisterRequestValidator.MaxPasswordLength);
        await using (var scope = api.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = new User { UserName = email, Email = email };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            api.TrackUser(user.Id);
            Assert.True((await users.AddToRoleAsync(user, Roles.Admin)).Succeeded);
        }

        using var response = await api.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("not-an-email", "weak")]
    public async Task InvalidCredentialFieldsStillReturnGeneric401(string? email, string? password)
    {
        using var response = await api.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));
        var problem = await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Users.InvalidCredentials);
        Assert.Equal("Invalid credentials", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task MalformedJsonReturnsProblemDetails400()
    {
        using var response = await api.Client.PostAsync("/api/v1/auth/login", new StringContent("{bad json", Encoding.UTF8, "application/json"));
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Common.BadRequest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-jwt")]
    public async Task MeRequiresAValidBearerToken(string? token)
    {
        using var response = await GetMeAsync(token);
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Common.Unauthorized);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("wrong-signature")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-audience")]
    [InlineData("wrong-algorithm")]
    [InlineData("invalid-subject")]
    [InlineData("missing-subject")]
    public async Task MeRejectsInvalidTokens(string scenario)
    {
        var account = await RegisterAsync();
        using var response = await GetMeAsync(CreateToken(scenario, account.Id));
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Common.Unauthorized);
    }

    [Fact]
    public async Task MeDoesNotTrustAUserIdFromTheQueryAndRejectsDeletedAccounts()
    {
        var account = await RegisterAsync();
        using var login = await api.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(account.Email, Password));
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/users/me?id={Guid.NewGuid()}"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            using var response = await api.Client.SendAsync(request);
            Assert.Equal(account.Id, (await response.Content.ReadFromJsonAsync<CurrentUserResponse>())!.Id);
        }

        await using (var scope = api.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await users.FindByIdAsync(account.Id.ToString());
            Assert.NotNull(user);
            Assert.True((await users.DeleteAsync(user)).Succeeded);
        }

        using var deleted = await GetMeAsync(token.AccessToken);
        await AssertProblemAsync(deleted, HttpStatusCode.Unauthorized, ErrorCodes.Common.Unauthorized);
    }

    private async Task<RegisterResponse> RegisterAsync()
    {
        var email = $"login-{Guid.NewGuid():N}@shop.test";
        using var response = await api.Client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, Password));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
        api.TrackUser(account.Id);
        return account;
    }

    private Task<HttpResponseMessage> GetMeAsync(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/me");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return api.Client.SendAsync(request);
    }

    private string CreateToken(string scenario, Guid userId)
    {
        var now = DateTime.UtcNow;
        var key = scenario == "wrong-signature" ? Convert.ToBase64String(new byte[32]) : api.SigningKey;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = scenario == "invalid-subject" ? "invalid" : userId.ToString(),
            ["email"] = "test@shop.test",
            ["role"] = Roles.Customer,
            ["jti"] = Guid.NewGuid().ToString(),
        };
        if (scenario == "missing-subject")
        {
            claims.Remove("sub");
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = scenario == "wrong-issuer" ? "other" : "shop",
            Audience = scenario == "wrong-audience" ? "other" : "shop-api",
            IssuedAt = now.AddMinutes(-20),
            NotBefore = now.AddMinutes(-20),
            Expires = scenario == "expired" ? now.AddMinutes(-1) : now.AddMinutes(15),
            Claims = claims,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(key)),
                scenario == "wrong-algorithm" ? SecurityAlgorithms.HmacSha384 : SecurityAlgorithms.HmacSha256),
        });
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        return problem;
    }
}
