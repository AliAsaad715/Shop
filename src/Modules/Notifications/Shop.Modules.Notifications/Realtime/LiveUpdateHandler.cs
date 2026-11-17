using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Shop.Messaging;
using Shop.Messaging.Consuming;
using Shop.Modules.Notifications.Contracts.Realtime;
using Shop.Modules.Orders.Contracts;

namespace Shop.Modules.Notifications.Realtime;

internal enum LiveUpdateOutcome { Done, Reject, Restart }

internal sealed class LiveUpdateHandler(
    IHubContext<OrdersHub> orders, IHubContext<AdminHub> admins,
    RecentRealtimeMessages processed, ILogger<LiveUpdateHandler> logger)
{
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<LiveUpdateOutcome> HandleAsync(ReceivedMessage message, CancellationToken cancellationToken)
    {
        if (message.Type is not (ShopTopology.RoutingKeys.OrderPlaced or ShopTopology.RoutingKeys.OrderStatusChanged))
        {
            logger.LogDebug("Ignoring unsupported live event {EventType}", message.Type);
            return LiveUpdateOutcome.Done;
        }

        if (message.MessageId is not { } messageId || messageId == Guid.Empty)
        {
            logger.LogWarning("Discarding live event {EventType} without a valid message ID", message.Type);
            return LiveUpdateOutcome.Reject;
        }

        (IClientProxy Recipient, string Method, object Payload)? update;
        try
        {
            update = Read(message, messageId);
        }
        catch (Exception error) when (error is JsonException or OverflowException)
        {
            update = null;
        }

        if (update is not { } delivery)
        {
            logger.LogWarning("Discarding invalid live event {MessageId} of type {EventType}", messageId, message.Type);
            return LiveUpdateOutcome.Reject;
        }

        if (processed.Contains(messageId)) return LiveUpdateOutcome.Done;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SendTimeout);
            try
            {
                await delivery.Recipient.SendCoreAsync(delivery.Method, [delivery.Payload], timeout.Token).WaitAsync(timeout.Token);
                processed.Record(messageId);
                return LiveUpdateOutcome.Done;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                logger.LogWarning("Sending live event {MessageId} failed on attempt {Attempt} ({ErrorType})",
                    messageId, attempt, error.GetType().Name);
            }

            if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
        }

        // Reopen the temporary subscription and request a snapshot, instead of endlessly requeueing a live update.
        return LiveUpdateOutcome.Restart;
    }

    public async Task ResynchronizeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SendTimeout);
        await Task.WhenAll(
            orders.Clients.All.SendCoreAsync(RealtimeEvents.ResyncRequired, [], timeout.Token),
            admins.Clients.All.SendCoreAsync(RealtimeEvents.ResyncRequired, [], timeout.Token)).WaitAsync(timeout.Token);
    }

    private (IClientProxy, string, object)? Read(ReceivedMessage message, Guid messageId)
    {
        if (message.Type == ShopTopology.RoutingKeys.OrderPlaced)
        {
            var order = JsonSerializer.Deserialize<OrderPlacedEvent>(message.Body.Span, Json);
            if (order is null || order.OrderId == Guid.Empty || order.CustomerId == Guid.Empty || order.Version < 0
                || string.IsNullOrWhiteSpace(order.Status) || order.PlacedAt == default || order.Total < 0
                || order.Lines is not { Count: > 0 } || order.Lines.Any(line => line is null || line.Quantity <= 0)) return null;

            return (admins.Clients.Group(AdminHub.GroupName), RealtimeEvents.OrderPlaced,
                new OrderPlacedNotification(messageId, order.OrderId, order.Version, order.CustomerId, order.Status,
                    order.Total, order.Lines.Sum(line => line.Quantity), order.PlacedAt));
        }

        var change = JsonSerializer.Deserialize<OrderStatusChangedEvent>(message.Body.Span, Json);
        if (change is null || change.OrderId == Guid.Empty || change.CustomerId == Guid.Empty || change.Version <= 0
            || string.IsNullOrWhiteSpace(change.FromStatus) || string.IsNullOrWhiteSpace(change.Status) || change.ChangedAt == default) return null;

        return (orders.Clients.User(change.CustomerId.ToString("D")), RealtimeEvents.OrderStatusChanged,
            new OrderStatusChangedNotification(messageId, change.OrderId, change.Version, change.FromStatus, change.Status, change.ChangedAt));
    }
}
