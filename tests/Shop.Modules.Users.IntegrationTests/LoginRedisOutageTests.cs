using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shop.SharedKernel.Errors;
using Xunit;

namespace Shop.Modules.Users.IntegrationTests;

// No external services: blank credentials never need a database, and this Redis address is deliberately closed.
public sealed class LoginRedisOutageTests
{
    [Fact]
    public async Task RedisFailureUsesThe503ProblemContractBeforeLoginAndLeavesOtherEndpointsAvailable()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Shop"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused",
            ["ConnectionStrings:Redis"] = "127.0.0.1:1",
            ["ReadRouting:Enabled"] = "false",
            ["Jwt:SigningKey"] = Convert.ToBase64String(new byte[32]),
            ["Jwt:Issuer"] = "shop", ["Jwt:Audience"] = "shop-api", ["Jwt:LifetimeMinutes"] = "15",
        });
        builder.Services.AddUsersModule();
        builder.Services.AddErrorContract();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGroup("/api/v1").MapUsersEndpoints();
        app.MapGet("/health", () => "healthy").AllowAnonymous();
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "", password = "" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
        Assert.Equal("application/problem+json", login.Content.Headers.ContentType?.MediaType);
        Assert.True(login.Headers.CacheControl?.NoStore);
        Assert.Equal(TimeSpan.FromSeconds(1), login.Headers.RetryAfter?.Delta);
        var problem = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ErrorCodes.Common.ServiceUnavailable, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var profile = await client.GetAsync("/api/v1/users/me");
        Assert.Equal(HttpStatusCode.Unauthorized, profile.StatusCode);
    }
}
