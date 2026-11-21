using System.Diagnostics;
using SoundStudio.Core.Bo2;
using SoundStudio.Sync;
using Xunit.Abstractions;

namespace SoundStudio.Core.Tests;

/// <summary>
/// Live test through a real Cloudflare quick tunnel. Opt-in only (it creates a temporary public address):
/// set SOUNDSTUDIO_TUNNEL_TEST=1. The server listens on 127.0.0.1 only; the tunnel is the only way in.
/// </summary>
public sealed class TunnelTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Sync_works_through_a_cloudflare_quick_tunnel()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("SOUNDSTUDIO_TUNNEL_TEST") == "1", "Opt-in: set SOUNDSTUDIO_TUNNEL_TEST=1");
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var cloudflared = QuickTunnel.FindCloudflared();
        Skip.If(cloudflared == null, "cloudflared not installed");

        var dir = Directory.CreateTempSubdirectory("ss-tunnel-");
        var token = SyncProtocol.NewToken();
        var sw = Stopwatch.StartNew();
        try
        {
            await using var server = new SyncServer(new SyncServerOptions
            {
                HostId = "tunnel-host", HostName = "Tunnel host", Token = token,
                DataFolder = Path.Combine(dir.FullName, "server"), Port = 0, LoopbackOnly = true,
            });
            await server.StartAsync();

            await using var tunnel = new QuickTunnel();
            var url = await tunnel.StartAsync(cloudflared!, server.Port);
            output.WriteLine($"tunnel up after {sw.Elapsed.TotalSeconds:0.0}s: {url}");
            Assert.Matches(@"^https://[a-z0-9-]+\.trycloudflare\.com$", url);

            // New quick-tunnel hostnames can take a moment to resolve; wait until hello answers through it.
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                var ready = false;
                for (var i = 0; i < 30 && !ready; i++)
                {
                    try { ready = (await http.GetAsync(url + "/api/hello")).IsSuccessStatusCode; }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) { }
                    if (!ready)
                        await Task.Delay(2000);
                }
                Assert.True(ready, "Tunnel never answered.");
                output.WriteLine($"hello answered through tunnel after {sw.Elapsed.TotalSeconds:0.0}s");

                // Without the token, the internet gets nothing.
                var denied = await http.GetAsync(url + "/api/changes");
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, denied.StatusCode);
            }

            // Two clients that only know the public https address.
            var bankSource = GameFolder.SmallCleanBank();
            var bankA = Path.Combine(dir.FullName, "a", Path.GetFileName(bankSource));
            Directory.CreateDirectory(Path.GetDirectoryName(bankA)!);
            File.Copy(bankSource, bankA);

            // A lists sound 0 in its manifest; B asks for it; A uploads it on request.
            uint soundId;
            using (var bank = SabBank.Open(bankA))
                soundId = bank.Entries[0].Id;
            var entry = SyncState.Describe(bankA, 0, soundId, "tunnel test");
            var received = new List<byte[]>();
            IReadOnlyList<Manifest> seenByB = [];
            SyncClient Client(string id, string name) => new(new SyncConnection("tunnel-host", "Tunnel host", token, [url]), id, name,
                Path.Combine(dir.FullName, id)) { RetryDelay = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromSeconds(60) };

            await using var a = Client("a", "Sender");
            a.ManifestProvider = () => [entry];
            a.AudioProvider = r => SyncState.ReadIfCurrent(Path.GetDirectoryName(bankA)!, r);
            await using var b = Client("b", "Receiver");
            b.OnManifests = m => { seenByB = m; return Task.CompletedTask; };
            b.OnAudio = (_, data) => { lock (received) received.Add(data); return Task.CompletedTask; };
            a.Start();
            b.Start();
            await Wait(() => a.Status == SyncStatus.Connected && b.Status == SyncStatus.Connected && seenByB.Count == 1, 60);
            output.WriteLine($"both clients connected via {b.ActiveUrl} after {sw.Elapsed.TotalSeconds:0.0}s");

            var t0 = sw.Elapsed;
            b.Request([("a", "Sender", seenByB[0].Sounds[0])]);
            await Wait(() => { lock (received) return received.Count == 1; }, 60);
            output.WriteLine($"sound ({entry.Size / 1024} KB) requested and delivered through the tunnel in {(sw.Elapsed - t0).TotalSeconds:0.00}s");

            using (var bank = SabBank.Open(bankA))
                Assert.Equal(bank.Entries[0].ReadOriginalData(), received[0]);
            Assert.Equal("Sender", seenByB[0].Name);
        }
        finally
        {
            output.WriteLine($"tunnel stopped; total {sw.Elapsed.TotalSeconds:0.0}s");
            dir.Delete(recursive: true);
        }
    }

    private static async Task Wait(Func<bool> condition, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Timed out.");
            await Task.Delay(100);
        }
    }
}
