using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Shop.Messaging;

/// <summary>
/// The host's one long-lived connection to RabbitMQ. Connections are expensive, so every channel in the process
/// comes from this one. The first successful <see cref="GetAsync"/> connects and declares <see cref="ShopTopology"/>;
/// from then on the client reconnects by itself after a network failure or a broker restart, and declares the
/// topology again on the new connection.
/// </summary>
public sealed class RabbitMqConnection : IAsyncDisposable
{
    private readonly ConnectionFactory _factory;
    private readonly ILogger<RabbitMqConnection> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IConnection? _connection;
    private bool _disposed;

    public RabbitMqConnection(RabbitMqOptions options, string clientName, ILogger<RabbitMqConnection> logger)
    {
        _logger = logger;
        _factory = new ConnectionFactory
        {
            HostName = options.Host,
            Port = options.Port,
            VirtualHost = options.VirtualHost,
            UserName = options.UserName,
            Password = options.Password,
            // The connection's name in the management UI, so each host is easy to spot
            ClientProvidedName = clientName,
            RequestedConnectionTimeout = options.ConnectTimeout,
            // Both default to true; set here because the comment above depends on them
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
        };
    }

    /// <summary>
    /// True while the connection is open. False before the first connect and while the client is reconnecting.
    /// </summary>
    public bool IsOpen => _connection?.IsOpen == true;

    /// <summary>
    /// Returns the connection, connecting and declaring the topology first if this host has never connected.
    /// Throws when RabbitMQ can't be reached; the next call tries again.
    /// </summary>
    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        // Once connected, the client keeps this object and reconnects it in place, so it's never replaced
        if (_connection is { } connected)
        {
            return connected;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is { } raced)
            {
                return raced;
            }

            var connection = await _factory.CreateConnectionAsync(cancellationToken);
            try
            {
                await ShopTopology.DeclareAsync(connection, cancellationToken);
            }
            catch
            {
                // A connection without the topology would let a publish reach no queue
                await connection.DisposeAsync();
                throw;
            }

            _logger.LogInformation(
                "Connected to RabbitMQ at {Host}:{Port} as {ClientName}; the topology is declared",
                _factory.HostName, _factory.Port, _factory.ClientProvidedName);
            _connection = connection;
            return connection;
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
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_connection is { } connection)
            {
                _connection = null;
                try
                {
                    // A clean close, so RabbitMQ returns this host's unacknowledged messages at once
                    await connection.CloseAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception error)
                {
                    // Already closed, or RabbitMQ is gone: shutting down must not fail over it
                    _logger.LogDebug(error, "Closing the RabbitMQ connection failed");
                }

                await connection.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
