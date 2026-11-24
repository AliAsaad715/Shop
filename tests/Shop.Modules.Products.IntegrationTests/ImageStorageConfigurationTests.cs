using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shop.Modules.Products.Images;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

public sealed class ImageStorageConfigurationTests
{
    [Theory]
    [InlineData("Images:Provider", "Unknown")]
    [InlineData("Images:S3:Endpoint", "")]
    [InlineData("Images:S3:Endpoint", "ftp://seaweedfs:8333")]
    [InlineData("Images:S3:Endpoint", "http://user:secret@seaweedfs:8333")]
    [InlineData("Images:S3:Endpoint", "http://seaweedfs:8333/path")]
    [InlineData("Images:S3:Endpoint", "http://seaweedfs:8333/?secret=value")]
    [InlineData("Images:S3:Bucket", "")]
    [InlineData("Images:S3:Bucket", "../invalid")]
    [InlineData("Images:S3:AccessKey", "")]
    [InlineData("Images:S3:SecretKey", "")]
    [InlineData("Images:S3:Region", "")]
    [InlineData("Images:S3:OperationTimeout", "00:00:00")]
    [InlineData("Images:S3:OperationTimeout", "00:00:31")]
    public async Task InvalidS3ConfigurationFailsAtStartupInsteadOfFallingBack(string key, string value)
    {
        var settings = Settings();
        settings[key] = value;
        using var host = Build(settings);
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("Images:Provider", error.Message);
        Assert.DoesNotContain("test-secret", error.Message);
    }

    [Fact]
    public async Task DirectRunsDefaultToLocalWithoutS3Credentials()
    {
        using var host = Build([]);
        await host.StartAsync();
        using var scope = host.Services.CreateScope();
        Assert.IsType<LocalImageStorage>(scope.ServiceProvider.GetRequiredService<IImageStorage>());
        await host.StopAsync();
    }

    [Fact]
    public async Task LocalRequiresARootDirectory()
    {
        using var host = Build(new() { ["Images:Provider"] = "Local", ["Images:RootPath"] = "" });
        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task ValidS3ConfigurationSelectsS3WithoutMakingAStartupNetworkCall()
    {
        using var host = Build(Settings());
        await host.StartAsync();
        using var scope = host.Services.CreateScope();
        Assert.IsType<S3ImageStorage>(scope.ServiceProvider.GetRequiredService<IImageStorage>());
        await host.StopAsync();
    }

    private static Dictionary<string, string?> Settings() => new()
    {
        ["Images:Provider"] = "S3", ["Images:S3:Endpoint"] = "http://127.0.0.1:1",
        ["Images:S3:Bucket"] = "shop-images", ["Images:S3:AccessKey"] = "test",
        ["Images:S3:SecretKey"] = "test-secret",
    };

    private static IHost Build(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddProductImageStorage();
        return builder.Build();
    }
}
