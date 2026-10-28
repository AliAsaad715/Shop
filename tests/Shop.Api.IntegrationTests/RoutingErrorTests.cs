using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shop.Api.Docs;
using Shop.Api.Routing;
using Shop.Api.Security;
using Shop.Modules.Users;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;
using Xunit;

namespace Shop.Api.IntegrationTests;

public sealed class RoutingApi : IAsyncLifetime
{
    private WebApplication _app = null!;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public HttpClient Client { get; private set; } = null!;
    public int? HttpsPort { get; init; }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = Convert.ToBase64String(_key),
            ["Jwt:Issuer"] = "shop",
            ["Jwt:Audience"] = "shop-api",
            ["Jwt:LifetimeMinutes"] = "15",
            ["Admin:Email"] = null,
            ["Admin:Password"] = null,
            ["Cors:AllowedOrigins:0"] = "https://frontend.shop.test",
            // Keep login-quota assertions independent of developer or CI environment settings.
            ["LoginRateLimit:PermitLimit"] = "5",
            ["LoginRateLimit:WindowSeconds"] = "60",
            // A valid provider configuration is needed to construct Identity; these tests never query it.
            ["ConnectionStrings:Shop"] = "Host=127.0.0.1;Database=unused_routing_test;Username=postgres",
        });
        builder.Services.AddUsersModule();
        builder.Services.AddVersionedApiDocs();
        builder.Services.AddHealthChecks();
        builder.Services.AddErrorContract();
        builder.Services.AddBrowserSecurity();
        if (HttpsPort is { } httpsPort)
        {
            builder.Services.AddHttpsRedirection(options => options.HttpsPort = httpsPort);
        }
        _app = builder.Build();
        _app.UseHsts();
        _app.UseSecurityHeaders();
        _app.UseExceptionHandler();
        _app.UseHttpsRedirection();
        _app.UseStatusCodePages();
        _app.UseRouting();

        // An explicitly protected custom endpoint must also continue through authorization.
        _app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/test/custom")
            {
                context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                    new EndpointMetadataCollection(new AuthorizeAttribute()), "custom protected endpoint"));
            }

            await next(context);
        });
        _app.UseFrontendCors();
        _app.UseRateLimiter();
        _app.UseAuthentication();
        _app.UseRoutingErrors();
        _app.UseAuthorization();
        _app.MapVersionedApi().MapUsersEndpoints();
        _app.MapHealthChecks("/health").AllowAnonymous();
        _app.MapApiDocs();
        _app.MapGet("/test/items", () => "get").AllowAnonymous();
        _app.MapPost("/test/items", () => "post").AllowAnonymous();
        _app.MapGet("/test/resource/{id:guid}", (Guid id) => id).AllowAnonymous();
        _app.MapGet("/test/protected", () => "protected");
        _app.MapGet("/test/admin", () => "admin").RequireAuthorization(Policies.AdminOnly);
        _app.MapGet("/api/v1/test/failure", (Func<string>)(() => throw new InvalidOperationException("Private test error")))
            .AllowAnonymous();
        await _app.StartAsync();
        Client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }

    public string CustomerToken() => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "shop",
        Audience = "shop-api",
        IssuedAt = DateTime.UtcNow,
        NotBefore = DateTime.UtcNow,
        Expires = DateTime.UtcNow.AddMinutes(15),
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256),
        Claims = new Dictionary<string, object>
        {
            ["sub"] = Guid.CreateVersion7().ToString(),
            ["email"] = "customer@shop.test",
            ["role"] = Roles.Customer,
            ["jti"] = Guid.NewGuid().ToString(),
        },
    });
}

public sealed class RoutingErrorTests(RoutingApi api) : IClassFixture<RoutingApi>
{
    [Theory]
    [InlineData("GET", "/api/v1/auth/login", "POST")]
    [InlineData("PATCH", "/api/v1/auth/register", "POST")]
    [InlineData("POST", "/api/v1/users/me", "GET")]
    [InlineData("DELETE", "/test/items", "GET, POST")]
    [InlineData("POST", "/test/resource/019a2180-0000-7000-8000-000000000001", "GET")]
    public async Task UnsupportedMethodKeeps405AndAllow(string method, string path, string allowed)
    {
        foreach (var authenticated in new[] { false, true })
        {
            using var response = await SendAsync(method, path, authenticated);
            await AssertProblemAsync(response, HttpStatusCode.MethodNotAllowed, ErrorCodes.Common.MethodNotAllowed);
            Assert.Equal(allowed.Split(", "), response.Content.Headers.Allow.Order(StringComparer.Ordinal));
        }
    }

    [Theory]
    [InlineData("GET", "/api/v1/missing")]
    [InlineData("POST", "/api/v1/missing")]
    [InlineData("DELETE", "/api/v1/missing.json")]
    [InlineData("GET", "/test/resource/not-a-guid")]
    public async Task UnknownPathReturns404(string method, string path)
    {
        foreach (var authenticated in new[] { false, true })
        {
            using var response = await SendAsync(method, path, authenticated);
            await AssertProblemAsync(response, HttpStatusCode.NotFound, ErrorCodes.Common.NotFound);
            Assert.Empty(response.Content.Headers.Allow);
        }
    }

    [Theory]
    [InlineData("/test/protected")]
    [InlineData("/test/custom")]
    [InlineData("/test/admin")]
    [InlineData("/api/v1/users/me")]
    public async Task RealEndpointsRemainProtected(string path)
    {
        using var response = await SendAsync("GET", path, false);
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.Common.Unauthorized);
    }

    [Fact]
    public async Task ValidBearerCanAccessAnUnannotatedEndpoint()
    {
        using var response = await SendAsync("GET", "/test/protected", true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WrongRoleRemainsForbidden()
    {
        using var response = await SendAsync("GET", "/test/admin", true);
        await AssertProblemAsync(response, HttpStatusCode.Forbidden, ErrorCodes.Common.Forbidden);
    }

    [Theory]
    [InlineData("/health", 200)]
    [InlineData("/docs", 302)]
    [InlineData("/docs/v1", 200)]
    [InlineData("/openapi/v1.json", 200)]
    public async Task PublicEndpointsRemainAccessible(string path, int status)
    {
        using var response = await SendAsync("GET", path, false);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        if (status == 302)
        {
            Assert.NotNull(response.Headers.Location);
            using var redirected = await api.Client.GetAsync(response.Headers.Location);
            Assert.Equal(HttpStatusCode.OK, redirected.StatusCode);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string method, string path, bool authenticated)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (authenticated)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.CustomerToken());
        }

        return await api.Client.SendAsync(request);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
    }
}
