namespace Shop.Modules.Orders.Outbox;

/// <summary>How the outbox relay runs, bound from <c>Orders:Outbox</c>; the defaults suit production.</summary>
internal sealed class OutboxOptions
{
    public const string Section = "Orders:Outbox";

    /// <summary>
    /// How often the relay looks for rows when nothing wakes it: rows another instance wrote, or after a restart.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How many rows one pass locks and publishes.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>How long a published row stays for inspection before the relay deletes it.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    public static bool IsValid(OutboxOptions options) =>
        options.PollInterval > TimeSpan.Zero && options.BatchSize is > 0 and <= 500 && options.Retention > TimeSpan.Zero;
}
