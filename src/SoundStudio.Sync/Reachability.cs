using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SoundStudio.Sync;

/// <summary>How a host can be reached; listed in the invite code in this order.</summary>
public enum RouteKind { Lan, Tailscale, Internet }

public sealed record HostRoute(RouteKind Kind, string Url, string Label);

/// <summary>Works out the host's addresses: home network, Tailscale, and (optionally) a Cloudflare tunnel.</summary>
public static class Reachability
{
    /// <summary>Private IPv4 addresses on real, up adapters (skips VirtualBox/Hyper-V/VPN-ish virtual ones).</summary>
    public static IEnumerable<IPAddress> LanAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            var description = (nic.Description + " " + nic.Name).ToLowerInvariant();
            if (description.Contains("virtual") || description.Contains("vmware") || description.Contains("hyper-v") ||
                description.Contains("tailscale") || description.Contains("vpn") || description.Contains("loopback"))
                continue;
            foreach (var ip in nic.GetIPProperties().UnicastAddresses.Select(a => a.Address))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && IsPrivate(ip))
                    yield return ip;
            }
        }
    }

    public static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public static string? TailscaleExe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public sealed record TailscaleSelf(string Ip, string? DnsName, bool Online);

    /// <summary>This machine on Tailscale (via <c>tailscale status --json</c>), or null if not installed/running.</summary>
    public static async Task<TailscaleSelf?> TailscaleAsync(CancellationToken ct = default)
    {
        var exe = TailscaleExe();
        if (exe == null)
            return null;
        try
        {
            var output = await RunAsync(exe, ["status", "--json"], TimeSpan.FromSeconds(5), ct);
            using var doc = JsonDocument.Parse(output);
            if (!doc.RootElement.TryGetProperty("Self", out var self))
                return null;
            var ips = self.TryGetProperty("TailscaleIPs", out var list) ? list.EnumerateArray().Select(e => e.GetString()).ToList() : [];
            var ipv4 = ips.FirstOrDefault(i => i != null && i.Contains('.'));
            if (ipv4 == null)
                return null;
            var dns = self.TryGetProperty("DNSName", out var d) ? d.GetString()?.TrimEnd('.') : null;
            var online = !self.TryGetProperty("Online", out var o) || o.GetBoolean();
            var state = doc.RootElement.TryGetProperty("BackendState", out var s) ? s.GetString() : "Running";
            return new TailscaleSelf(ipv4, string.IsNullOrEmpty(dns) ? null : dns, online && state == "Running");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Cleans up a user-entered public address (e.g. a named Cloudflare tunnel or reverse proxy):
    /// "sounds.example.com" → "https://sounds.example.com". Returns null and an error for anything unusable.
    /// </summary>
    public static string? NormalizePublicUrl(string? input, out string? error)
    {
        error = null;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (!text.Contains("://"))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http") ||
            string.IsNullOrEmpty(uri.Host) || uri.Host.Contains(' '))
        {
            error = "That isn't a web address. Use something like sounds.example.com.";
            return null;
        }
        if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query))
        {
            error = "Use just the address (no path), e.g. https://sounds.example.com.";
            return null;
        }
        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// All the ways in, best first: LAN, then Tailscale (stable MagicDNS name preferred), then your own stable
    /// address if you set one, then the temporary quick tunnel.
    /// </summary>
    public static async Task<List<HostRoute>> RoutesAsync(int port, string? tunnelUrl, CancellationToken ct = default, string? publicUrl = null)
    {
        var routes = LanAddresses().Distinct()
            .Select(ip => new HostRoute(RouteKind.Lan, $"http://{ip}:{port}", $"Home network · {ip}"))
            .ToList();
        if (await TailscaleAsync(ct) is { Online: true } ts)
        {
            if (ts.DnsName != null)
                routes.Add(new HostRoute(RouteKind.Tailscale, $"http://{ts.DnsName}:{port}", $"Tailscale · {ts.DnsName}"));
            routes.Add(new HostRoute(RouteKind.Tailscale, $"http://{ts.Ip}:{port}", $"Tailscale · {ts.Ip}"));
        }
        if (publicUrl != null)
            routes.Add(new HostRoute(RouteKind.Internet, publicUrl, $"Your address · {new Uri(publicUrl).Host}"));
        if (tunnelUrl != null)
            routes.Add(new HostRoute(RouteKind.Internet, tunnelUrl, $"Internet (temporary) · {new Uri(tunnelUrl).Host}"));
        return routes;
    }

    public static RouteKind Classify(string url)
    {
        var host = new Uri(url).Host;
        if (url.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            return RouteKind.Internet;
        if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) || host.StartsWith("100."))
            return RouteKind.Tailscale;
        return RouteKind.Lan;
    }

    internal static async Task<string> RunAsync(string exe, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start " + exe);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var output = await p.StandardOutput.ReadToEndAsync(cts.Token);
        await p.WaitForExitAsync(cts.Token);
        return output;
    }
}

/// <summary>
/// A Cloudflare quick tunnel (<c>cloudflared tunnel --url</c>): gives the host a temporary public
/// https://*.trycloudflare.com address with no account, domain or port forwarding. The address changes
/// every time it starts.
/// </summary>
public sealed class QuickTunnel : IAsyncDisposable
{
    private static readonly Regex UrlPattern = new(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private Process? _process;

    public string? Url { get; private set; }
    public event EventHandler? Exited;

    public static string? FindCloudflared(string? configured = null)
    {
        var candidates = new[]
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, "cloudflared.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "cloudflared", "cloudflared.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "cloudflared", "cloudflared.exe"),
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), "cloudflared.exe")));
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
    }

    /// <summary>Starts the tunnel to a local port and waits for its public address.</summary>
    public async Task<string> StartAsync(string cloudflaredExe, int localPort, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(cloudflaredExe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "tunnel", "--no-autoupdate", "--url", $"http://127.0.0.1:{localPort}" })
            psi.ArgumentList.Add(a);

        var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new StringBuilder();
        void OnLine(string? line)
        {
            if (line == null)
                return;
            lock (log)
                log.AppendLine(line);
            var m = UrlPattern.Match(line);
            if (m.Success)
                found.TrySetResult(m.Value);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data); // cloudflared logs to stderr
        process.Exited += (_, _) =>
        {
            found.TrySetException(new InvalidOperationException("cloudflared stopped: " + LastLines(log)));
            Exited?.Invoke(this, EventArgs.Empty);
        };
        process.Start();
        ChildProcessGuard.Attach(process); // killed even if Sound Studio crashes
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        await using (timeout.Token.Register(() => found.TrySetException(new TimeoutException("Cloudflare didn't give an address in time. " + LastLines(log)))))
            Url = await found.Task;
        return Url;
    }

    private static string LastLines(StringBuilder log)
    {
        lock (log)
            return string.Join(" ", log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(2)).Trim();
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        _process?.Dispose();
        _process = null;
        Url = null;
        return ValueTask.CompletedTask;
    }
}
