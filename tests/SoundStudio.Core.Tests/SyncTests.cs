using System.Net;
using System.Net.Http.Headers;
using SoundStudio.Core.Bo2;
using SoundStudio.Core.Editing;
using SoundStudio.Sync;

namespace SoundStudio.Core.Tests;

/// <summary>
/// Two "PCs" (separate folders, each with its own copy of a game bank) syncing through a real in-process
/// server on 127.0.0.1. Nothing leaves this machine.
/// </summary>
public sealed class SyncTests : IAsyncLifetime
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("soundstudio-sync-");
    private SyncServer _server = null!;
    private const string Token = "test-token-0123456789";
    private const string HostId = "host-1";

    public async Task InitializeAsync()
    {
        _server = new SyncServer(new SyncServerOptions
        {
            HostId = HostId, HostName = "Big brother", Token = Token,
            DataFolder = Path.Combine(_dir.FullName, "server"), Port = 0, LoopbackOnly = true,
        });
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _dir.Delete(recursive: true);
    }

    private string Url => $"http://127.0.0.1:{_server.Port}";

    private sealed class Pc(string root, string bankName)
    {
        public string Root { get; } = root;
        public string SoundFolder => Path.Combine(Root, "sound");
        public string BankPath => Path.Combine(SoundFolder, bankName);
        public BankEditor Editor { get; } = new(new OriginalsStore(Path.Combine(root, "originals")), new ChangeHistory(Path.Combine(root, "history.jsonl")), "me");
        public List<IncomingChange> Received { get; } = [];
    }

    private Pc MakePc(string name)
    {
        var bank = Path.GetFileName(GameFolder.SmallCleanBank());
        var pc = new Pc(Path.Combine(_dir.FullName, name), bank);
        Directory.CreateDirectory(pc.SoundFolder);
        File.Copy(GameFolder.SmallCleanBank(), pc.BankPath);
        return pc;
    }

    private SyncClient Client(Pc pc, string clientId, string author, string? url = null, string token = Token)
    {
        var client = new SyncClient(new SyncConnection(HostId, "Big brother", token, [url ?? Url]), clientId, author,
            Path.Combine(pc.Root, "sync"))
        {
            RetryDelay = TimeSpan.FromMilliseconds(200),
            PollInterval = TimeSpan.FromSeconds(60), // must work via live updates and the wake signal, not polling
        };
        client.OnChanges = changes =>
        {
            lock (pc.Received)
                pc.Received.AddRange(changes);
            return Task.CompletedTask;
        };
        return client;
    }

    private static async Task WaitUntil(Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(50);
        }
    }

    [SkippableFact]
    public async Task A_change_on_one_pc_arrives_on_the_other_and_applies_identically()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var a = MakePc("a");
        var b = MakePc("b");
        await using var clientA = Client(a, "pc-a", "Alex");
        await using var clientB = Client(b, "pc-b", "Brother");
        clientA.Start();
        clientB.Start();
        await WaitUntil(() => clientA.Status == SyncStatus.Connected && clientB.Status == SyncStatus.Connected);

        // A replaces sound 1 and publishes it.
        uint id;
        using (var bank = SabBank.Open(a.BankPath))
            id = bank.Entries[1].Id;
        var data = new byte[3000];
        Random.Shared.NextBytes(data);
        a.Editor.Apply(a.BankPath, [new SoundChange(1, id, data, 1500, "voice.wav")]);
        clientA.Publish([SyncChanges.Replace(a.BankPath, 1, id, data, 1500, "voice.wav")]);

        // B hears about it (via WebSocket), checks it, and applies it.
        await WaitUntil(() => b.Received.Count == 1);
        var incoming = b.Received[0];
        Assert.Equal("Alex", incoming.Change.Author);
        Assert.Equal("voice.wav", incoming.Change.SourceName);
        Assert.Null(SyncChanges.CheckCompatible(incoming.Change, b.SoundFolder));
        b.Editor.Apply(b.BankPath, [new SoundChange(incoming.Change.Index, incoming.Change.Id, incoming.Data!, incoming.Change.SampleCount)]);

        Assert.Equal(File.ReadAllBytes(a.BankPath), File.ReadAllBytes(b.BankPath));
        Assert.Empty(a.Received); // A doesn't get its own change back
        Assert.Contains(_server.Peers, p => p.Name == "Brother");

        // B reverts; A receives the revert.
        b.Editor.Revert(b.BankPath, [1]);
        clientB.Publish([(SyncChanges.Revert(b.BankPath, 1, id), null)]);
        await WaitUntil(() => a.Received.Count == 1);
        Assert.Equal(SyncAction.Revert, a.Received[0].Change.Action);
    }

    [SkippableFact]
    public async Task Changes_made_offline_are_sent_when_the_host_is_back()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var a = MakePc("a");
        var b = MakePc("b");
        var deadUrl = "http://127.0.0.1:9"; // nothing listens there
        await using var offline = Client(a, "pc-a", "Alex", url: deadUrl);
        offline.Start();
        uint id;
        using (var bank = SabBank.Open(a.BankPath))
            id = bank.Entries[0].Id;
        offline.Publish([SyncChanges.Current(a.BankPath, 0, id, "test")]);
        await WaitUntil(() => offline.Status == SyncStatus.Offline);
        Assert.Equal(1, offline.OutboxCount);
        await offline.DisposeAsync();

        // Same PC, now with the right address: the queued change goes out.
        await using var online = Client(a, "pc-a", "Alex");
        online.Start();
        await WaitUntil(() => online.OutboxCount == 0);

        await using var clientB = Client(b, "pc-b", "Brother");
        clientB.Start();
        await WaitUntil(() => b.Received.Count == 1);
        Assert.Equal(id, b.Received[0].Change.Id);
    }

    [Fact]
    public async Task Wrong_token_is_rejected_and_hello_reveals_nothing_secret()
    {
        using var http = new HttpClient { BaseAddress = new Uri(Url) };
        var hello = await http.GetStringAsync("/api/hello");
        Assert.Contains(HostId, hello);
        Assert.DoesNotContain(Token, hello);

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/changes")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/changes")).StatusCode);

        // A blob whose content doesn't match its name is refused.
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var put = await http.PutAsync($"/api/blobs/{new string('a', 64)}", new ByteArrayContent([1, 2, 3]));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [SkippableFact]
    public async Task Bad_token_client_reports_a_clear_error()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var a = MakePc("a");
        await using var client = Client(a, "pc-a", "Alex", token: "wrong");
        client.Start();
        await WaitUntil(() => client.Status == SyncStatus.Offline);
        Assert.Contains("rejected the invite code", client.LastError);
    }

    [Fact]
    public void Invite_codes_round_trip_and_reject_garbage()
    {
        var code = new InviteCode("h1", "Alex's PC", SyncProtocol.NewToken(),
            ["http://192.168.0.170:5150", "http://alex.tail1234.ts.net:5150", "https://abc-def.trycloudflare.com"]);
        var text = code.ToString();
        Assert.StartsWith("SS1.", text);
        Assert.True(InviteCode.TryParse("  " + text + "\n", out var parsed));
        Assert.Equal(code.Token, parsed!.Token);
        Assert.Equal(code.Urls, parsed.Urls);
        Assert.False(InviteCode.TryParse("SS1.not-base64!!", out _));
        Assert.False(InviteCode.TryParse("hello", out _));

        Assert.Equal(RouteKind.Lan, Reachability.Classify("http://192.168.0.170:5150"));
        Assert.Equal(RouteKind.Tailscale, Reachability.Classify("http://100.67.37.57:5150"));
        Assert.Equal(RouteKind.Tailscale, Reachability.Classify("http://pc.tail1234.ts.net:5150"));
        Assert.Equal(RouteKind.Internet, Reachability.Classify("https://abc.trycloudflare.com"));
    }

    [Theory]
    [InlineData("sounds.example.com", "https://sounds.example.com")]
    [InlineData(" https://Sounds.Example.com/ ", "https://sounds.example.com")]
    [InlineData("http://192.168.0.5:8080", "http://192.168.0.5:8080")]
    [InlineData("", null)]
    public void Normalizes_public_addresses(string input, string? expected)
    {
        Assert.Equal(expected, Reachability.NormalizePublicUrl(input, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("https://sounds.example.com/some/path")]
    [InlineData("ftp://example.com")]
    [InlineData("not a url at all")]
    public void Rejects_unusable_public_addresses(string input)
    {
        Assert.Null(Reachability.NormalizePublicUrl(input, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Public_address_is_listed_before_the_temporary_tunnel_and_is_optional()
    {
        var without = await Reachability.RoutesAsync(5150, "https://tmp.trycloudflare.com");
        Assert.DoesNotContain(without, r => r.Label.StartsWith("Your address"));

        var with = await Reachability.RoutesAsync(5150, "https://tmp.trycloudflare.com", publicUrl: "https://sounds.example.com");
        var urls = with.Select(r => r.Url).ToList();
        Assert.True(urls.IndexOf("https://sounds.example.com") < urls.IndexOf("https://tmp.trycloudflare.com"));
    }

    [Theory]
    [InlineData(@"..\..\Windows\win.ini")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("sub/zmb_tomb.all.sabs")]
    [InlineData("notes.txt")]
    public void Rejects_bank_names_that_are_not_plain_bank_files(string bank)
    {
        var change = new SyncChange
        {
            ClientId = "c", Author = "a", Action = SyncAction.Revert, Bank = bank, Index = 0, Id = 1,
            OriginalChecksum = new string('0', 32),
        };
        Assert.Equal("Invalid bank name.", SyncProtocol.Validate(change));
        Assert.Equal("Invalid bank name.", SyncChanges.CheckCompatible(change, @"C:\nowhere"));
    }

    [Fact]
    public async Task Server_refuses_invalid_changes()
    {
        using var http = new HttpClient { BaseAddress = new Uri(Url) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var bad = new SyncChange
        {
            ClientId = "c", Author = "a", Action = SyncAction.Revert, Bank = @"..\evil.sabs", Index = 0, Id = 1,
            OriginalChecksum = new string('0', 32),
        };
        var response = await http.PostAsync("/api/changes",
            new StringContent(System.Text.Json.JsonSerializer.Serialize(bad, SyncProtocol.Json), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Regression: stopping the host while a friend's connection has died must not throw.</summary>
    [Fact]
    public async Task Host_stops_cleanly_when_a_friend_connection_died()
    {
        var server = new SyncServer(new SyncServerOptions
        {
            HostId = "h2", HostName = "H", Token = Token, Port = 0, LoopbackOnly = true,
            DataFolder = Path.Combine(_dir.FullName, "server2"),
        });
        await server.StartAsync();
        var ws = new System.Net.WebSockets.ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + Token);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/api/live"), CancellationToken.None);
        await ws.ReceiveAsync(new byte[64], CancellationToken.None); // "seq:0"
        ws.Abort(); // like a tunnel or Wi-Fi dropping
        ws.Dispose();
        await Task.Delay(200);
        await server.DisposeAsync(); // must not throw
    }

    [Fact]
    public void Cleans_names_from_other_people()
    {
        Assert.Equal("Bro", SyncProtocol.CleanName("Bro\\..\\"));
        Assert.Equal("Friend", SyncProtocol.CleanName("  "));
        Assert.Equal(40, SyncProtocol.CleanName(new string('x', 200)).Length);
    }

    [SkippableFact]
    public void Incompatible_changes_are_detected()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var a = MakePc("a");
        uint id;
        using (var bank = SabBank.Open(a.BankPath))
            id = bank.Entries[0].Id;
        var (change, _) = SyncChanges.Current(a.BankPath, 0, id, null);
        Assert.Null(SyncChanges.CheckCompatible(change, a.SoundFolder));
        Assert.Contains("different game version", SyncChanges.CheckCompatible(change with { OriginalChecksum = new string('0', 32) }, a.SoundFolder));
        Assert.Contains("don't have", SyncChanges.CheckCompatible(change with { Bank = "nope.all.sabl" }, a.SoundFolder));
    }
}
