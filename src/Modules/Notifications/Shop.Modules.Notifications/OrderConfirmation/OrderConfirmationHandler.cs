using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
/// <remarks>
/// A failed send is sorted by <see cref="EmailFailures"/> (task R4-05). A temporary failure is tried again here, with
/// a growing wait, and only then handed back to RabbitMQ; a permanent one goes straight to the dead-letter queue; one
/// caused by our own settings goes back at once.
/// </remarks>
internal sealed class OrderConfirmationHandler(
    ProcessedMessages processed, IEmailSender sender, IOptions<EmailOptions> options, ILogger<OrderConfirmationHandler> logger)
    : IMessageHandler
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

        var sent = await SendAsync(OrderConfirmationEmail.From(order, order.CustomerEmail, messageId), order.OrderId, message, cancellationToken);
        if (sent != MessageOutcome.Done)
        {
            return sent;
        }

        await processed.RecordAsync(messageId, message.Type, cancellationToken);
        logger.LogInformation("Sent the confirmation for order {OrderId}", order.OrderId);
        return MessageOutcome.Done;
    }

    private async Task<MessageOutcome> SendAsync(EmailMessage email, Guid orderId, ReceivedMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var delay = settings.RetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await sender.SendAsync(email, cancellationToken);
                return MessageOutcome.Done;
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                switch (EmailFailures.Classify(error))
                {
                    case EmailFailure.Permanent:
                        logger.LogWarning("The confirmation for order {OrderId} can never be sent ({Reason}); it goes to the dead-letter queue",
                            orderId, error.Message);
                        return MessageOutcome.Reject;

                    case EmailFailure.Settings:
                        logger.LogError("The email settings are wrong ({Reason}); check the Email section. The confirmation for order " +
                            "{OrderId} goes back to RabbitMQ ({Failed} failed deliveries before this one)", error.Message, orderId, message.FailedDeliveries);
                        return MessageOutcome.Retry;

                    case EmailFailure.Temporary when attempt < settings.SendAttempts:
                        var wait = Jitter(delay);
                        logger.LogWarning("Sending the confirmation for order {OrderId} failed ({Reason}); try {Attempt} of {Attempts} in {Wait}",
                            orderId, error.Message, attempt + 1, settings.SendAttempts, wait);
                        await Task.Delay(wait, cancellationToken);
                        delay *= 2;
                        break;

                    default:
                        logger.LogWarning("Sending the confirmation for order {OrderId} failed {Attempts} times ({Reason}); it goes back to " +
                            "RabbitMQ ({Failed} failed deliveries before this one)", orderId, attempt, error.Message, message.FailedDeliveries);
                        return MessageOutcome.Retry;
                }
            }
        }
    }

    // Up to a fifth either way, so workers that failed together don't all try again at the same moment
    private static TimeSpan Jitter(TimeSpan delay) => delay * (0.8 + (Random.Shared.NextDouble() * 0.4));

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
