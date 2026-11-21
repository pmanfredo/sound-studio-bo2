using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SoundStudio.Sync;

/// <summary>Where and how to reach a host; saved after joining.</summary>
public sealed record SyncConnection(string HostId, string HostName, string Token, List<string> Urls)
{
    public static SyncConnection FromInvite(InviteCode code) => new(code.HostId, code.HostName, code.Token, [.. code.Urls]);
}

/// <summary>A sound this install asked for and is waiting to receive.</summary>
public sealed record WantedAudio(string OwnerId, string OwnerName, ManifestEntry Entry, DateTime RequestedUtc);

public enum SyncStatus { Stopped, Connecting, Connected, Offline }

/// <summary>
/// Keeps this install in step with a host using state, not a change log:
/// <list type="bullet">
/// <item>publishes this install's <see cref="Manifest"/> (what it has changed, no audio) whenever it changes;</item>
/// <item>receives everyone else's manifests, so the app can show only real differences;</item>
/// <item>uploads a sound's audio only when someone asks for it;</item>
/// <item>downloads sounds this install asked for once their owner has uploaded them (requests survive
/// restarts and wait for the owner to come online).</item>
/// </list>
/// Tries the host's addresses in order (LAN, Tailscale, internet), listens on a WebSocket for instant updates,
/// and falls back to polling.
/// </summary>
public sealed class SyncClient : IAsyncDisposable
{
    private readonly SyncConnection _connection;
    private readonly string _clientId;
    private readonly string _name;
    private readonly string _folder;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _roundLock = new(1, 1);
    private readonly object _wantedLock = new();
    private readonly List<WantedAudio> _wanted;
    private readonly HashSet<string> _requestedThisConnection = [];
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private HttpClient? _http;
    private volatile bool _manifestDirty = true;
    private string? _lastPublishedManifest;

    public SyncClient(SyncConnection connection, string clientId, string name, string stateFolder)
    {
        _connection = connection;
        _clientId = clientId;
        _name = name;
        _folder = Path.Combine(stateFolder, "client-" + Sanitize(connection.HostId));
        Directory.CreateDirectory(_folder);
        _wanted = LoadWanted();
    }

    // ---- what the app plugs in ---------------------------------------------------------------------------

    /// <summary>This install's current modified sounds. Called on a background thread.</summary>
    public Func<List<ManifestEntry>>? ManifestProvider { get; set; }

    /// <summary>
    /// The current, game-encoded audio for a requested sound, or null if this install no longer has that exact
    /// version (its fingerprint changed). Called on a background thread.
    /// </summary>
    public Func<AudioRequest, byte[]?>? AudioProvider { get; set; }

    /// <summary>Everyone else's manifests (whenever they may have changed).</summary>
    public Func<IReadOnlyList<Manifest>, Task>? OnManifests { get; set; }

    /// <summary>Audio this install asked for has arrived (already checked against its fingerprint).</summary>
    public Func<WantedAudio, byte[], Task>? OnAudio { get; set; }

    public event EventHandler? StatusChanged;
    public event EventHandler<HostDetails>? HostDetailsReceived;

