using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace FirstBlick.Api;

/// <summary>One explicitly trusted TLS terminator; forwarded host and prefix are never accepted.</summary>
public static class ProxyHosting
{
    public static ForwardedHeadersOptions Configure(IConfiguration configuration)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.None,
            ForwardLimit = 1,
            RequireHeaderSymmetry = true
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        var values = configuration.GetSection("Hosting:KnownProxies").Get<string[]>() ?? [];
        if (values.Length > 16) throw new InvalidOperationException("At most 16 explicit proxy addresses are supported.");
        foreach (var value in values)
        {
            if (!IPAddress.TryParse(value, out var address) || address.Equals(IPAddress.Any)
                || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None)
                || address.IsIPv6Multicast || (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && address.GetAddressBytes()[0] is >= 224 and <= 239))
                throw new InvalidOperationException("Hosting:KnownProxies must contain individual unicast IP addresses.");
            if (!options.KnownProxies.Contains(address)) options.KnownProxies.Add(address);
        }
        // An empty trust list must not enable the framework's trust-all fallback.
        if (options.KnownProxies.Count > 0)
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        return options;
    }
}
