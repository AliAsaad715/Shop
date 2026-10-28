using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Shop.Api.IntegrationTests;

public sealed class BrowserSecurityTests(RoutingApi api) : IClassFixture<RoutingApi>
{
    private const string FrontendOrigin = "https://frontend.shop.test";

    [Fact]
    public async Task AllowedFrontendCanPreflightLoginWithAuthorizationAndJson()
    {
        using var response = await PreflightAsync(FrontendOrigin);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(FrontendOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("POST", response.Headers.GetValues("Access-Control-Allow-Methods").SelectMany(value => value.Split(',')));
        var headers = string.Join(',', response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("authorization", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-type", headers, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        AssertBaseline(response);
    }

    [Theory]
    [InlineData("https://untrusted.shop.test")]
    [InlineData("http://frontend.shop.test")]
    [InlineData("https://frontend.shop.test:444")]
    [InlineData("https://frontend.shop.test.evil.test")]
    [InlineData("null")]
    public async Task OtherOriginsReceiveNoCorsPermission(string origin)
    {
        using var preflight = await PreflightAsync(origin);
        Assert.False(preflight.Headers.Contains("Access-Control-Allow-Origin"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", origin);
        using var actual = await api.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, actual.StatusCode);
        Assert.False(actual.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("/health", 200)]
    [InlineData("/api/v1/users/me", 401)]
    [InlineData("/api/v1/missing", 404)]
    [InlineData("/api/v1/auth/login", 405)]
    [InlineData("/api/v1/test/failure", 500)]
    public async Task AllowedOriginAndSecurityHeadersSurviveSuccessAndErrors(string path, int status)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://shop.test" + path);
        request.Headers.Add("Origin", FrontendOrigin);
        using var response = await api.Client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(FrontendOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        AssertBaseline(response);
        Assert.True(response.Headers.Contains("Strict-Transport-Security"));
        if (status >= 400)
        {
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(problem.TryGetProperty("code", out _));
            Assert.True(problem.TryGetProperty("traceId", out _));
            Assert.Contains("default-src 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        }
    }

    [Theory]
    [InlineData("/health", 200)]
    [InlineData("/api/v1/missing", 404)]
    [InlineData("/api/v1/auth/login", 405)]
    [InlineData("/test/admin", 403)]
    public async Task RealBearerResponsesAreNotCachedIncludingRoutingErrors(string path, int status)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.CustomerToken());
        using var response = await api.Client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task RateLimitErrorsKeepCorsSecurityHeadersAndExposeRetryAfter()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var allowed = await api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = "", password = "" });
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email = "", password = "" }),
        };
        request.Headers.Add("Origin", FrontendOrigin);
        using var response = await api.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(FrontendOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        AssertBaseline(response);
        Assert.NotNull(response.Headers.RetryAfter);
        var exposed = string.Join(',', response.Headers.GetValues("Access-Control-Expose-Headers"));
        Assert.Contains("Retry-After", exposed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Location", exposed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DocsPermitLocalScriptsAndInlineInitializationWhileApiCspRemainsStrict()
    {
        using var docs = await api.Client.GetAsync("/docs/v1");
        Assert.Equal(HttpStatusCode.OK, docs.StatusCode);
        var policy = Assert.Single(docs.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self' 'unsafe-inline'", policy);
        Assert.Contains("style-src 'self' 'unsafe-inline'", policy);
        Assert.Contains("connect-src 'self'", policy);
        Assert.DoesNotContain("'unsafe-eval'", policy);
        var html = await docs.Content.ReadAsStringAsync();
        Assert.Contains("withDefaultFonts", html);
        Assert.DoesNotContain("https://fonts.scalar.com", html);
        using var error = await api.Client.GetAsync("/api/v1/missing");
        var apiPolicy = Assert.Single(error.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", apiPolicy);
        Assert.DoesNotContain("'unsafe-inline'", apiPolicy);
    }

    [Fact]
    public async Task ConfiguredHttpsPortRedirectsHttpAndKeepsThePathAndQuery()
    {
        var redirectApi = new RoutingApi { HttpsPort = 7443 };
        await redirectApi.InitializeAsync();
        try
        {
            using var response = await redirectApi.Client.GetAsync("http://shop.test/health?probe=1");
            Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
            Assert.Equal("https://shop.test:7443/health?probe=1", response.Headers.Location?.ToString());
            Assert.False(response.Headers.Contains("Strict-Transport-Security"));
            AssertBaseline(response);
        }
        finally
        {
            await redirectApi.DisposeAsync();
        }
    }

    [Fact]
    public async Task ForwardedProtoFromAnUntrustedClientDoesNotEnableHsts()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://shop.test/health");
        request.Headers.Add("X-Forwarded-Proto", "https");
        using var response = await api.Client.SendAsync(request);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    private async Task<HttpResponseMessage> PreflightAsync(string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        return await api.Client.SendAsync(request);
    }

    private static void AssertBaseline(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.False(response.Headers.Contains("Server"));
    }
}
