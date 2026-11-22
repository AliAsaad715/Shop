using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shop.Api.Instances;
using Shop.SharedKernel.Errors;
using Xunit;

namespace Shop.Api.IntegrationTests;

// Task R5-03: behind Nginx, X-Instance says which of the three instances answered
public sealed class InstanceHeaderTests
{
    [Theory]
    [InlineData("/ok", 200)]
    [InlineData("/missing", 404)]
    [InlineData("/failure", 500)]
    public async Task EveryResponseSaysWhichInstanceAnsweredErrorsIncluded(string path, int status)
    {
        await using var app = await StartAsync("api2");
        using var response = await app.GetTestClient().GetAsync(path);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("api2", Assert.Single(response.Headers.GetValues(InstanceName.Header)));
    }

    [Fact]
    public async Task WithoutAnInstanceNameThereIsNoHeader()
    {
        await using var app = await StartAsync(null);
        using var response = await app.GetTestClient().GetAsync("/ok");
        Assert.False(response.Headers.Contains(InstanceName.Header));
    }

    [Theory]
    [InlineData("api 1")]
    [InlineData("api1\r\nX-Other: 1")]
    [InlineData("ápi1")]
    public void ANameAHeaderCouldNotCarryStopsStartup(string name)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [InstanceName.Setting] = name })
            .Build();
        Assert.Throws<InvalidOperationException>(() => InstanceName.From(configuration));
    }

    // The order Program.cs uses: the header first, then the error handling that rewrites failed responses
    private static async Task<WebApplication> StartAsync(string? name)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddErrorContract();
        var app = builder.Build();
        app.UseInstanceHeader(name);
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.MapGet("/ok", () => "ok");
        app.MapGet("/failure", (Func<string>)(() => throw new InvalidOperationException("Private test error")));
        await app.StartAsync();
        return app;
    }
}
