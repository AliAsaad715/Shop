using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Shop.Messaging.Consuming;

/// <summary>
/// Consumes one queue with manual acknowledgements, one message at a time, at most <c>prefetch</c> unacknowledged.
/// A message is acknowledged only after its handler finishes, so a crash at any point returns it to the queue.
/// </summary>
/// <remarks>
/// Stopping is graceful: no new messages are taken, the current one finishes (up to <see cref="DrainTimeout"/>), and
/// messages that were prefetched but never started go back with <c>basic.nack</c>. Since RabbitMQ 4.3 a nack doesn't
/// count as a failed delivery, so a restart never uses up a message's retries; a reject, used when a handler fails,
/// does.
/// </remarks>
internal sealed class QueueConsumer<THandler>(
    string queue, ushort prefetch, RabbitMqConnection connection, IServiceScopeFactory scopes, ILogger<QueueConsumer<THandler>> logger)
    : BackgroundService
    where THandler : class, IMessageHandler
{
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _outOfTime = new();

    // Set when the client has dispatched the consumer's cancel-ok. Deliveries are dispatched one at a time in the order
    // they arrived, and cancel-ok arrives after every delivery RabbitMQ sent before it: once this is set, the message in
    // progress has finished and every prefetched one has been nacked
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopping;
    private IChannel? _channel;
    private string? _consumerTag;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = FirstRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeUntilLostAsync(stoppingToken);
                delay = FirstRetryDelay;
                logger.LogWarning("The consumer of {Queue} lost its channel; starting it again", queue);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning("Can't consume {Queue} yet ({Error}); trying again in {Delay}", queue, error.Message, delay);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        var cancelled = false;
        if (_channel is { IsOpen: true } channel && _consumerTag is { } consumerTag)
        {
            try
            {
                // No new deliveries; ones already sent to this client still arrive, and are nacked
                await channel.BasicCancelAsync(consumerTag, noWait: false, cancellationToken);
                cancelled = true;
                logger.LogInformation("Stopped taking messages from {Queue}; finishing the one in progress", queue);
            }
            catch (Exception error)
            {
                logger.LogDebug(error, "Cancelling the consumer of {Queue} failed", queue);
            }
        }

        // Without a consumer to cancel (never connected, or the channel is gone) there is nothing to finish
        if (cancelled)
        {
            try
            {
                await _drained.Task.WaitAsync(DrainTimeout, cancellationToken);
            }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException)
            {
                // The handler is told to give up; its message isn't acknowledged, so RabbitMQ delivers it again
                logger.LogWarning("The message being handled from {Queue} didn't finish in time; stopping anyway", queue);
                await _outOfTime.CancelAsync();
            }
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _outOfTime.Dispose();
        base.Dispose();
    }

    // Returns when the channel is gone for good; a dropped connection doesn't count, as the client brings the
    // channel and this consumer back by itself
    private async Task ConsumeUntilLostAsync(CancellationToken stoppingToken)
    {
        var current = await connection.GetAsync(stoppingToken);

        // Again on every start, in case someone deleted the queue: consuming a missing queue fails
        await ShopTopology.DeclareAsync(current, stoppingToken);

        // One delivery at a time, in order: the graceful stop relies on it (see _drained)
        await using var channel = await current.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false, consumerDispatchConcurrency: 1),
            stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: prefetch, global: false, stoppingToken);

        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, _) =>
        {
            // With the connection still open, RabbitMQ closed just this channel, over an error such as an ack that
            // came after the consumer timeout. Recovery only restores channels a dropped connection took with it
            if (current.IsOpen && !_stopping)
            {
                lost.TrySetResult();
            }

            return Task.CompletedTask;
        };

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += OnReceivedAsync;
        consumer.UnregisteredAsync += (_, _) =>
        {
            if (_stopping)
            {
                // Our own cancel: every delivery before it has been answered
                _drained.TrySetResult();
            }
            else
            {
                // RabbitMQ cancelled the consumer, as it does when the queue is deleted
                lost.TrySetResult();
            }

            return Task.CompletedTask;
        };

        _channel = channel;
        try
        {
            _consumerTag = await channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken);
            logger.LogInformation("Consuming {Queue}, {Prefetch} messages at most in flight", queue, prefetch);
            await lost.Task.WaitAsync(stoppingToken);
        }
        finally
        {
            _channel = null;
            _consumerTag = null;
        }
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs delivery)
    {
        var channel = ((AsyncEventingBasicConsumer)sender).Channel;
        try
        {
            if (_stopping)
            {
                // Prefetched but never started: back to the queue without counting as a failed delivery
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
                return;
            }

            switch (await HandleAsync(delivery))
            {
                case MessageOutcome.Done:
                    await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
                    break;
                case MessageOutcome.Retry:
                    // Reject, not nack: only a reject counts toward the queue's delivery limit
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: true);
                    break;
                default:
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false);
                    break;
            }
        }
        catch (Exception error) when (error is AlreadyClosedException or OperationInterruptedException or IOException)
        {
            // Unacknowledged, so RabbitMQ delivers it again once the channel is back
            logger.LogWarning("The channel closed before the answer for a message from {Queue}; it will be delivered again", queue);
        }
    }

    private async Task<MessageOutcome> HandleAsync(BasicDeliverEventArgs delivery)
    {
        var properties = delivery.BasicProperties;
        var message = new ReceivedMessage(
            Guid.TryParse(properties.MessageId, out var id) ? id : null,
            delivery.RoutingKey,
            delivery.Body.ToArray(),
            FailedDeliveries(properties));

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<THandler>().HandleAsync(message, _outOfTime.Token);
            if (outcome != MessageOutcome.Done)
            {
                logger.LogWarning("Message {MessageId} from {Queue}: {Outcome}", message.MessageId, queue, outcome);
            }

            return outcome;
        }
        catch (Exception error)
        {
            logger.LogError(error, "Handling message {MessageId} from {Queue} failed; RabbitMQ delivers it again", message.MessageId, queue);
            return MessageOutcome.Retry;
        }
    }

    private static int FailedDeliveries(IReadOnlyBasicProperties properties) =>
        properties.Headers?.TryGetValue("x-delivery-count", out var value) == true
            ? value switch
            {
                long count => (int)count,
                int count => count,
                byte[] text when int.TryParse(Encoding.UTF8.GetString(text), out var count) => count,
                _ => 0,
            }
            : 0;
}
