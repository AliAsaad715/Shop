namespace Shop.Messaging;

/// <summary>
/// Where RabbitMQ is and how long to wait for it, bound from the <c>RabbitMq</c> section. Separate settings rather
/// than an amqp:// URI, so a password needs no URL encoding.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";

    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Compose sets <c>rabbitmq</c>; a local <c>dotnet run</c> uses <c>localhost</c>.</summary>
    public string Host { get; init; } = string.Empty;

    public int Port { get; init; } = 5672;

    public string VirtualHost { get; init; } = "/";

    public string UserName { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    /// <summary>How long opening the TCP connection and the AMQP handshake may take.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a batch may wait for RabbitMQ's publisher confirms. A message not confirmed in time counts as not
    /// published, so its sender tries again later.
    /// </summary>
    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public static bool HasServer(RabbitMqOptions options) =>
        !string.IsNullOrWhiteSpace(options.Host) && options.Port is > 0 and <= 65535 && !string.IsNullOrEmpty(options.VirtualHost);

    public static bool HasCredentials(RabbitMqOptions options) =>
        !string.IsNullOrWhiteSpace(options.UserName) && !string.IsNullOrEmpty(options.Password);

    public static bool HasValidTimeouts(RabbitMqOptions options) =>
        new[] { options.ConnectTimeout, options.ConfirmTimeout }.All(timeout => timeout > TimeSpan.Zero && timeout <= MaxTimeout);
}
