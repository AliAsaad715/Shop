using System.Collections.Concurrent;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shop.Modules.Products.Data;
using Shop.Modules.Products.Images;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;
using Shop.SharedKernel.Validation;
using Testcontainers.PostgreSql;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

public sealed class S3ProductFixture : IAsyncLifetime
{
    public const string Bucket = "shop-images";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();
    private readonly IContainer _seaweed = new ContainerBuilder("chrislusf/seaweedfs:4.53")
        .WithEnvironment("AWS_ACCESS_KEY_ID", "test").WithEnvironment("AWS_SECRET_ACCESS_KEY", "test-secret")
        .WithEnvironment("S3_BUCKET", Bucket).WithPortBinding(8333, true).WithPortBinding(8888, true)
        .WithCommand("mini", "-dir=/data", "-ip=127.0.0.1", "-ip.bind=0.0.0.0", "-webdav=false", "-admin.ui=false", "-s3.autoCreateBucket=false")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8888).ForPath($"/buckets/{Bucket}/")))
        .Build();
    private readonly List<WebApplication> _apps = [];
    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public SaveFailureInterceptor SaveFailure { get; } = new();
    public ConcurrentQueue<Exception> Errors { get; } = new();
    public Guid CategoryId { get; private set; }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await _seaweed.StartAsync();
        App = await CreateAppAsync();
        Client = App.GetTestClient();
        await using var scope = App.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
        await database.Database.MigrateAsync();
        CategoryId = await database.Categories.Select(category => category.Id).FirstAsync();
    }

    public async Task<WebApplication> CreateAppAsync(string? bucket = null, string? secret = null, bool failDelete = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Shop"] = _postgres.GetConnectionString(), ["ReadRouting:Enabled"] = "false",
            ["CatalogCache:Enabled"] = "false", ["Inventory:Address"] = "http://127.0.0.1:1",
            ["Images:Provider"] = "S3", ["Images:S3:Endpoint"] = $"http://{_seaweed.Hostname}:{_seaweed.GetMappedPublicPort(8333)}",
            ["Images:S3:Bucket"] = bucket ?? Bucket, ["Images:S3:AccessKey"] = "test", ["Images:S3:SecretKey"] = secret ?? "test-secret",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ErrorCollector(Errors));
        builder.Services.AddProductsModule();
        builder.Services.AddDbContext<ProductsDbContext>(options => options.AddInterceptors(SaveFailure));
        if (failDelete) builder.Services.AddScoped<IImageStorage>(services => new FailingDeleteStorage(services.GetRequiredService<S3ImageStorage>()));
        builder.Services.AddErrorContract();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, UploadTestAuthentication>("Test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("AdminOnly", policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Admin));
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });
        var app = builder.Build();
        app.UseRouting();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseProductUploads();
        app.MapProductImages();
        app.MapGroup("/api/v1").WithRequestValidation().MapProductsEndpoints();
        _apps.Add(app);
        await app.StartAsync();
        return app;
    }

    public async Task<int> ObjectCountAsync()
    {
        var client = App.Services.GetRequiredService<IAmazonS3>();
        var response = await client.ListObjectsV2Async(new() { BucketName = Bucket });
        return response.S3Objects?.Count ?? 0;
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        foreach (var app in _apps) await app.DisposeAsync();
        await _seaweed.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private sealed class FailingDeleteStorage(S3ImageStorage inner) : IImageStorage
    {
        public Task<string> SaveAsync(Stream content, string extension, CancellationToken ct) => inner.SaveAsync(content, extension, ct);
        public Task<StoredImage?> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task DeleteAsync(string path, CancellationToken ct) => throw new ImageStorageUnavailableException(new IOException("Simulated delete outage"));
    }

    private sealed class ErrorCollector(ConcurrentQueue<Exception> errors) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ErrorLogger(errors);
        public void Dispose() { }
        private sealed class ErrorLogger(ConcurrentQueue<Exception> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            { if (exception is not null) errors.Enqueue(exception); }
        }
    }
}
