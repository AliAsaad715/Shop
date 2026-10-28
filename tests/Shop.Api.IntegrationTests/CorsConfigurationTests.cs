using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shop.Api.Security;
using Xunit;

namespace Shop.Api.IntegrationTests;

public sealed class CorsConfigurationTests
{
    [Theory]
    [InlineData("*")]
    [InlineData("https://*.shop.test")]
    [InlineData("https://frontend.shop.test/")]
    [InlineData("https://frontend.shop.test/path")]
    [InlineData("https://user:password@frontend.shop.test")]
    [InlineData("https://frontend.shop.test?query=1")]
    [InlineData("https://frontend.shop.test#fragment")]
    [InlineData("file:///frontend")]
    public void InvalidOriginsAreRejected(string origin)
    {
        using var services = Build(origin);
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<CorsOptions>>().Value);
    }

    [Fact]
    public void MissingOriginConfigurationDoesNotGrantCrossOriginAccess()
    {
        using var services = Build(null);
        var policy = services.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy("Frontend");
        Assert.NotNull(policy);
        Assert.False(policy.IsOriginAllowed("https://frontend.shop.test"));
        Assert.False(policy.IsOriginAllowed("http://localhost:5173"));
        Assert.False(policy.AllowAnyOrigin);
        Assert.False(policy.SupportsCredentials);
    }

    private static ServiceProvider Build(string? origin) => new ServiceCollection()
        .AddLogging()
        .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            origin is null ? [] : new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = origin }).Build())
        .AddBrowserSecurity()
        .BuildServiceProvider();
}
