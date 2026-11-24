using System.Net;
using System.Net.Http.Headers;
using SoundStudio.Core.Bo2;
using SoundStudio.Core.Editing;
using SoundStudio.Sync;

namespace SoundStudio.Core.Tests;

/// <summary>
/// Two installs ("PCs": separate folders, each with its own copy of a game bank) syncing through a real
/// in-process server on 127.0.0.1. Nothing leaves this machine.
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
            HostId = HostId, HostName = "Host", Token = Token,
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

    /// <summary>One install: a sound folder with a copy of a bank, an editor, and what sync told it.</summary>
    private sealed class Pc(string root, string bankName)
    {
        public string Root { get; } = root;
        public string SoundFolder => Path.Combine(Root, "sound");
        public string BankPath => Path.Combine(SoundFolder, bankName);
        public BankEditor Editor { get; } = new(new OriginalsStore(Path.Combine(root, "originals")), new ChangeHistory(Path.Combine(root, "history.jsonl")), "me");
        public IReadOnlyList<Manifest> Others { get; set; } = [];
        public List<(WantedAudio Wanted, byte[] Data)> Received { get; } = [];

        /// <summary>This install's modified sounds, as the app would describe them.</summary>
        public List<ManifestEntry> Manifest()
        {
            using var bank = SabBank.Open(BankPath);
            return bank.Entries.Where(e => e.IsModifiedOnDisk())
                .Select(e => SyncState.Describe(BankPath, e.Index, e.Id, "test")).ToList();
        }

        public List<Difference> Differences()
        {
            var mine = Manifest().ToDictionary(e => e.Key);
            using var bank = SabBank.Open(BankPath);
            return SyncState.Compare(mine, Others, key =>
                key.Bank == Path.GetFileName(BankPath) && key.Index < bank.Entries.Count && bank.Entries[key.Index].Id == key.Id
                    ? Convert.ToHexString(bank.Entries[key.Index].Checksum)
                    : null);
        }

        public uint IdAt(int index)
        {
            using var bank = SabBank.Open(BankPath);
            return bank.Entries[index].Id;
        }

        public byte[] DataAt(int index)
        {
            using var bank = SabBank.Open(BankPath);
            return bank.Entries[index].ReadOriginalData();
        }

        public void Replace(int index, int seed)
        {
            var data = new byte[3000 + seed];
            new Random(seed).NextBytes(data);
            Editor.Apply(BankPath, [new SoundChange(index, IdAt(index), data, data.Length / 2, "voice.wav")]);
        }
    }

    private Pc MakePc(string name)
    {
        var source = GameFolder.SmallCleanBank();
        var pc = new Pc(Path.Combine(_dir.FullName, name), Path.GetFileName(source));
        Directory.CreateDirectory(pc.SoundFolder);
        File.Copy(source, pc.BankPath);
        return pc;
    }

    private SyncClient Client(Pc pc, string clientId, string name, string? url = null, string token = Token)
    {
        var client = new SyncClient(new SyncConnection(HostId, "Host", token, [url ?? Url]), clientId, name, Path.Combine(pc.Root, "sync"))
        {
            RetryDelay = TimeSpan.FromMilliseconds(200),
            PollInterval = TimeSpan.FromSeconds(60), // must work via live updates, not polling
            ManifestProvider = pc.Manifest,
            AudioProvider = r => SyncState.ReadIfCurrent(pc.SoundFolder, r),
            OnManifests = m => { pc.Others = m; return Task.CompletedTask; },
            OnAudio = (w, data) => { lock (pc.Received) pc.Received.Add((w, data)); return Task.CompletedTask; },
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
    public async Task Only_real_differences_are_offered_and_audio_moves_only_when_asked()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var a = MakePc("a");
        var b = MakePc("b");
        a.Replace(1, seed: 1);   // only A changed sound 1
        a.Replace(2, seed: 2);   // both changed sound 2 identically
        b.Replace(2, seed: 2);

        await using var clientA = Client(a, "pc-a", "Alex");
        await using var clientB = Client(b, "pc-b", "Sam");
        clientA.Start();
        clientB.Start();
        await WaitUntil(() => b.Others.Any(m => m.ClientId == "pc-a" && m.Sounds.Count == 2));

        // B is offered sound 1 only: sound 2 is identical on both PCs.
        var diffs = b.Differences();
        var diff = Assert.Single(diffs);
        Assert.Equal(1, diff.Theirs.Index);
        Assert.Equal(DifferenceKind.TheyChanged, diff.Kind);
        Assert.Equal("Alex", diff.OwnerName);
        Assert.Equal(0, _server.BlobCount); // nothing uploaded yet

        // B asks; A (online) uploads just that one sound; B receives it byte for byte.
        clientB.Request([(diff.OwnerId, diff.OwnerName, diff.Theirs)]);
        await WaitUntil(() => b.Received.Count == 1);
        Assert.Equal(a.DataAt(1), b.Received[0].Data);
        Assert.Equal(1, _server.BlobCount);
        Assert.Empty(clientB.Wanted);

        // B applies it: now the PCs match and nothing is offered any more.
        b.Editor.Apply(b.BankPath, [new SoundChange(1, b.IdAt(1), b.Received[0].Data, diff.Theirs.SampleCount)]);
        clientB.ManifestChanged();
        await WaitUntil(() => a.Others.Any(m => m.ClientId == "pc-b" && m.Sounds.Count == 2));
        Assert.Empty(b.Differences());
        Assert.Empty(a.Differences());
        Assert.Contains(_server.Peers, p => p.Name == "Sam" && p.Online);
    }

    [SkippableFact]
    public async Task A_request_waits_until_the_owner_is_back_online()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var a = MakePc("a");
        var b = MakePc("b");
        a.Replace(0, seed: 7);

        // A publishes its manifest, then goes offline.
        var clientA = Client(a, "pc-a", "Alex");
        clientA.Start();
        await WaitUntil(() => clientA.Status == SyncStatus.Connected);
        await clientA.DisposeAsync();

        await using var clientB = Client(b, "pc-b", "Sam");
        clientB.Start();
        await WaitUntil(() => b.Others.Any(m => m.ClientId == "pc-a"));
        var diff = Assert.Single(b.Differences());
        clientB.Request([(diff.OwnerId, diff.OwnerName, diff.Theirs)]);
        await Task.Delay(800);
        Assert.Empty(b.Received);       // owner is offline: still waiting
        Assert.Single(clientB.Wanted);

        // B restarts meanwhile: the request is remembered.
        await clientB.DisposeAsync();
        await using var clientB2 = Client(b, "pc-b", "Sam");
        Assert.Single(clientB2.Wanted);
        clientB2.Start();

        // A comes back: it sees the waiting request and uploads; B gets it.
        await using var clientA2 = Client(a, "pc-a", "Alex");
        clientA2.Start();
        await WaitUntil(() => b.Received.Count == 1);
        Assert.Equal(a.DataAt(0), b.Received[0].Data);
    }

    [SkippableFact]
    public async Task Both_changed_differently_is_a_conflict_on_both_sides()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var a = MakePc("a");
        var b = MakePc("b");
        a.Replace(1, seed: 10);
        b.Replace(1, seed: 20);
        await using var clientA = Client(a, "pc-a", "Alex");
        await using var clientB = Client(b, "pc-b", "Sam");
        clientA.Start();
        clientB.Start();
        await WaitUntil(() => a.Others.Count == 1 && b.Others.Count == 1);
        Assert.Equal(DifferenceKind.BothChanged, Assert.Single(a.Differences()).Kind);
        Assert.Equal(DifferenceKind.BothChanged, Assert.Single(b.Differences()).Kind);
    }

    [SkippableFact]
    public void Sounds_from_another_game_version_are_not_offered()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var b = MakePc("b");
        var theirs = SyncState.Describe(b.BankPath, 0, b.IdAt(0), null) with
        {
            Hash = new string('a', 64),
            OriginalChecksum = new string('0', 32), // their bank started from different audio
        };
        b.Others = [new Manifest { ClientId = "x", Name = "X", Sounds = [theirs] }];
        Assert.Empty(b.Differences());

        b.Others = [new Manifest { ClientId = "x", Name = "X", Sounds = [theirs with { OriginalChecksum = SyncState.Describe(b.BankPath, 0, b.IdAt(0), null).OriginalChecksum }] }];
        Assert.Single(b.Differences());
    }

    [Fact]
    public async Task Host_refuses_bad_tokens_unrequested_audio_and_bad_manifests()
    {
        using var http = new HttpClient { BaseAddress = new Uri(Url) };
        var hello = await http.GetStringAsync("/api/hello");
        Assert.Contains(HostId, hello);
        Assert.DoesNotContain(Token, hello);

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/manifests")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/manifests")).StatusCode);

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        http.DefaultRequestHeaders.Add("X-Client-Id", "me");

        // Audio nobody asked for is refused: audio only travels on request.
        var data = new byte[] { 1, 2, 3 };
        var put = await http.PutAsync($"/api/blobs/{SyncProtocol.Sha256Hex(data)}", new ByteArrayContent(data));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);

        // A manifest pointing outside the sound folder, or published for someone else, is refused.
        var entry = new ManifestEntry { Bank = @"..\evil.sabs", Index = 0, Id = 1, OriginalChecksum = new string('0', 32), Hash = new string('a', 64) };
        Assert.Equal(HttpStatusCode.BadRequest, (await PutManifest(http, new Manifest { ClientId = "me", Name = "Me", Sounds = [entry] })).StatusCode);
        var ok = entry with { Bank = "zmb_tomb.all.sabl" };
        Assert.Equal(HttpStatusCode.BadRequest, (await PutManifest(http, new Manifest { ClientId = "someone-else", Name = "X", Sounds = [ok] })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutManifest(http, new Manifest { ClientId = "me", Name = "Me", Sounds = [ok] })).StatusCode);
    }

    private static Task<HttpResponseMessage> PutManifest(HttpClient http, Manifest m) =>
        http.PutAsync("/api/manifest", new StringContent(System.Text.Json.JsonSerializer.Serialize(m, SyncProtocol.Json),
            System.Text.Encoding.UTF8, "application/json"));

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

    /// <summary>Regression: stopping the host while someone's connection has died must not throw.</summary>
    [Fact]
    public async Task Host_stops_cleanly_when_a_connection_died()
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
        await ws.ReceiveAsync(new byte[64], CancellationToken.None); // "hello"
        ws.Abort();
        ws.Dispose();
        await Task.Delay(200);
        await server.DisposeAsync(); // must not throw
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
        var entry = new ManifestEntry { Bank = bank, Index = 0, Id = 1, OriginalChecksum = new string('0', 32), Hash = new string('a', 64) };
        Assert.Equal("Invalid bank name.", SyncProtocol.Validate(entry));
        Assert.Null(SyncState.ReadIfCurrent(@"C:\nowhere", new AudioRequest
        {
            RequesterId = "r", OwnerId = "o", Bank = bank, Index = 0, Id = 1, Hash = new string('a', 64),
        }));
    }

    [Fact]
    public void Hidden_flag_survives_the_trip_and_older_manifests_are_not_hidden()
    {
        var entry = new ManifestEntry { Bank = "b.sabl", Index = 1, Id = 2, OriginalChecksum = "c", Hash = "h", Hidden = true };
        var back = System.Text.Json.JsonSerializer.Deserialize<ManifestEntry>(
            System.Text.Json.JsonSerializer.Serialize(entry, SyncProtocol.Json), SyncProtocol.Json)!;
        Assert.True(back.Hidden);
        Assert.Null(back.Source);

        var old = System.Text.Json.JsonSerializer.Deserialize<ManifestEntry>(
            "{\"bank\":\"b.sabl\",\"index\":1,\"id\":2,\"originalChecksum\":\"c\",\"hash\":\"h\",\"source\":\"voice.wav\"}", SyncProtocol.Json)!;
        Assert.False(old.Hidden);
        Assert.Equal("voice.wav", old.Source);
    }

    [Fact]
    public void Cleans_names_from_other_people()
    {
        Assert.Equal("Sam", SyncProtocol.CleanName("Sam\\..\\"));
        Assert.Equal("Someone", SyncProtocol.CleanName("  "));
        Assert.Equal(40, SyncProtocol.CleanName(new string('x', 200)).Length);
    }
}
