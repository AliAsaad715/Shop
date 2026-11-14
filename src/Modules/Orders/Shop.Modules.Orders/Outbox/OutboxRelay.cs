using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Shop.Modules.Orders.Outbox;

/// <summary>
/// Publishes outbox rows in the background, so placing an order never waits for RabbitMQ. A save that writes rows
/// wakes it at once; otherwise it polls every <see cref="OutboxOptions.PollInterval"/>. Every API instance runs one,
/// and <c>SKIP LOCKED</c> keeps them from publishing the same row at the same time.
/// </summary>
internal sealed class OutboxRelay(
    IServiceScopeFactory scopes, OutboxSignal signal, IOptions<OutboxOptions> options, TimeProvider time, ILogger<OutboxRelay> logger)
    : BackgroundService
{
    internal static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    internal static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var failedPasses = 0;
        var nextCleanup = time.GetUtcNow();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var pass = await RunPassAsync();
                if (time.GetUtcNow() >= nextCleanup)
                {
                    nextCleanup = time.GetUtcNow() + CleanupInterval;
                    await DeleteOldRowsAsync(settings.Retention, stoppingToken);
                }

                if (pass is not { } done || (done.Taken > 0 && done.Published == 0))
                {
                    // RabbitMQ or the database is down. Wait longer after each failed pass, and ignore wake-ups until
                    // a pass gets through, so a stream of new orders doesn't turn into a stream of failing passes
                    failedPasses++;
                    var delay = RetryDelay(failedPasses);
                    logger.LogWarning("No outbox message could be published; the relay tries again in {Delay}", delay);
                    await Task.Delay(delay, time, stoppingToken);
                    continue;
                }

                failedPasses = 0;
                if (done.Taken == settings.BatchSize)
                {
                    // A full batch: more rows are probably waiting, so go again at once
                    continue;
                }

                await signal.WaitAsync(settings.PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>1 s after the first failed pass, doubling each time, up to 30 s.</summary>
    internal static TimeSpan RetryDelay(int failedPasses) =>
        TimeSpan.FromTicks(Math.Min(FirstRetryDelay.Ticks << Math.Clamp(failedPasses - 1, 0, 10), MaxRetryDelay.Ticks));

    // Null when the pass threw
    private async Task<OutboxPass?> RunPassAsync()
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();

            // Not the stopping token: a pass stopped between RabbitMQ's confirms and the commit would publish its batch
            // again after the restart. The publisher's confirm timeout keeps a pass short, well inside shutdown's limit
            var pass = await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().PublishPendingAsync(CancellationToken.None);
            if (pass.Taken > 0)
            {
                logger.LogDebug("Published {Published} of {Taken} outbox messages", pass.Published, pass.Taken);
            }

            return pass;
        }
        catch (Exception error)
        {
            logger.LogError(error, "An outbox relay pass failed");
            return null;
        }
    }

    private async Task DeleteOldRowsAsync(TimeSpan retention, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var deleted = await scope.ServiceProvider.GetRequiredService<OutboxProcessor>()
                .DeletePublishedBeforeAsync(time.GetUtcNow() - retention, stoppingToken);
            if (deleted > 0)
            {
                logger.LogInformation("Deleted {Deleted} outbox messages published more than {Retention} ago", deleted, retention);
            }
        }
        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
        {
            // Retried at the next cleanup; old rows only take space meanwhile
            logger.LogWarning(error, "Deleting old outbox messages failed");
        }
    }
}
