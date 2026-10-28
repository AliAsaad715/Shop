using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shop.Modules.Users.Auth;
using Shop.Modules.Users.Domain;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;
using Xunit;

namespace Shop.Modules.Users.IntegrationTests;

[Collection("Users HTTP")]
public sealed class LoginProtectionTests : IAsyncLifetime
{
    private const string Password = "Customer123!";
    private readonly AuthApi _api = new() { LoginPermitLimit = 5 };

    public Task InitializeAsync() => _api.InitializeAsync();
    public Task DisposeAsync() => _api.DisposeAsync();

    [Theory]
    [InlineData(Roles.Customer)]
    [InlineData(Roles.Admin)]
    public async Task FiveFailuresAcrossDifferentIpsLockTheAccountForFiveMinutes(string role)
    {
        var account = await RegisterAsync(role);
        var before = DateTimeOffset.UtcNow;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var response = await LoginAsync(account.Email.ToUpperInvariant(), "Wrong123!", $"192.0.2.{attempt}");
            await AssertInvalidCredentialsAsync(response);
        }

        var state = await ReadUserAsync(account.Id);
        Assert.True(state.LockoutEnabled);
        Assert.NotNull(state.LockoutEnd);
        Assert.InRange(state.LockoutEnd.Value, before.AddMinutes(5), DateTimeOffset.UtcNow.AddMinutes(5));

        // A correct password from a fresh IP must not bypass or extend the lockout.
        using var locked = await LoginAsync(account.Email, Password, "192.0.2.6");
        await AssertInvalidCredentialsAsync(locked);
        Assert.Equal(state.LockoutEnd, (await ReadUserAsync(account.Id)).LockoutEnd);

