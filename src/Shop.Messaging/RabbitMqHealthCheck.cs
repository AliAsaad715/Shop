using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Shop.Messaging;

/// <summary>
/// Reports whether the host's RabbitMQ connection is open. It reads the connection's state rather than connecting,
/// so a health probe never waits on a missing broker. Each host picks the failure status when it registers it.
/// </summary>
internal sealed class RabbitMqHealthCheck(RabbitMqConnection connection) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(connection.IsOpen
            ? HealthCheckResult.Healthy("Connected to RabbitMQ.")
            : new HealthCheckResult(context.Registration.FailureStatus, "Not connected to RabbitMQ."));
}
