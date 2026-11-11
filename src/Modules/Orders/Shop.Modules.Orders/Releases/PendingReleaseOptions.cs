namespace Shop.Modules.Orders.Releases;

internal sealed class PendingReleaseOptions
{
    public const string Section = "Orders:PendingReleases";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}
