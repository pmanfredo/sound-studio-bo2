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

/// <summary>A change received from someone else, with its audio (null for reverts).</summary>
public sealed record IncomingChange(SyncChange Change, byte[]? Data);

public enum SyncStatus { Stopped, Connecting, Connected, Offline }

/// <summary>
/// Keeps this install in step with a host: sends local changes (queued in an outbox while offline) and
/// receives everyone else's. Tries the host's addresses in order (LAN, Tailscale, internet tunnel),
/// listens on a WebSocket for instant updates, and falls back to polling.
/// </summary>
public sealed class SyncClient : IAsyncDisposable
{
    private readonly SyncConnection _connection;
    private readonly string _clientId;
    private readonly string _author;
    private readonly string _folder;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _pullLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private HttpClient? _http;
    private long _lastSeq;

    public SyncClient(SyncConnection connection, string clientId, string author, string stateFolder)
    {
        _connection = connection;
        _clientId = clientId;
        _author = author;
        _folder = Path.Combine(stateFolder, "client-" + Sanitize(connection.HostId));
        Directory.CreateDirectory(Path.Combine(_folder, "outbox"));
        _lastSeq = LoadState();
    }

    /// <summary>Handles received changes; the position only advances after this completes.</summary>
    public Func<IReadOnlyList<IncomingChange>, Task>? OnChanges { get; set; }

    public event EventHandler? StatusChanged;
    public event EventHandler<HostDetails>? HostDetailsReceived;

    public SyncStatus Status { get; private set; } = SyncStatus.Stopped;
    public string? ActiveUrl { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? LastSyncUtc { get; private set; }
    public SyncConnection Connection => _connection;
    public long LastSeq => Interlocked.Read(ref _lastSeq);
    public int OutboxCount => Directory.EnumerateFiles(Path.Combine(_folder, "outbox"), "*.json").Count();

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

    /// <summary>Queues local changes for sending (works offline) and wakes the sync loop.</summary>
    public void Publish(IEnumerable<(SyncChange Change, byte[]? Data)> changes)
    {
        foreach (var (change, data) in changes)
        {
            var stamped = change with { ClientId = _clientId, Author = _author };
            if (data != null)
                File.WriteAllBytes(Path.Combine(_folder, "outbox", stamped.ChangeId + ".bin"), data);
            File.WriteAllText(Path.Combine(_folder, "outbox", stamped.ChangeId + ".json"), JsonSerializer.Serialize(stamped, SyncProtocol.Json));
        }
        _wake.Release();
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
                await FlushOutboxAsync(ct);
                await PullAsync(ct);
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
            }
        }
        throw last ?? new InvalidOperationException("No address to connect to.");
    }

    private HttpClient CreateHttp(string baseUrl)
    {
        var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _connection.Token);
        http.DefaultRequestHeaders.Add("X-Client-Id", _clientId);
        http.DefaultRequestHeaders.Add("X-Client-Name", Uri.EscapeDataString(_author));
        return http;
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        var url = ActiveUrl!;
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + _connection.Token);
        socket.Options.SetRequestHeader("X-Client-Id", _clientId);
        socket.Options.SetRequestHeader("X-Client-Name", Uri.EscapeDataString(_author));
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var wsUrl = new Uri(new Uri(url.TrimEnd('/') + "/"), "api/live");
        var builder = new UriBuilder(wsUrl) { Scheme = wsUrl.Scheme == "https" ? "wss" : "ws" };

        var live = true;
        try { await socket.ConnectAsync(builder.Uri, ct); }
        catch (WebSocketException) { live = false; } // some proxies block WebSockets: poll instead

        var buffer = new byte[256];
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
                if (text.StartsWith("seq:") && long.TryParse(text[4..], out var seq) && seq <= LastSeq)
                    continue;
            }
            await FlushOutboxAsync(ct);
            await PullAsync(ct);
        }
    }

    /// <summary>
    /// One outstanding wait on the wake signal, reused until it fires. Starting a new WaitAsync each loop
    /// would leave abandoned waiters that silently swallow later "send now" signals.
    /// </summary>
    private Task? _wakeTask;

    private Task WakeTask(CancellationToken ct) => _wakeTask ??= _wake.WaitAsync(ct);

    private void ConsumeWake()
    {
        _wakeTask = null;
        // Collapse a burst of signals into one round.
        while (_wake.CurrentCount > 0 && _wake.Wait(0)) { }
    }

    // ---- send / receive -----------------------------------------------------------------------------------

    private async Task FlushOutboxAsync(CancellationToken ct)
    {
        var http = _http ?? throw new InvalidOperationException("Not connected.");
        foreach (var json in Directory.EnumerateFiles(Path.Combine(_folder, "outbox"), "*.json").OrderBy(File.GetCreationTimeUtc).ToList())
        {
            var change = JsonSerializer.Deserialize<SyncChange>(await File.ReadAllTextAsync(json, ct), SyncProtocol.Json)!;
            var blob = Path.ChangeExtension(json, ".bin");
            if (change.BlobSha256 != null)
            {
                using var head = new HttpRequestMessage(HttpMethod.Head, $"api/blobs/{change.BlobSha256}");
                var exists = (await http.SendAsync(head, ct)).IsSuccessStatusCode;
                if (!exists)
                {
                    await using var fs = File.OpenRead(blob);
                    using var put = await http.PutAsync($"api/blobs/{change.BlobSha256}", new StreamContent(fs), ct);
                    put.EnsureSuccessStatusCode();
                }
            }
            using var post = await http.PostAsJsonAsync("api/changes", change, SyncProtocol.Json, ct);
            post.EnsureSuccessStatusCode();
            File.Delete(json);
            if (File.Exists(blob))
                File.Delete(blob);
        }
        LastSyncUtc = DateTime.UtcNow;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task PullAsync(CancellationToken ct)
    {
        var http = _http ?? throw new InvalidOperationException("Not connected.");
        await _pullLock.WaitAsync(ct);
        try
        {
            var changes = await http.GetFromJsonAsync<List<SyncChange>>($"api/changes?since={LastSeq}", SyncProtocol.Json, ct) ?? [];
            if (changes.Count == 0)
                return;
            var incoming = new List<IncomingChange>();
            foreach (var change in changes.Where(c => c.ClientId != _clientId))
            {
                byte[]? data = null;
                if (change.Action == SyncAction.Replace && change.BlobSha256 != null)
                {
                    data = await http.GetByteArrayAsync($"api/blobs/{change.BlobSha256}", ct);
                    if (SyncProtocol.Sha256Hex(data) != change.BlobSha256.ToLowerInvariant())
                        throw new IOException("Downloaded audio was damaged; will retry.");
                }
                incoming.Add(new IncomingChange(change, data));
            }
            if (incoming.Count > 0 && OnChanges != null)
                await OnChanges(incoming);
            Interlocked.Exchange(ref _lastSeq, changes.Max(c => c.Seq));
            SaveState();
            LastSyncUtc = DateTime.UtcNow;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _pullLock.Release();
        }
    }

    // ---- state -------------------------------------------------------------------------------------------

    private sealed record ClientState(long LastSeq);

    private long LoadState()
    {
        var path = Path.Combine(_folder, "state.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ClientState>(File.ReadAllText(path))?.LastSeq ?? 0 : 0; }
        catch (JsonException) { return 0; }
    }

    private void SaveState() =>
        File.WriteAllText(Path.Combine(_folder, "state.json"), JsonSerializer.Serialize(new ClientState(LastSeq)));

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
