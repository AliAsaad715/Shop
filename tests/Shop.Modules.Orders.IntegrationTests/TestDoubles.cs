using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shop.Modules.Orders.Domain;
using Shop.Modules.Products.Contracts;
using Shop.SharedKernel.Auth;

namespace Shop.Modules.Orders.IntegrationTests;

// Stands in for the Users module's JWT validation: X-Test-User is the sub claim, X-Test-Role the role
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new("sub", user.ToString()) };
        claims.AddRange(Request.Headers["X-Test-Role"].Select(role => new Claim("role", role!)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, "sub", "role"));

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

internal sealed class TestCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid? UserId => Guid.TryParse(Principal?.FindFirstValue("sub"), out var id) ? id : null;

    public string? Email => null;

    public IReadOnlyCollection<string> Roles => Principal?.FindAll("role").Select(claim => claim.Value).ToArray() ?? [];

    public bool IsInRole(string role) => Principal?.IsInRole(role) == true;
}

public sealed class FakeProductCatalog : IProductCatalog
{
    public static readonly ProductCatalogItem Book = new(Guid.Parse("019a2180-2000-7000-8000-000000000001"), "Book", 19.95m);
    public static readonly ProductCatalogItem Lamp = new(Guid.Parse("019a2180-2000-7000-8000-000000000002"), "Lamp", 45.50m);

    public Task<IReadOnlyDictionary<Guid, ProductCatalogItem>> GetByIdsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<Guid, ProductCatalogItem>>(
            new[] { Book, Lamp }.Where(product => productIds.Contains(product.Id)).ToDictionary(product => product.Id));
}

// Records every call so tests can prove the reserve, save, release flow; tests can also make it refuse or fail
public sealed class RecordingInventory : IInventoryService
{
    public ConcurrentQueue<(Guid OrderId, IReadOnlyList<StockLine> Lines)> Reserved { get; } = new();
    public ConcurrentQueue<Guid> Released { get; } = new();

    // Every order sent to the warehouse, whether or not the call succeeded
    public ConcurrentQueue<Guid> ReserveAttempts { get; } = new();
    public ConcurrentQueue<Guid> ReleaseAttempts { get; } = new();

    public bool RefuseReservations { get; set; }
    public IReadOnlyList<StockShortage> Shortages { get; set; } = [];

    // Thrown in place of an answer, as a warehouse that is down or slow would
    public Func<Exception>? ReserveFailure { get; set; }
    public Func<Exception>? ReleaseFailure { get; set; }

    public void Reset()
    {
        RefuseReservations = false;
        Shortages = [];
        ReserveFailure = null;
        ReleaseFailure = null;
    }

    public Task<int> GetAvailableAsync(Guid productId, CancellationToken ct) => Task.FromResult(int.MaxValue);

    public Task<ReserveResult> ReserveAsync(Guid orderId, IReadOnlyList<StockLine> lines, CancellationToken ct)
    {
        ReserveAttempts.Enqueue(orderId);
        if (ReserveFailure is { } failure)
        {
            return Task.FromException<ReserveResult>(failure());
        }

        if (RefuseReservations)
        {
            return Task.FromResult(new ReserveResult(false, Shortages));
        }

        Reserved.Enqueue((orderId, lines));
        return Task.FromResult(new ReserveResult(true));
    }

    public Task ReleaseAsync(Guid orderId, CancellationToken ct)
    {
        ReleaseAttempts.Enqueue(orderId);
        if (ReleaseFailure is { } failure)
        {
            return Task.FromException(failure());
        }

        Released.Enqueue(orderId);
        return Task.CompletedTask;
    }
}

// Fails the next save that adds an order, as a database dropping the connection mid-request would
public sealed class OrderSaveFailure : SaveChangesInterceptor
{
    public bool FailNextOrderSave { get; set; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (FailNextOrderSave && eventData.Context!.ChangeTracker.Entries<Order>().Any(entry => entry.State == EntityState.Added))
        {
            FailNextOrderSave = false;
            throw new DbUpdateException("Simulated failure saving the order.");
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
