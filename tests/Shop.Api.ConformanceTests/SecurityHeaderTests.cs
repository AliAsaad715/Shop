using System.Net.Http.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Shop.Api.ConformanceTests;

[Collection("API conformance")]
public sealed class SecurityHeaderTests(ConformanceFixture fixture)
{
    [Theory]
    [InlineData("GET", "/health", 200)]
    [InlineData("GET", "/docs", 302)]
    [InlineData("GET", "/openapi/v1.json", 200)]
    [InlineData("GET", "/api/v1/no-such-resource", 404)]
    [InlineData("GET", "/api/v1/auth/register", 405)]
    [InlineData("POST", "/api/v1/auth/register", 422)]
    public async Task SecurityHeadersCoverSuccessDocsAndErrorResponses(string method, string path, int status)
    {
        using var response = await SendAsync(method, path);
        Assert.Equal(status, (int)response.StatusCode);

        AssertSecurityHeaders(response, method, path);
    }

    [Fact]
    public async Task DocumentationPageAfterTheRedirectAlsoHasSecurityHeaders()
    {
        using var redirect = await fixture.Client.GetAsync("/docs");
        Assert.Equal(302, (int)redirect.StatusCode);
        Assert.NotNull(redirect.Headers.Location);
        var target = new Uri(fixture.Client.BaseAddress!, redirect.Headers.Location);
        Assert.Equal(fixture.Client.BaseAddress!.Host, target.Host);
        using var page = await fixture.Client.GetAsync(target);
        Assert.Equal(200, (int)page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);

        AssertSecurityHeaders(page, "GET", target.AbsolutePath);
    }

    [Theory]
    [InlineData("GET", "/api/v1/no-such-resource", 404)]
    [InlineData("POST", "/api/v1/auth/register", 422)]
    public async Task ApiResponsesHaveAStrictContentSecurityPolicy(string method, string path, int status)
    {
        using var response = await SendAsync(method, path);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var values), "Task 13: API CSP is missing.");
        var policy = string.Join(";", values);
        Assert.Contains("default-src 'none'", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'unsafe-inline'", policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'unsafe-eval'", policy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HstsIsPresentOverHttpsForANonLocalhostHost()
    {
        using var response = await fixture.Client.GetAsync("/health");

        Assert.True(response.Headers.TryGetValues("Strict-Transport-Security", out var values), "Task 13: HTTPS HSTS is missing.");
        var maxAge = string.Join(";", values).Split(';', StringSplitOptions.TrimEntries)
            .Single(value => value.StartsWith("max-age=", StringComparison.OrdinalIgnoreCase));
        Assert.True(long.TryParse(maxAge["max-age=".Length..], out var seconds) && seconds > 0);
    }

    [Fact]
    public async Task HstsIsNotSentOverHttp()
    {
        using var response = await fixture.Client.GetAsync("http://shop.test/health");

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Theory]
    [InlineData("/health", 200)]
    [InlineData("/api/v1/no-such-resource", 404)]
    public async Task AuthenticatedResponsesAreNotStored(string path, int status)
    {
        var observation = fixture.Factory.Services.GetRequiredService<AuthenticationObservation>();
        var before = observation.AuthenticatedRequests;
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Conformance-User", "customer");
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(observation.AuthenticatedRequests > before, "The test request was not authenticated.");
        Assert.True(response.Headers.CacheControl?.NoStore == true, "Task 13: authenticated responses need Cache-Control: no-store.");
    }

    [Fact]
    public void KestrelServerHeaderIsDisabledInTheRealHostConfiguration()
    {
        // TestServer does not add Kestrel's Server header; check the actual host setting as well as wire headers.
        var options = fixture.Factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        Assert.False(options.AddServerHeader, "Task 13: Kestrel AddServerHeader must be disabled.");
    }

    private Task<HttpResponseMessage> SendAsync(string method, string path) => method == "POST"
        ? fixture.Client.PostAsJsonAsync(path, new { email = "invalid", password = "" })
        : fixture.Client.GetAsync(path);

    private static void AssertSecurityHeaders(HttpResponseMessage response, string method, string path)
    {
        var missing = new List<string>();
        RequireHeader(response, "X-Content-Type-Options", "nosniff", missing);
        RequireHeader(response, "X-Frame-Options", "DENY", missing);
        RequireHeader(response, "Referrer-Policy", "no-referrer", missing);
        if (response.Headers.Contains("Server"))
        {
            missing.Add("Server must be absent");
        }

        Assert.True(missing.Count == 0, $"Task 13 conformance gap on {method} {path}: {string.Join("; ", missing)}");
    }

    private static void RequireHeader(HttpResponseMessage response, string name, string expected, List<string> missing)
    {
        if (!response.Headers.TryGetValues(name, out var values)
            || !string.Equals(string.Join(",", values), expected, StringComparison.OrdinalIgnoreCase))
        {
            missing.Add($"{name}: {expected}");
        }
    }
}
