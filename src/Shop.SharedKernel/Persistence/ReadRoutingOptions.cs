namespace Shop.SharedKernel.Persistence;

public sealed class ReadRoutingOptions
{
    public const string Section = "ReadRouting";
    public bool Enabled { get; set; }
    public TimeSpan PrimaryWindow { get; set; } = TimeSpan.FromSeconds(10);
}

public enum DatabaseReadRoute { Primary, Replica }