        // Advance the persisted deadline rather than sleeping for five minutes.
        await using (var scope = _api.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await users.FindByIdAsync(account.Id.ToString());
            Assert.NotNull(user);
            Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddSeconds(-1))).Succeeded);
        }

        using var recovered = await LoginAsync(account.Email, Password, "192.0.2.7");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(0, (await ReadUserAsync(account.Id)).AccessFailedCount);
    }

    [Fact]
    public async Task SuccessfulLoginResetsTheFailureCount()
    {
        var account = await RegisterAsync();
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var failed = await LoginAsync(account.Email, "Wrong123!", "192.0.2.20");
            await AssertInvalidCredentialsAsync(failed);
        }

        Assert.Equal(4, (await ReadUserAsync(account.Id)).AccessFailedCount);
        using var success = await LoginAsync(account.Email, Password, "192.0.2.20");
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(0, (await ReadUserAsync(account.Id)).AccessFailedCount);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var failed = await LoginAsync(account.Email, "Wrong123!", "192.0.2.21");
            await AssertInvalidCredentialsAsync(failed);
        }

        var state = await ReadUserAsync(account.Id);
        Assert.Equal(4, state.AccessFailedCount);
        Assert.Null(state.LockoutEnd);
    }

    [Fact]
    public async Task IpQuotaIncludesSuccessfulAndFailedLoginsAndCannotBeSpoofedByForwardingHeaders()
    {
        var account = await RegisterAsync();
        using var success = await LoginAsync(account.Email, Password, "192.0.2.30");
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var failed = await LoginAsync($"missing-{attempt}@shop.test", "Wrong123!", "192.0.2.30");
            await AssertInvalidCredentialsAsync(failed);
        }

        using var blocked = await LoginAsync(account.Email, "Wrong123!", "192.0.2.30");
        await AssertRateLimitedAsync(blocked);
        Assert.Equal(0, (await ReadUserAsync(account.Id)).AccessFailedCount);

        using var otherIp = await LoginAsync(account.Email, Password, "192.0.2.31");
        Assert.Equal(HttpStatusCode.OK, otherIp.StatusCode);

        // The limiter applies only to login; registration and profile requests retain their normal behavior.
        using var registerRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        {
            Content = JsonContent.Create(new RegisterRequest(account.Email, Password)),
        };
        registerRequest.Headers.Add("X-Test-Client-IP", "192.0.2.30");
        using var registration = await _api.Client.SendAsync(registerRequest);
        Assert.Equal(HttpStatusCode.Conflict, registration.StatusCode);
        using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/me");
        profileRequest.Headers.Add("X-Test-Client-IP", "192.0.2.30");
        using var profile = await _api.Client.SendAsync(profileRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, profile.StatusCode);
    }

    [Fact]
    public async Task Ipv4AndItsMappedIpv6AddressShareTheSameQuota()
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var response = await LoginAsync("missing@shop.test", Password, "192.0.2.40");
            await AssertInvalidCredentialsAsync(response);
        }

        using var mapped = await LoginAsync("missing@shop.test", Password, "::ffff:192.0.2.40");
        await AssertRateLimitedAsync(mapped);
    }

    [Fact]
    public async Task InvalidJsonAlsoConsumesTheIpQuota()
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
            {
                Content = new StringContent("{bad json", Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Test-Client-IP", "192.0.2.50");
            using var response = await _api.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using var blocked = await LoginAsync("missing@shop.test", Password, "192.0.2.50");
        await AssertRateLimitedAsync(blocked);
    }

    [Fact]
    public async Task PermitsBecomeAvailableAfterTheConfiguredWindow()
    {
        var api = new AuthApi { LoginPermitLimit = 1, LoginWindowSeconds = 1 };
        await api.InitializeAsync();
        try
        {
            // Invalid fields avoid password hashing, keeping both requests inside the one-second window.
            using var first = await api.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(null, null));
            await AssertInvalidCredentialsAsync(first);
            using var blocked = await api.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(null, null));
            await AssertRateLimitedAsync(blocked);
            await Task.Delay(TimeSpan.FromSeconds(1.2));
            using var nextWindow = await api.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(null, null));
            await AssertInvalidCredentialsAsync(nextWindow);
        }
        finally
        {
            await api.DisposeAsync();
        }
    }

    private async Task<RegisterResponse> RegisterAsync(string role = Roles.Customer)
    {
        var email = $"protection-{Guid.NewGuid():N}@shop.test";
        using var response = await _api.Client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, Password));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var account = (await response.Content.ReadFromJsonAsync<RegisterResponse>())!;
        _api.TrackUser(account.Id);
        if (role == Roles.Admin)
        {
            await using var scope = _api.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await users.FindByIdAsync(account.Id.ToString());
            Assert.NotNull(user);
            Assert.True((await users.RemoveFromRoleAsync(user, Roles.Customer)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, Roles.Admin)).Succeeded);
        }

        return account;
    }

    private async Task<User> ReadUserAsync(Guid id)
    {
        await using var scope = _api.Services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByIdAsync(id.ToString());
        Assert.NotNull(user);
        return user;
    }

    private async Task<HttpResponseMessage> LoginAsync(string email, string password, string address)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(email, password)),
        };
        request.Headers.Add("X-Test-Client-IP", address);
        request.Headers.Add("X-Forwarded-For", $"198.51.100.{Random.Shared.Next(1, 255)}");
        request.Headers.Add("Forwarded", $"for=198.51.100.{Random.Shared.Next(1, 255)}");
        return await _api.Client.SendAsync(request);
    }

    private static async Task AssertInvalidCredentialsAsync(HttpResponseMessage response)
    {
        var problem = await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Users.InvalidCredentials);
        Assert.Equal("Invalid credentials", problem.GetProperty("title").GetString());
        Assert.False(problem.TryGetProperty("accessToken", out _));
    }

    private static async Task AssertRateLimitedAsync(HttpResponseMessage response)
    {
        await AssertProblemAsync(response, HttpStatusCode.TooManyRequests, ErrorCodes.Common.TooManyRequests);
        Assert.NotNull(response.Headers.RetryAfter?.Delta);
        Assert.InRange(response.Headers.RetryAfter.Delta.Value.TotalSeconds, 1, 60);
        Assert.True(response.Headers.CacheControl?.NoStore);
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
