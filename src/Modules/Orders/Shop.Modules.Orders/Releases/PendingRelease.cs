namespace Shop.Modules.Orders.Releases;

internal sealed class PendingRelease
{
    public const int MaxErrorLength = 1000;

    private PendingRelease() { }

    public PendingRelease(Guid orderId, string error, DateTimeOffset now)
    {
        OrderId = orderId;
        QueuedAt = now;
        NextAttemptAt = now;
        LastError = Truncate(error);
    }

    public Guid OrderId { get; private set; }
    public DateTimeOffset QueuedAt { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public string LastError { get; private set; } = string.Empty;

    public void RetryFailed(string error, DateTimeOffset now)
    {
        Attempts++;
        LastError = Truncate(error);
        NextAttemptAt = now + RetryDelay(Attempts);
    }

    public static TimeSpan RetryDelay(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 6))));

    private static string Truncate(string error) => error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
}
