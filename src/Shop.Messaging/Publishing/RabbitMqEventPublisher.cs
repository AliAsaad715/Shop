using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Shop.Messaging.Publishing;

/// <summary>
/// Publishes on one channel with publisher confirms: RabbitMQ answers every message with an ack once it has stored it
/// in all the queues it routes to, or a nack when it can't. Event types that must reach a queue
/// (<see cref="ShopTopology.RequiresQueue"/>) are mandatory, so one that no queue takes comes back as a return
/// instead of vanishing.
/// </summary>
internal sealed class RabbitMqEventPublisher(
    RabbitMqConnection connection, IOptions<RabbitMqOptions> options, ILogger<RabbitMqEventPublisher> logger)
    : IEventPublisher, IAsyncDisposable
{
    private const string JsonContentType = "application/json";

    // One batch at a time: they share the channel, and the timeout must not cut off another caller's batch
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;
    private bool _disposed;

    public async Task<IReadOnlyList<PublishResult>> PublishAsync(
        IReadOnlyList<OutgoingMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            IChannel channel;
            try
            {
                channel = await GetChannelAsync(cancellationToken);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(error, "Couldn't publish {Count} messages: RabbitMQ can't be reached", messages.Count);
                return [.. messages.Select(message => PublishResult.Failed(message.MessageId, $"RabbitMQ can't be reached: {error.Message}"))];
            }

            using var confirmWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            confirmWindow.CancelAfter(options.Value.ConfirmTimeout);

            // Started in list order without waiting in between: the client sends them in that order, and the confirms
            // for the whole batch come back in about one round trip rather than one per message
            var publishes = new Task<PublishResult>[messages.Count];
            for (var i = 0; i < messages.Count; i++)
            {
                publishes[i] = PublishOneAsync(channel, messages[i], confirmWindow.Token);
            }

            var results = await Task.WhenAll(publishes);

            // A publish cut off part way may leave the channel unusable, so the next batch gets a fresh one
            if (confirmWindow.IsCancellationRequested || !channel.IsOpen)
            {
                await DiscardChannelAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            LogFailures(results);
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            await DiscardChannelAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PublishResult> PublishOneAsync(IChannel channel, OutgoingMessage message, CancellationToken cancellationToken)
    {
        var properties = new BasicProperties
        {
            MessageId = message.MessageId.ToString(),
            Type = message.RoutingKey,
            ContentType = JsonContentType,
            // Written to disk, so the message survives a broker restart
            DeliveryMode = DeliveryModes.Persistent,
            Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
        };

        try
        {
            // With confirm tracking on, this completes only when RabbitMQ acks the message
            await channel.BasicPublishAsync(
                ShopTopology.EventsExchange, message.RoutingKey, mandatory: ShopTopology.RequiresQueue(message.RoutingKey),
                properties, message.Body, cancellationToken);
            return PublishResult.Success(message.MessageId);
        }
        catch (PublishException error) when (error.IsReturn)
        {
            return PublishResult.Failed(message.MessageId, $"No queue is bound for '{message.RoutingKey}', so RabbitMQ returned it");
        }
        catch (PublishException)
        {
            return PublishResult.Failed(message.MessageId, "RabbitMQ refused it (nack)");
        }
        catch (OperationCanceledException)
        {
            return PublishResult.Failed(message.MessageId, $"No confirm within {options.Value.ConfirmTimeout.TotalSeconds:0.#} s");
        }
        catch (Exception error) when (error is RabbitMQClientException or IOException)
        {
            // Mostly AlreadyClosedException: the connection dropped, or RabbitMQ closed the channel over an error
            return PublishResult.Failed(message.MessageId, $"The channel closed: {error.Message}");
        }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        await DiscardChannelAsync();
        var current = await connection.GetAsync(cancellationToken);

        // No rate limiter: callers already bound their batches (the outbox relay sends 50 rows), so its throttling
        // would only slow a batch down
        _channel = await current.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true,
                outstandingPublisherConfirmationsRateLimiter: null),
            cancellationToken);
        return _channel;
    }

    private async Task DiscardChannelAsync()
    {
        if (_channel is not { } channel)
        {
            return;
        }

        _channel = null;
        try
        {
            await channel.DisposeAsync();
        }
        catch (Exception error)
        {
            logger.LogDebug(error, "Disposing a RabbitMQ publish channel failed");
        }
    }

    private void LogFailures(PublishResult[] results)
    {
        var failed = results.Where(result => !result.Confirmed).ToList();
        if (failed.Count > 0)
        {
            logger.LogWarning(
                "RabbitMQ confirmed {Confirmed} of {Total} messages; the first failure: {Failure}",
                results.Length - failed.Count, results.Length, failed[0].Failure);
        }
    }
}
