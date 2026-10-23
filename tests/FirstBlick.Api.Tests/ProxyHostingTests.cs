using System.Net;
using FirstBlick.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FirstBlick.Api.Tests;

public sealed class ProxyHostingTests
{
    [Theory]
    [InlineData(null, "10.20.30.2", false)]
    [InlineData("10.20.30.2", "10.20.30.2", true)]
    [InlineData("10.20.30.2", "10.20.30.3", false)]
    [InlineData("10.20.30.2", "127.0.0.1", false)]
    public async Task Only_the_configured_peer_can_forward_scheme_and_client_ip(string? trusted, string peer, bool accepted)
    {
        var context = Context(peer);
        context.Request.Headers["X-Forwarded-For"] = "192.0.2.7";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "attacker.example";
        context.Request.Headers["X-Forwarded-Prefix"] = "/attacker";
        await Apply(context, Options(trusted));
        Assert.Equal(accepted ? "https" : "http", context.Request.Scheme);
        Assert.Equal(IPAddress.Parse(accepted ? "192.0.2.7" : peer), context.Connection.RemoteIpAddress);
        Assert.Equal("firstblick.example", context.Request.Host.Value);
        Assert.Equal(PathString.Empty, context.Request.PathBase);
    }

    [Fact]
    public async Task Only_the_rightmost_hop_is_consumed_and_missing_header_pairs_are_ignored()
    {
        var context = Context("10.20.30.2");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.9, 192.0.2.7";
        context.Request.Headers["X-Forwarded-Proto"] = "http, https";
        await Apply(context, Options("10.20.30.2"));
        Assert.Equal("https", context.Request.Scheme);
        Assert.Equal(IPAddress.Parse("192.0.2.7"), context.Connection.RemoteIpAddress);
        Assert.Equal("203.0.113.9", context.Request.Headers["X-Forwarded-For"].ToString());
        var incomplete = Context("10.20.30.2");
        incomplete.Request.Headers["X-Forwarded-Proto"] = "https";
        await Apply(incomplete, Options("10.20.30.2"));
        Assert.Equal("http", incomplete.Request.Scheme);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("10.20.30.0/24")]
    [InlineData("proxy.example")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("224.0.0.1")]
    [InlineData("ff02::1")]
    public void Invalid_or_broad_trust_configuration_fails_at_startup(string value)
        => Assert.Throws<InvalidOperationException>(() => Options(value));

    [Fact]
    public void Excess_proxy_entries_fail_at_startup()
    {
        var entries = Enumerable.Range(0, 17).ToDictionary(i => $"Hosting:KnownProxies:{i}", _ => (string?)"10.20.30.2");
        Assert.Throws<InvalidOperationException>(() => ProxyHosting.Configure(new ConfigurationBuilder().AddInMemoryCollection(entries).Build()));
    }

    private static ForwardedHeadersOptions Options(string? trusted)
        => ProxyHosting.Configure(new ConfigurationBuilder().AddInMemoryCollection(
            trusted is null ? [] : new Dictionary<string, string?> { ["Hosting:KnownProxies:0"] = trusted }).Build());

    private static DefaultHttpContext Context(string peer)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("firstblick.example");
        return context;
    }

    private static Task Apply(HttpContext context, ForwardedHeadersOptions options)
        => new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Microsoft.Extensions.Options.Options.Create(options)).Invoke(context);
}
