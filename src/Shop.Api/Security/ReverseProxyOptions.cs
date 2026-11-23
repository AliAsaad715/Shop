using System.Net;

namespace Shop.Api.Security;

internal sealed class ReverseProxyOptions
{
    public const string Section = "ReverseProxy";

    public bool Enabled { get; init; }
    public string[] TrustedNetworks { get; init; } = [];

    public static bool HasExplicitNetworks(ReverseProxyOptions options) => !options.Enabled
        || (options.TrustedNetworks is { Length: > 0 } && options.TrustedNetworks.All(network =>
            IPNetwork.TryParse(network, out var parsed) && parsed.PrefixLength > 0));
}
