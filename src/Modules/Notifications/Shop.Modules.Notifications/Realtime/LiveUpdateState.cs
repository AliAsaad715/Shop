using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Shop.Modules.Notifications.Realtime;

internal sealed class LiveUpdateState
{
    private volatile bool _ready;
    private string? _queueName;

    public bool IsReady => _ready;
    public string? QueueName => Volatile.Read(ref _queueName);

    public void SetQueue(string queueName) => Volatile.Write(ref _queueName, queueName);
    public void Ready() => _ready = true;
    public void Disconnected() => _ready = false;
}

internal sealed class LiveUpdateHealthCheck(LiveUpdateState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.IsReady
            ? HealthCheckResult.Healthy("The live-update subscription is ready.")
            : HealthCheckResult.Degraded("The live-update subscription is not ready."));
}
