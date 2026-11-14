using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shop.Messaging;

/// <summary>
/// Connects at startup, so the topology exists before the first publish and the health check is accurate. It
/// retries with backoff and never blocks startup: the API takes orders while RabbitMQ is down, and their events wait
/// in the outbox (R4-02). After the first connect the client reconnects by itself, so this service ends.
/// </summary>
internal sealed class RabbitMqConnector(RabbitMqConnection connection, ILogger<RabbitMqConnector> logger) : BackgroundService
{
    internal static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = FirstRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await connection.GetAsync(stoppingToken);
                return;
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Can't connect to RabbitMQ yet ({Error}); trying again in {Delay}", error.Message, delay);
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
}
