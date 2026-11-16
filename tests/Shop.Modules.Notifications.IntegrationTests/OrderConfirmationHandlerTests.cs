using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shop.Messaging.Consuming;
using Shop.Modules.Notifications.Data;
using Shop.Modules.Notifications.OrderConfirmation;
using Xunit;

namespace Shop.Modules.Notifications.IntegrationTests;

/// <summary>
/// The email worker's handler (task R4-03): one confirmation per order.placed message, even when RabbitMQ delivers it
/// twice, nothing recorded when the send fails, and messages that can never be sent rejected.
/// </summary>
[Collection(NotificationsCollection.Name)]
public sealed class OrderConfirmationHandlerTests(NotificationsFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        fixture.Sender.Reset();
        await using var scope = fixture.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().ProcessedMessages.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SendsTheConfirmationToTheCustomerAndRecordsTheMessage()
    {
        var order = NotificationsFixture.Order();
        var message = NotificationsFixture.Message(order);

        Assert.Equal(MessageOutcome.Done, await HandleAsync(message));

        var email = Assert.Single(fixture.Sender.Sent);
        Assert.Equal("ada@shop.test", email.To);
        Assert.Equal($"We received your order {order.OrderId.ToString()[..8]}", email.Subject);
        var recorded = Assert.Single(await ProcessedAsync());
        Assert.Equal(message.MessageId, recorded.MessageId);
        Assert.Equal("order.placed", recorded.Type);
    }

    [Fact]
    public async Task ARepeatOfAHandledMessageIsAcknowledgedWithoutSendingAgain()
    {
        var message = NotificationsFixture.Message(NotificationsFixture.Order());

        Assert.Equal(MessageOutcome.Done, await HandleAsync(message));
        // Delivered again: the worker crashed before its ack, or the outbox published the row twice
        Assert.Equal(MessageOutcome.Done, await HandleAsync(message));

        Assert.Single(fixture.Sender.Sent);
        Assert.Single(await ProcessedAsync());
    }

    [Fact]
    public async Task AFailedSendRecordsNothingSoTheNextDeliverySends()
    {
        var message = NotificationsFixture.Message(NotificationsFixture.Order());
        fixture.Sender.FailNextSend = true;

        // The consumer turns the exception into a reject with requeue, which RabbitMQ counts and delivers again
        await Assert.ThrowsAsync<IOException>(() => HandleAsync(message));
        Assert.Empty(await ProcessedAsync());

        Assert.Equal(MessageOutcome.Done, await HandleAsync(message));
        Assert.Single(fixture.Sender.Sent);
        Assert.Single(await ProcessedAsync());
    }

    [Fact]
    public async Task MessagesThatCanNeverBeSentAreRejectedSoTheyGoToTheDlq()
    {
        var unreadable = new ReceivedMessage(Guid.CreateVersion7(), "order.placed", Encoding.UTF8.GetBytes("not json"), 0);
        var noEmail = NotificationsFixture.Message(NotificationsFixture.Order(email: null));
        var noMessageId = NotificationsFixture.Message(NotificationsFixture.Order()) with { MessageId = null };

        Assert.Equal(MessageOutcome.Reject, await HandleAsync(unreadable));
        Assert.Equal(MessageOutcome.Reject, await HandleAsync(noEmail));
        Assert.Equal(MessageOutcome.Reject, await HandleAsync(noMessageId));

        Assert.Empty(fixture.Sender.Sent);
        Assert.Empty(await ProcessedAsync());
    }

    [Fact]
    public void TheEmailListsEachLineAndTheTotal()
    {
        var order = NotificationsFixture.Order();

        var email = OrderConfirmationEmail.From(order, "ada@shop.test");

        Assert.Contains($"Order: {order.OrderId}", email.TextBody);
        Assert.Contains("Placed: 2026-11-15 09:30 UTC", email.TextBody);
        Assert.Contains("2 x Cotton T-Shirt at 19.99 = 39.98", email.TextBody);
        Assert.Contains("1 x Ceramic Mug at 19.99 = 19.99", email.TextBody);
        Assert.Contains("Total: 59.97", email.TextBody);
    }

    private async Task<MessageOutcome> HandleAsync(ReceivedMessage message)
    {
        // A new scope per delivery, as the consumer does
        await using var scope = fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrderConfirmationHandler>().HandleAsync(message, CancellationToken.None);
    }

    private async Task<List<Processing.ProcessedMessage>> ProcessedAsync()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().ProcessedMessages.AsNoTracking().ToListAsync();
    }
}
