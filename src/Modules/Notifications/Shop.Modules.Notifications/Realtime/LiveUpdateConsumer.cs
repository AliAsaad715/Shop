using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using Shop.Messaging;
using Shop.Messaging.Consuming;

namespace Shop.Modules.Notifications.Realtime;

/// <summary>
/// One temporary subscription per API process. Connection loss is recovered by the RabbitMQ client; a cancelled
/// consumer or a failed channel is replaced here. Reopening always asks local pages to reload their state.
/// </summary>
internal sealed class LiveUpdateConsumer(
    RabbitMqConnection connection, LiveUpdateHandler handler, LiveUpdateState state, ILogger<LiveUpdateConsumer> logger)
    : BackgroundService
{
    internal const ushort Prefetch = 5;
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _outOfTime = new();
    private TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning("Can't start the live-update subscription ({ErrorType}); retrying in {Delay}",
                    error.GetType().Name, delay);
            }
            finally
            {
                state.Disconnected();
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        state.Disconnected();
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drain.CancelAfter(DrainTimeout);
        if (_channel is { IsOpen: true } channel && _consumerTag is { } tag)
        {
            try
            {
                await channel.BasicCancelAsync(tag, noWait: false, drain.Token);
                await _drained.Task.WaitAsync(drain.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("The live-update consumer did not drain in time; stopping it");
                await _outOfTime.CancelAsync();
            }
            catch (Exception error)
            {
                logger.LogDebug("Cancelling the live-update consumer failed ({ErrorType})", error.GetType().Name);
            }
        }
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _outOfTime.Dispose();
        base.Dispose();
    }

    private async Task ConsumeUntilLostAsync(CancellationToken stoppingToken)
    {
        var current = await connection.GetAsync(stoppingToken);
        await using var channel = await current.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false,
                consumerDispatchConcurrency: 1), stoppingToken);
        await channel.BasicQosAsync(0, Prefetch, global: false, stoppingToken);
        var queue = await ShopTopology.DeclareLiveQueueAsync(channel, stoppingToken);
        state.SetQueue(queue);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _drained = drained;

        Task OnQueueNameChanged(object? sender, QueueNameChangedAfterRecoveryEventArgs args)
        {
            if (args.NameBefore == queue)
            {
                queue = args.NameAfter;
                state.SetQueue(queue);
            }
            return Task.CompletedTask;
        }
        current.QueueNameChangedAfterRecoveryAsync += OnQueueNameChanged;

        channel.ChannelShutdownAsync += (_, _) =>
        {
            state.Disconnected();
            // A network failure recovers this channel and consumer in place. Only replace a channel lost on its own.
            if (current.IsOpen && !_stopping) lost.TrySetResult();
            return Task.CompletedTask;
        };

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.RegisteredAsync += async (_, _) =>
        {
            if (_stopping) return;
            state.Ready();
            logger.LogInformation("Live-update subscription ready on {Queue}", queue);
            try
            {
                // Also runs after automatic recovery: SignalR itself may have stayed connected throughout the gap.
                await handler.ResynchronizeAsync(_outOfTime.Token);
            }
            catch (Exception error)
            {
                logger.LogWarning("Requesting a live-state reload failed ({ErrorType}); replacing the subscription", error.GetType().Name);
                state.Disconnected();
                lost.TrySetResult();
            }
        };
        consumer.ReceivedAsync += (_, delivery) => ReceiveAsync(channel, lost, delivery);
        consumer.UnregisteredAsync += (_, _) =>
        {
            state.Disconnected();
            if (_stopping) drained.TrySetResult();
            // A network failure unregisters the consumer too, and then the client restores it in place with its queue.
            // Replacing it here as well left that restored queue without a consumer, keeping a copy of every event.
            else if (current.IsOpen) lost.TrySetResult();
            return Task.CompletedTask;
        };

        // A client-generated tag is stable across recovery; a server-generated queue name is not.
        var tag = $"shop-live-{Guid.NewGuid():N}";
        _channel = channel;
        _consumerTag = tag;
        try
        {
            await channel.BasicConsumeAsync(queue, autoAck: false, tag, consumer, stoppingToken);
            await lost.Task.WaitAsync(stoppingToken);
        }
        finally
        {
            state.Disconnected();
            current.QueueNameChangedAfterRecoveryAsync -= OnQueueNameChanged;
            _channel = null;
            _consumerTag = null;
        }
    }

    private async Task ReceiveAsync(IChannel channel, TaskCompletionSource lost, BasicDeliverEventArgs delivery)
    {
        try
        {
            if (_stopping || lost.Task.IsCompleted)
            {
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
                return;
            }

            var message = new ReceivedMessage(
                Guid.TryParse(delivery.BasicProperties.MessageId, out var id) ? id : null,
                delivery.RoutingKey, delivery.Body.ToArray(), 0);
            switch (await handler.HandleAsync(message, _outOfTime.Token))
            {
                case LiveUpdateOutcome.Done:
                    await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
                    break;
                case LiveUpdateOutcome.Reject:
                    // Live queues deliberately have no DLQ: persistent email delivery has a separate subscription.
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false);
                    break;
                case LiveUpdateOutcome.Restart:
                    state.Disconnected();
                    lost.TrySetResult();
                    break;
            }
        }
        catch (OperationCanceledException) when (_outOfTime.IsCancellationRequested)
        {
            // Shutdown's drain timeout: leave it unacknowledged and close the temporary subscription.
        }
        catch (Exception error) when (error is AlreadyClosedException or OperationInterruptedException or IOException)
        {
            state.Disconnected();
            if (connection.IsOpen && !_stopping) lost.TrySetResult();
            logger.LogWarning("The live-update channel closed before acknowledging a delivery");
        }
        catch (Exception error)
        {
            logger.LogWarning("Processing a live update failed ({ErrorType}); replacing the subscription", error.GetType().Name);
            state.Disconnected();
            lost.TrySetResult();
        }
    }
}
