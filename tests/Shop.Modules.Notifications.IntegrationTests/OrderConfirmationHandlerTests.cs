using System.Net.Sockets;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
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
        Assert.Equal(message.MessageId, email.Id);
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
    public async Task ATemporaryFailureIsTriedAgainInTheWorkerWithoutUsingADelivery()
    {
        var message = NotificationsFixture.Message(NotificationsFixture.Order());
        fixture.Sender.FailNext(new SocketException((int)SocketError.ConnectionRefused));

        Assert.Equal(MessageOutcome.Done, await HandleAsync(message));

        Assert.Equal(2, fixture.Sender.Attempts);
        Assert.Single(fixture.Sender.Sent);
        Assert.Single(await ProcessedAsync());
    }

    [Fact]
    public async Task WhenEveryTryFailsTheMessageGoesBackToRabbitMqAndNothingIsRecorded()
    {
        var message = NotificationsFixture.Message(NotificationsFixture.Order());
        fixture.Sender.FailNext(new IOException("Timed out"), new IOException("Timed out"), new IOException("Timed out"));

        // Retry: the consumer rejects with requeue, which RabbitMQ counts and delivers again
        Assert.Equal(MessageOutcome.Retry, await HandleAsync(message));
        Assert.Equal(3, fixture.Sender.Attempts);
        Assert.Empty(await ProcessedAsync());

        // The next delivery, once the server is back
        Assert.Equal(MessageOutcome.Done, await HandleAsync(message));
        Assert.Single(fixture.Sender.Sent);
        Assert.Single(await ProcessedAsync());
    }

    [Fact]
    public async Task ARefusedRecipientGoesStraightToTheDlqAfterOneTry()
    {
        var message = NotificationsFixture.Message(NotificationsFixture.Order());
        fixture.Sender.FailNext(new SmtpCommandException(
            SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, MailboxAddress.Parse("ada@shop.test"), "No such user"));

        Assert.Equal(MessageOutcome.Reject, await HandleAsync(message));

        Assert.Equal(1, fixture.Sender.Attempts);
        Assert.Empty(await ProcessedAsync());
    }

    [Fact]
    public async Task AWrongPasswordGoesBackAtOnceWithoutTryingAgainInTheWorker()
    {
        var message = NotificationsFixture.Message(NotificationsFixture.Order());
        fixture.Sender.FailNext(new AuthenticationException("535 5.7.8 Username and Password not accepted"));

        // Counted by RabbitMQ, so after five deliveries it waits in the DLQ until the password is fixed.
        Assert.Equal(MessageOutcome.Retry, await HandleAsync(message));

        Assert.Equal(1, fixture.Sender.Attempts);
        Assert.Empty(await ProcessedAsync());
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

        var email = OrderConfirmationEmail.From(order, "ada@shop.test", Guid.CreateVersion7());

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
