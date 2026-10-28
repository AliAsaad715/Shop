using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shop.Modules.Products.Data;
using Shop.SharedKernel.Auth;
using Shop.SharedKernel.Errors;
using Shop.SharedKernel.Validation;
using Testcontainers.PostgreSql;
using Xunit;

namespace Shop.Modules.Products.IntegrationTests;

public sealed class CreateProductFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private readonly DirectoryInfo _images = Directory.CreateTempSubdirectory("shop-product-images-");

    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid CategoryId { get; private set; }
    public SaveFailureInterceptor SaveFailure { get; } = new();
    public string ImageRoot => _images.FullName;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("SHOP_PRODUCTS_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _postgres = new PostgreSqlBuilder("postgres:18").Build();
            await _postgres.StartAsync();
            connectionString = _postgres.GetConnectionString();
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Shop"] = connectionString,
            ["Images:RootPath"] = ImageRoot,
        });
        builder.Services.AddProductsModule();
        builder.Services.AddDbContext<ProductsDbContext>(options => options.AddInterceptors(SaveFailure));
        builder.Services.AddErrorContract();
        // Test-only authentication: production JWTs and AdminOnly registration belong to task 11.
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, UploadTestAuthentication>("Test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("AdminOnly", policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Admin));
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });

        App = builder.Build();
        App.UseRouting();
        App.UseExceptionHandler();
        App.UseStatusCodePages();
        App.UseAuthentication();
        App.UseAuthorization();
        App.UseProductUploads();
        App.MapProductImages();
        App.MapGroup("/api/v1").WithRequestValidation().MapProductsEndpoints();

        await using (var scope = App.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
            await context.Database.MigrateAsync();
            CategoryId = await context.Categories.Select(category => category.Id).FirstAsync();
        }

        await App.StartAsync();
        Client = App.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (App is not null)
        {
            await App.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }

        // Delete only the exact temporary directory created by this fixture.
        if (_images.FullName.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && _images.Name.StartsWith("shop-product-images-", StringComparison.Ordinal))
        {
            _images.Delete(recursive: true);
        }
    }
}

public sealed class SaveFailureInterceptor : SaveChangesInterceptor
{
    public bool FailNext { get; set; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("Simulated database save failure.");
        }

        return ValueTask.FromResult(result);
    }
}

internal sealed class UploadTestAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers["X-Test-Role"].ToString();
        if (string.IsNullOrEmpty(role))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, role) }, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
