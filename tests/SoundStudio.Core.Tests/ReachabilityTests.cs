using SoundStudio.Sync;
using Xunit.Abstractions;

namespace SoundStudio.Core.Tests;

public class ReachabilityTests(ITestOutputHelper output)
{
    /// <summary>Read-only: lists the addresses an invite code would contain on this machine.</summary>
    [Fact]
    public async Task Lists_lan_and_tailscale_routes_without_virtual_adapters()
    {
        var routes = await Reachability.RoutesAsync(5150, tunnelUrl: null);
        foreach (var r in routes)
            output.WriteLine($"{r.Kind}: {r.Url} ({r.Label})");
        Assert.All(routes, r => Assert.DoesNotContain("192.168.56.", r.Url)); // VirtualBox host-only adapter
        Assert.Equal(routes.OrderBy(r => r.Kind).Select(r => r.Url), routes.Select(r => r.Url)); // LAN, then Tailscale
        if (Reachability.TailscaleExe() != null && await Reachability.TailscaleAsync() is { Online: true })
            Assert.Contains(routes, r => r.Kind == RouteKind.Tailscale);
    }
}
