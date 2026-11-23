using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Shop.Api.IntegrationTests;

// Exercise the real executable, including its exit code. These databases never belong to the Compose stack.
public sealed class MigratorTests(MigratorFixture fixture) : IClassFixture<MigratorFixture>
{
    [Fact]
    public async Task MigratorInitializesAllFourSchemasAndCanBeRepeatedWithoutStartingTheHost()
    {
        var settings = Settings(await fixture.CreateDatabaseAsync(), fixture.CreateImages());
        // These options are needed only by a running API. Starting its host would fail validation.
        settings["Jwt:SigningKey"] = "invalid";
        settings["Inventory:Address"] = "invalid";
        settings["RabbitMq:Host"] = "";

        var first = await RunMigratorAsync(settings);
        Assert.Equal(0, first.ExitCode);
        Assert.Contains("Database initialization completed", first.Output);
        Assert.DoesNotContain("Now listening on", first.Output);
        Assert.DoesNotContain("Hosting failed to start", first.Output);
        var database = settings["ConnectionStrings:Shop"];
        foreach (var schema in new[] { "users", "products", "orders", "notifications" })
        {
            Assert.True(await ScalarAsync<long>(database, $"SELECT count(*) FROM {schema}.\"__EFMigrationsHistory\"") > 0);
        }
        Assert.Equal(2, await ScalarAsync<long>(database, "SELECT count(*) FROM users.roles"));
        Assert.Equal(1, await ScalarAsync<long>(database, "SELECT count(*) FROM users.user_roles"));
        Assert.Equal(6, await ScalarAsync<long>(database, "SELECT count(*) FROM products.products"));
        Assert.Equal(0, await ScalarAsync<long>(database, "SELECT count(*) FROM notifications.processed_messages"));
        var passwordHash = await ScalarAsync<string>(database, "SELECT password_hash FROM users.users");
        var images = Directory.GetFiles(settings["Images:RootPath"], "*.png", SearchOption.AllDirectories).Order().ToArray();
        Assert.Equal(6, images.Length);

        await ExecuteAsync(database, "UPDATE products.products SET price=64, name='Edited demo product'");
        settings["Admin:Password"] = "Different123!";
        var second = await RunMigratorAsync(settings);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(1, await ScalarAsync<long>(database, "SELECT count(*) FROM users.users"));
        Assert.Equal(passwordHash, await ScalarAsync<string>(database, "SELECT password_hash FROM users.users"));
        Assert.Equal(6, await ScalarAsync<long>(database,
            "SELECT count(*) FROM products.products WHERE price=64 AND name='Edited demo product'"));
        Assert.Equal(images, Directory.GetFiles(settings["Images:RootPath"], "*.png", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public async Task FailedSeedingReturnsOneAndCanBeRetriedOnTheSameDatabase()
    {
        var settings = Settings(await fixture.CreateDatabaseAsync(), fixture.CreateImages());
        settings["Admin:Password"] = "weak";
        var failed = await RunMigratorAsync(settings);
        Assert.Equal(1, failed.ExitCode);
        Assert.Contains("Database initialization failed", failed.Output);
        Assert.DoesNotContain("Database initialization completed", failed.Output);
        Assert.Equal(0, await ScalarAsync<long>(settings["ConnectionStrings:Shop"], "SELECT count(*) FROM products.products"));
        // Notifications was migrated too, before the first seeder was attempted.
        Assert.Equal(0, await ScalarAsync<long>(settings["ConnectionStrings:Shop"], "SELECT count(*) FROM notifications.processed_messages"));

        settings["Admin:Password"] = "Admin123!";
        var retried = await RunMigratorAsync(settings);
        Assert.Equal(0, retried.ExitCode);
        Assert.Equal(1, await ScalarAsync<long>(settings["ConnectionStrings:Shop"], "SELECT count(*) FROM users.users"));
        Assert.Equal(6, await ScalarAsync<long>(settings["ConnectionStrings:Shop"], "SELECT count(*) FROM products.products"));
    }

    [Fact]
    public async Task FailedMigrationReturnsOneWithoutAttemptingAnySeeder()
    {
        var database = await fixture.CreateDatabaseAsync();
        var settings = Settings(new NpgsqlConnectionStringBuilder(database)
            { Options = "-c default_transaction_read_only=on" }.ConnectionString, fixture.CreateImages());
        var result = await RunMigratorAsync(settings);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Database initialization failed", result.Output);
        Assert.DoesNotContain("Running module seeder", result.Output);
        Assert.Equal(0, await ScalarAsync<long>(database,
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name IN ('users','products','orders','notifications')"));
        Assert.Empty(Directory.GetFiles(settings["Images:RootPath"], "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OptionalSeedsCanBeDisabled()
    {
        var settings = Settings(await fixture.CreateDatabaseAsync(), fixture.CreateImages());
        settings["Admin:Email"] = "";
        settings["Admin:Password"] = "";
        settings["DemoSeed:Enabled"] = "false";
        var result = await RunMigratorAsync(settings);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No admin account seeded", result.Output);
        Assert.Equal(0, await ScalarAsync<long>(settings["ConnectionStrings:Shop"], "SELECT count(*) FROM users.users"));
        Assert.Equal(0, await ScalarAsync<long>(settings["ConnectionStrings:Shop"], "SELECT count(*) FROM products.products"));
        Assert.Empty(Directory.GetFiles(settings["Images:RootPath"], "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task NormalApiDoesNotInitializeAnEmptyDatabaseEvenWhenSeedsAreEnabled(string environment)
    {
        var settings = Settings(await fixture.CreateDatabaseAsync(), fixture.CreateImages());
        await using var factory = new RuntimeApiFactory(settings, environment);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://shop.test") });
        using var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        // A legacy startup migration or seeder would have initialized the database or crashed before this response.
        Assert.Equal(0, await ScalarAsync<long>(settings["ConnectionStrings:Shop"],
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name IN ('users','products','orders','notifications')"));
        Assert.Empty(Directory.GetFiles(settings["Images:RootPath"], "*", SearchOption.AllDirectories));
    }

    private static Dictionary<string, string> Settings(string connection, string images) => new()
    {
        ["ConnectionStrings:Shop"] = connection,
        ["ReadRouting:Enabled"] = "false",
        ["CatalogCache:Enabled"] = "false",
        ["Admin:Email"] = "admin@shop.test",
        ["Admin:Password"] = "Admin123!",
        ["DemoSeed:Enabled"] = "true",
        ["Images:RootPath"] = images,
        ["Jwt:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["Jwt:Issuer"] = "shop", ["Jwt:Audience"] = "shop-api", ["Jwt:LifetimeMinutes"] = "15",
        ["Inventory:Address"] = "http://127.0.0.1:1",
        ["RabbitMq:Host"] = "127.0.0.1", ["RabbitMq:Port"] = "1",
        ["RabbitMq:UserName"] = "shop", ["RabbitMq:Password"] = "shop",
        ["RabbitMq:ConnectTimeout"] = "00:00:01",
        ["LoginRateLimit:PermitLimit"] = "5", ["LoginRateLimit:WindowSeconds"] = "60",
        ["Cors:AllowedOrigins:0"] = "http://localhost:5173",
    };

    private static async Task<(int ExitCode, string Output)> RunMigratorAsync(Dictionary<string, string> settings)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(global::Program).Assembly.Location);
        start.ArgumentList.Add("--migrate-and-seed");
        start.Environment["DOTNET_ENVIRONMENT"] = Environments.Production;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = Environments.Production;
        foreach (var setting in settings) start.Environment[setting.Key.Replace(":", "__")] = setting.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the migrator");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"Migrator did not exit: {await output}\n{await errors}");
        }
        return (process.ExitCode, $"{await output}\n{await errors}");
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class RuntimeApiFactory(Dictionary<string, string> settings, string environment)
        : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(settings.Select(setting =>
                    new KeyValuePair<string, string?>(setting.Key, setting.Value))));
        }
    }
}

public sealed class MigratorFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18").Build();
    private readonly List<DirectoryInfo> _images = [];

    public Task InitializeAsync() => _postgres.StartAsync();

    public async Task<string> CreateDatabaseAsync()
    {
        var database = $"r504_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {database}", connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = database }.ConnectionString;
    }

    public string CreateImages()
    {
        var directory = Directory.CreateTempSubdirectory("shop-r504-images-");
        _images.Add(directory);
        return directory.FullName;
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        foreach (var directory in _images)
        {
            if (directory.FullName.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && directory.Name.StartsWith("shop-r504-images-", StringComparison.Ordinal)) directory.Delete(recursive: true);
        }
    }
}
