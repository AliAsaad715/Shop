using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shop.Messaging.Consuming;
using Shop.Modules.Notifications.Email;
using Shop.Modules.Notifications.Processing;
using Shop.Modules.Orders.Contracts;

namespace Shop.Modules.Notifications.OrderConfirmation;

/// <summary>
/// Sends the confirmation email for each <c>order.placed</c> message, once. The message ID is recorded after the
/// email goes out: a crash between the two costs one duplicate email, never a lost one, because exactly once can't
/// stretch across a mail server.
/// </summary>
internal sealed class OrderConfirmationHandler(
    ProcessedMessages processed, IEmailSender sender, ILogger<OrderConfirmationHandler> logger) : IMessageHandler
{
    // camelCase, as the Orders outbox writes it
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<MessageOutcome> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        // Retrying can't fix any of these, so they go straight to the dead-letter queue for someone to look at
        if (message.MessageId is not { } messageId)
        {
            logger.LogWarning("An order.placed message has no message ID, so a repeat couldn't be told apart");
            return MessageOutcome.Reject;
        }

        if (Read(message) is not { } order)
        {
            logger.LogWarning("Message {MessageId} isn't a readable order.placed event", messageId);
            return MessageOutcome.Reject;
        }

        if (string.IsNullOrWhiteSpace(order.CustomerEmail))
        {
            logger.LogWarning("Order {OrderId} has no customer email to confirm it to", order.OrderId);
            return MessageOutcome.Reject;
        }

        if (await processed.ContainsAsync(messageId, cancellationToken))
        {
            logger.LogInformation("Message {MessageId} was handled before; acknowledging it without sending again", messageId);
            return MessageOutcome.Done;
        }

        await sender.SendAsync(OrderConfirmationEmail.From(order, order.CustomerEmail), cancellationToken);
        await processed.RecordAsync(messageId, message.Type, cancellationToken);
        logger.LogInformation("Sent the confirmation for order {OrderId}", order.OrderId);
        return MessageOutcome.Done;
    }

    private static OrderPlacedEvent? Read(ReceivedMessage message)
    {
        try
        {
            return JsonSerializer.Deserialize<OrderPlacedEvent>(message.Body.Span, Json) is { Lines: not null } order ? order : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
