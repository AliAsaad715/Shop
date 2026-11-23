using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace Shop.Api.Security;

public static class ReverseProxyExtensions
{
    public static IServiceCollection AddReverseProxyHeaders(this IServiceCollection services)
    {
        services.AddOptions<ReverseProxyOptions>()
            .BindConfiguration(ReverseProxyOptions.Section)
            .Validate(ReverseProxyOptions.HasExplicitNetworks,
                "When ReverseProxy:Enabled is true, ReverseProxy:TrustedNetworks must contain valid CIDRs with nonzero prefixes.")
            .ValidateOnStart();

        // PostConfigure also overrides framework configuration from ASPNETCORE_FORWARDEDHEADERS_ENABLED.
        // An explicit disabled policy must never turn into the framework's trust-all shorthand.
        services.AddOptions<ForwardedHeadersOptions>().PostConfigure<IOptions<ReverseProxyOptions>>((forwarded, settings) =>
        {
            var proxy = settings.Value;
            forwarded.ForwardedHeaders = proxy.Enabled
                ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
                : ForwardedHeaders.None;
            forwarded.ForwardLimit = 1;
            forwarded.RequireHeaderSymmetry = true;
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            if (proxy.Enabled)
            {
                foreach (var network in proxy.TrustedNetworks)
                {
                    forwarded.KnownIPNetworks.Add(IPNetwork.Parse(network));
                }
            }
        });
        return services;
    }

    public static IApplicationBuilder UseReverseProxyHeaders(this IApplicationBuilder app) => app.UseForwardedHeaders();
}