    public SyncStatus Status { get; private set; } = SyncStatus.Stopped;
    public string? ActiveUrl { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? LastSyncUtc { get; private set; }
    public SyncConnection Connection => _connection;
    public string ClientId => _clientId;

    public IReadOnlyList<WantedAudio> Wanted
    {
        get { lock (_wantedLock) return [.. _wanted]; }
    }

    /// <summary>How long a broken connection waits before retrying (shortened in tests).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    public void Start()
    {
        if (_loop != null)
            return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>This install's sounds changed (applied, reverted, rescanned): publish the new manifest.</summary>
    public void ManifestChanged()
    {
        _manifestDirty = true;
        _wake.Release();
    }

    /// <summary>Asks for sounds from other installs. Remembered until they arrive, even across restarts.</summary>
    public void Request(IEnumerable<(string OwnerId, string OwnerName, ManifestEntry Entry)> sounds)
    {
        lock (_wantedLock)
        {
            foreach (var (owner, ownerName, entry) in sounds)
            {
                _wanted.RemoveAll(w => w.Entry.Key == entry.Key); // a newer ask replaces an older one
                _wanted.Add(new WantedAudio(owner, ownerName, entry, DateTime.UtcNow));
            }
            SaveWanted();
        }
        _wake.Release();
    }

    public void CancelRequest(SoundKey key)
    {
        lock (_wantedLock)
        {
            if (_wanted.RemoveAll(w => w.Entry.Key == key) > 0)
                SaveWanted();
        }
    }

    /// <summary>Forces a sync round now (e.g. a "Sync now" button).</summary>
    public void Kick() => _wake.Release();

    // ---- main loop ---------------------------------------------------------------------------------------

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetStatus(SyncStatus.Connecting);
                await ConnectAsync(ct);
                _requestedThisConnection.Clear();
                _manifestDirty = true; // the host may have restarted: always publish on (re)connect
                await RoundAsync(ct);
                SetStatus(SyncStatus.Connected);
                await ListenAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or WebSocketException or IOException
                                           or TaskCanceledException or InvalidOperationException or JsonException)
            {
                LastError = ex.Message;
                ActiveUrl = null;
                SetStatus(SyncStatus.Offline);
                try
                {
                    var wake = WakeTask(ct);
                    if (await Task.WhenAny(Task.Delay(RetryDelay, ct), wake) == wake)
                        ConsumeWake();
                }
                catch (OperationCanceledException) { break; }
            }
        }
        SetStatus(SyncStatus.Stopped);
    }

    /// <summary>Finds the first address that answers as the right host and accepts our token.</summary>
    private async Task ConnectAsync(CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in _connection.Urls.ToList())
        {
            var http = CreateHttp(url);
            try
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probe.CancelAfter(TimeSpan.FromSeconds(url.StartsWith("https") ? 10 : 3));
                var hello = await http.GetFromJsonAsync<HostHello>("api/hello", SyncProtocol.Json, probe.Token);
                if (hello?.HostId != _connection.HostId)
                    throw new InvalidOperationException("A different Sound Studio host answered at " + url);
                if (hello.Protocol != SyncProtocol.Version)
                    throw new InvalidOperationException(hello.Protocol < SyncProtocol.Version
                        ? "The host runs an older Sound Studio. Update it on the host's PC."
                        : "The host runs a newer Sound Studio. Update this copy.");
                var details = await http.GetFromJsonAsync<HostDetails>("api/details", SyncProtocol.Json, probe.Token);
                if (details != null)
                {
                    // Learn new addresses (e.g. a fresh tunnel URL) for next time.
                    foreach (var u in details.Urls.Where(u => !_connection.Urls.Contains(u)))
                        _connection.Urls.Add(u);
                    HostDetailsReceived?.Invoke(this, details);
                }
                _http?.Dispose();
                _http = http;
                ActiveUrl = url;
                LastError = null;
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
            {
                last = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized }
                    ? new InvalidOperationException("The host rejected the invite code. Ask for a new one.")
                    : ex;
                http.Dispose();
                if (ex is InvalidOperationException { Message: var m } && m.Contains("Sound Studio. Update"))
                    break; // reachable but incompatible: trying other addresses won't help
            }
        }
        throw last ?? new InvalidOperationException("No address to connect to.");
    }

    private HttpClient CreateHttp(string baseUrl)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _connection.Token);
        http.DefaultRequestHeaders.Add("X-Client-Id", _clientId);
        http.DefaultRequestHeaders.Add("X-Client-Name", Uri.EscapeDataString(_name));
        return http;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        var url = ActiveUrl!;
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + _connection.Token);
        socket.Options.SetRequestHeader("X-Client-Id", _clientId);
        socket.Options.SetRequestHeader("X-Client-Name", Uri.EscapeDataString(_name));
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var wsUrl = new Uri(new Uri(url.TrimEnd('/') + "/"), "api/live");
        var builder = new UriBuilder(wsUrl) { Scheme = wsUrl.Scheme == "https" ? "wss" : "ws" };

        var live = true;
        try { await socket.ConnectAsync(builder.Uri, ct); }
        catch (WebSocketException) { live = false; } // some proxies block WebSockets: poll instead

        var buffer = new byte[512];
        Task<WebSocketReceiveResult>? receive = live ? socket.ReceiveAsync(buffer, ct) : null;
        while (!ct.IsCancellationRequested)
        {
            var wake = WakeTask(ct);
            var poll = Task.Delay(PollInterval, ct);
            var done = receive != null ? await Task.WhenAny(receive, wake, poll) : await Task.WhenAny(wake, poll);
            if (done == wake)
                ConsumeWake();
            if (receive != null && done == receive)
            {
                var result = await receive;
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new IOException("The host closed the connection.");
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                receive = socket.ReceiveAsync(buffer, ct);
                if (text == $"manifest:{_clientId}" || text == "hello")
                    continue; // our own echo / greeting
            }
            await RoundAsync(ct);
        }
    }

    /// <summary>
    /// One outstanding wait on the wake signal, reused until it fires. Starting a new WaitAsync each loop
    /// would leave abandoned waiters that silently swallow later signals.
    /// </summary>
    private Task? _wakeTask;

    private Task WakeTask(CancellationToken ct) => _wakeTask ??= _wake.WaitAsync(ct);

    private void ConsumeWake()
    {
        _wakeTask = null;
        while (_wake.CurrentCount > 0 && _wake.Wait(0)) { } // collapse a burst of signals into one round
    }

    // ---- one sync round -------------------------------------------------------------------------------------

    private async Task RoundAsync(CancellationToken ct)
    {
        var http = _http ?? throw new InvalidOperationException("Not connected.");
        await _roundLock.WaitAsync(ct);
        try
        {
            await PublishManifestAsync(http, ct);
            await ReceiveManifestsAsync(http, ct);
            await FulfilRequestsAsync(http, ct);
            await CollectWantedAsync(http, ct);
            LastSyncUtc = DateTime.UtcNow;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _roundLock.Release();
        }
    }

    private async Task PublishManifestAsync(HttpClient http, CancellationToken ct)
    {
        if (!_manifestDirty || ManifestProvider == null)
            return;
        _manifestDirty = false;
        var manifest = new Manifest { ClientId = _clientId, Name = _name, Sounds = ManifestProvider() };
        var json = JsonSerializer.Serialize(manifest with { UpdatedUtc = default }, SyncProtocol.Json);
        if (json == _lastPublishedManifest)
            return;
        using var put = await http.PutAsJsonAsync("api/manifest", manifest, SyncProtocol.Json, ct);
        put.EnsureSuccessStatusCode();
        _lastPublishedManifest = json;
    }

    private async Task ReceiveManifestsAsync(HttpClient http, CancellationToken ct)
    {
        var all = await http.GetFromJsonAsync<List<Manifest>>("api/manifests", SyncProtocol.Json, ct) ?? [];
        var others = all.Where(m => m.ClientId != _clientId && SyncProtocol.Validate(m) == null)
            .Select(m => m with { Name = SyncProtocol.CleanName(m.Name) })
            .ToList();
        if (OnManifests != null)
            await OnManifests(others);
    }

    /// <summary>Uploads audio someone asked us for, if we still have that exact version.</summary>
    private async Task FulfilRequestsAsync(HttpClient http, CancellationToken ct)
    {
        if (AudioProvider == null)
            return;
        var requests = await http.GetFromJsonAsync<List<AudioRequest>>("api/requests", SyncProtocol.Json, ct) ?? [];
        foreach (var request in requests.GroupBy(r => r.Hash.ToLowerInvariant()).Select(g => g.First()))
        {
            if (SyncProtocol.Validate(request) != null)
                continue;
            var data = AudioProvider(request);
            if (data == null || SyncProtocol.Sha256Hex(data) != request.Hash.ToLowerInvariant())
                continue; // changed since: our new manifest tells them
            using var put = await http.PutAsync($"api/blobs/{request.Hash.ToLowerInvariant()}", new ByteArrayContent(data), ct);
            put.EnsureSuccessStatusCode();
        }
    }

    /// <summary>Asks for wanted sounds (once per connection) and downloads any that have arrived.</summary>
    private async Task CollectWantedAsync(HttpClient http, CancellationToken ct)
    {
        var wanted = Wanted;
        if (wanted.Count == 0)
            return;

        var toAsk = wanted.Where(w => _requestedThisConnection.Add($"{w.OwnerId}|{w.Entry.Hash}")).ToList();
        if (toAsk.Count > 0)
        {
            var requests = toAsk.Select(w => new AudioRequest
            {
                RequesterId = _clientId, RequesterName = _name, OwnerId = w.OwnerId,
                Bank = w.Entry.Bank, Index = w.Entry.Index, Id = w.Entry.Id, Hash = w.Entry.Hash.ToLowerInvariant(),
            }).ToList();
            using var post = await http.PostAsJsonAsync("api/requests", requests, SyncProtocol.Json, ct);
            post.EnsureSuccessStatusCode();
        }

        foreach (var w in wanted)
        {
            var sha = w.Entry.Hash.ToLowerInvariant();
            using var head = new HttpRequestMessage(HttpMethod.Head, $"api/blobs/{sha}");
            if (!(await http.SendAsync(head, ct)).IsSuccessStatusCode)
                continue; // owner hasn't uploaded it yet
            var data = await http.GetByteArrayAsync($"api/blobs/{sha}", ct);
            if (SyncProtocol.Sha256Hex(data) != sha)
                throw new IOException("Downloaded audio was damaged; will retry.");
            if (OnAudio != null)
                await OnAudio(w, data);
            lock (_wantedLock)
            {
                _wanted.Remove(w);
                SaveWanted();
            }
        }
    }

    // ---- state -------------------------------------------------------------------------------------------

    private List<WantedAudio> LoadWanted()
    {
        var path = Path.Combine(_folder, "wanted.json");
        try
        {
            var list = File.Exists(path) ? JsonSerializer.Deserialize<List<WantedAudio>>(File.ReadAllText(path), SyncProtocol.Json) : null;
            return list?.Where(w => SyncProtocol.Validate(w.Entry) == null).ToList() ?? [];
        }
        catch (JsonException) { return []; }
    }

    private void SaveWanted()
    {
        var path = Path.Combine(_folder, "wanted.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_wanted, SyncProtocol.Json));
        File.Move(temp, path, overwrite: true);
    }

    private void SetStatus(SyncStatus status)
    {
        if (Status == status)
            return;
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Sanitize(string s) => string.Concat(s.Where(char.IsLetterOrDigit));

    public async ValueTask DisposeAsync()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            if (_loop != null)
            {
                try { await _loop; }
                catch (OperationCanceledException) { }
            }
        }
        _http?.Dispose();
    }
}
