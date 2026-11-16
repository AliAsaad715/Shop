using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shop.Messaging.Consuming;
using Shop.Modules.Notifications.Data;
using Shop.Modules.Notifications.Email;
using Shop.Modules.Orders.Contracts;
using Testcontainers.PostgreSql;
using Xunit;

namespace Shop.Modules.Notifications.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class NotificationsCollection : ICollectionFixture<NotificationsFixture>
{
    public const string Name = "Notifications database";
}

/// <summary>
/// The worker's services as AddNotificationsWorker registers them, on a real Postgres, with a recording email sender in
/// place of the mail provider. Nothing here starts the RabbitMQ consumer; tests hand messages to the handler directly.
/// </summary>
public sealed class NotificationsFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;

    public ServiceProvider Services { get; private set; } = null!;
    internal RecordingEmailSender Sender { get; } = new();

    public async Task InitializeAsync()
    {
        // The tests delete every processed message, so an external database must be a disposable one
        var connectionString = Environment.GetEnvironmentVariable("SHOP_NOTIFICATIONS_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _postgres = new PostgreSqlBuilder("postgres:18").Build();
            await _postgres.StartAsync();
            connectionString = _postgres.GetConnectionString();
        }

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Shop"] = connectionString })
            .Build());
        services.AddLogging();
        services.AddSingleton<IEmailSender>(Sender);
        services.AddNotificationsWorker();
        Services = services.BuildServiceProvider();

        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Services is not null)
        {
            await Services.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    public static OrderPlacedEvent Order(string? email = "ada@shop.test") => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        email,
        "Pending",
        59.97m,
        [
            new OrderPlacedLine(Guid.CreateVersion7(), "Cotton T-Shirt", 19.99m, 2, 39.98m),
            new OrderPlacedLine(Guid.CreateVersion7(), "Ceramic Mug", 19.99m, 1, 19.99m),
        ],
        0,
        new DateTimeOffset(2026, 11, 15, 9, 30, 0, TimeSpan.Zero));

    // As the consumer hands it over: the body is the outbox's camelCase JSON
    public static ReceivedMessage Message(OrderPlacedEvent order, Guid? messageId = null) => new(
        messageId ?? Guid.CreateVersion7(),
        "order.placed",
        JsonSerializer.SerializeToUtf8Bytes(order, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        0);
}

// Stands in for the mail provider: keeps every email, and can fail the next send as a mail server that is down would
internal sealed class RecordingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public bool FailNextSend { get; set; }

    public void Reset()
    {
        Sent.Clear();
        FailNextSend = false;
    }

    public Task SendAsync(EmailMessage email, CancellationToken cancellationToken)
    {
        if (FailNextSend)
        {
            FailNextSend = false;
            throw new IOException("The mail server didn't answer.");
        }

        Sent.Enqueue(email);
        return Task.CompletedTask;
    }
}
