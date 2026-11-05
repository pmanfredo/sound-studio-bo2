using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SoundStudio.Sync;

public sealed record SyncServerOptions
{
    public required string HostId { get; init; }
    public required string HostName { get; init; }
    public required string Token { get; init; }
    public required string DataFolder { get; init; }
    public int Port { get; init; } = SyncProtocol.DefaultPort;

    /// <summary>Listen only on 127.0.0.1 (tests, or tunnel-only hosting).</summary>
    public bool LoopbackOnly { get; init; }
}

/// <summary>
/// Host mode: a small HTTP + WebSocket server inside the app. Keeps an append-only change log and the audio
/// blobs; every request except the hello needs the invite token. Reachable over LAN, Tailscale, or a
/// Cloudflare tunnel pointed at it.
/// </summary>
public sealed class SyncServer : IAsyncDisposable
{
    private readonly SyncServerOptions _options;
    private readonly ServerStore _store;
    private readonly ConcurrentDictionary<Guid, LiveSocket> _sockets = new();

    /// <summary>A WebSocket plus a lock: WebSockets allow only one send at a time.</summary>
    private sealed class LiveSocket(WebSocket socket)
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        public WebSocket Socket { get; } = socket;

        public async Task SendAsync(string text)
        {
            await _send.WaitAsync();
            try { await Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { _send.Release(); }
        }
    }
    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new();
    private WebApplication? _app;

    public SyncServer(SyncServerOptions options)
    {
        _options = options;
        _store = new ServerStore(options.DataFolder);
    }

    /// <summary>Extra public addresses to advertise (e.g. the current Cloudflare tunnel URL).</summary>
    public Func<IReadOnlyList<string>> ExtraUrls { get; set; } = () => [];

    public int Port { get; private set; }
    public IReadOnlyCollection<PeerInfo> Peers => _peers.Values.ToList();
    public event EventHandler? PeersChanged;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<KestrelServerOptions>(k => k.Limits.MaxRequestBodySize = SyncProtocol.MaxBlobBytes);
        builder.WebHost.ConfigureKestrel(k =>
        {
            if (_options.LoopbackOnly)
                k.Listen(IPAddress.Loopback, _options.Port);
            else
                k.Listen(IPAddress.Any, _options.Port);
        });

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        Map(app);
        await app.StartAsync(ct);
        _app = app;
        var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
        Port = addresses?.Select(a => new Uri(a.Replace("0.0.0.0", "localhost").Replace("[::]", "localhost")).Port).FirstOrDefault() ?? _options.Port;
    }

    private void Map(WebApplication app)
    {
        app.MapGet("/api/hello", () => Results.Json(
            new HostHello(_options.HostId, _options.HostName, SyncProtocol.Version), SyncProtocol.Json));

        var api = app.MapGroup("/api").AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var header = http.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : null;
            if (!SyncProtocol.TokensEqual(token, _options.Token))
            {
                await Task.Delay(250); // slow down guessing
                return Results.Unauthorized();
            }
            TouchPeer(http);
            return await next(ctx);
        });

        api.MapGet("/details", () => Results.Json(new HostDetails(_options.HostId, _options.HostName,
            ExtraUrls(), Peers.ToList(), _store.LatestSeq), SyncProtocol.Json));

        api.MapGet("/changes", (long? since) =>
            Results.Json(_store.ChangesSince(since ?? 0), SyncProtocol.Json));

        api.MapPost("/changes", async (HttpRequest request) =>
        {
            var change = await JsonSerializer.DeserializeAsync<SyncChange>(request.Body, SyncProtocol.Json);
            if (change == null)
                return Results.BadRequest("Invalid change.");
            if (SyncProtocol.Validate(change) is { } problem)
                return Results.BadRequest(problem);
            if (change.Action == SyncAction.Replace && (change.BlobSha256 == null || !_store.HasBlob(change.BlobSha256)))
                return Results.BadRequest("Upload the audio before the change.");
            var stored = _store.Append(change);
            await NotifyAsync(stored.Seq);
            return Results.Json(stored, SyncProtocol.Json);
        });

        api.MapMethods("/blobs/{sha}", ["HEAD"], (string sha) =>
            _store.HasBlob(sha) ? Results.Ok() : Results.NotFound());

        api.MapGet("/blobs/{sha}", (string sha) =>
            _store.HasBlob(sha) ? Results.File(_store.BlobPath(sha), "application/octet-stream") : Results.NotFound());

        api.MapPut("/blobs/{sha}", async (string sha, HttpRequest request) =>
        {
            if (!ServerStore.IsSha(sha))
                return Results.BadRequest("Bad hash.");
            var ok = await _store.PutBlobAsync(sha, request.Body);
            return ok ? Results.Ok() : Results.BadRequest("Content doesn't match its hash.");
        });

        // Live updates: the server sends "seq:N" whenever a change is added.
        api.Map("/live", async (HttpContext http) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
                return Results.BadRequest();
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            var id = Guid.NewGuid();
            var live = new LiveSocket(socket);
            _sockets[id] = live;
            try
            {
                await live.SendAsync($"seq:{_store.LatestSeq}");
                var buffer = new byte[256];
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, http.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                    TouchPeer(http);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { }
            finally
            {
                _sockets.TryRemove(id, out _);
            }
            return Results.Empty;
        });
    }

    private void TouchPeer(HttpContext http)
    {
        var clientId = http.Request.Headers["X-Client-Id"].ToString();
        if (string.IsNullOrEmpty(clientId))
            return;
        var name = Uri.UnescapeDataString(http.Request.Headers["X-Client-Name"].ToString());
        var isNew = !_peers.ContainsKey(clientId);
        if (clientId.Length > 64)
            return;
        _peers[clientId] = new PeerInfo(clientId, SyncProtocol.CleanName(name, "Someone"), DateTime.UtcNow);
        if (isNew)
            PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task NotifyAsync(long seq)
    {
        foreach (var (id, live) in _sockets)
        {
            try { await live.SendAsync($"seq:{seq}"); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException or InvalidOperationException)
            {
                _sockets.TryRemove(id, out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Say goodbye politely, but never let a friend's half-dead connection stop the host from shutting down.
        foreach (var socket in _sockets.Values.Select(s => s.Socket))
        {
            if (socket.State != WebSocketState.Open)
                continue;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Host stopped", timeout.Token); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException
                                           or OperationCanceledException or IOException) { }
        }
        _sockets.Clear();
        if (_app != null)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _app.StopAsync(stop.Token); }
            catch (OperationCanceledException) { }
            await _app.DisposeAsync();
            _app = null;
        }
    }
}

/// <summary>Host-side storage: changes.jsonl (append-only) + blobs/{sha256}.</summary>
internal sealed class ServerStore
{
    private readonly string _folder;
    private readonly string _logPath;
    private readonly List<SyncChange> _changes = [];
    private readonly HashSet<Guid> _ids = [];
    private readonly object _lock = new();

    public ServerStore(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(Path.Combine(folder, "blobs"));
        _logPath = Path.Combine(folder, "changes.jsonl");
        if (File.Exists(_logPath))
        {
            foreach (var line in File.ReadLines(_logPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    var c = JsonSerializer.Deserialize<SyncChange>(line, SyncProtocol.Json);
                    if (c != null && _ids.Add(c.ChangeId))
                        _changes.Add(c);
                }
                catch (JsonException) { }
            }
        }
    }

    public long LatestSeq { get { lock (_lock) return _changes.Count == 0 ? 0 : _changes[^1].Seq; } }

    public List<SyncChange> ChangesSince(long seq)
    {
        lock (_lock)
            return _changes.Where(c => c.Seq > seq).ToList();
    }

    /// <summary>Adds a change (idempotent by ChangeId) and returns it with its sequence number.</summary>
    public SyncChange Append(SyncChange change)
    {
        lock (_lock)
        {
            if (!_ids.Add(change.ChangeId))
                return _changes.First(c => c.ChangeId == change.ChangeId);
            var stored = change with { Seq = (_changes.Count == 0 ? 0 : _changes[^1].Seq) + 1 };
            _changes.Add(stored);
            File.AppendAllText(_logPath, JsonSerializer.Serialize(stored, SyncProtocol.Json) + Environment.NewLine);
            return stored;
        }
    }

    public static bool IsSha(string sha) => sha.Length == 64 && sha.All(Uri.IsHexDigit);
    public string BlobPath(string sha) => Path.Combine(_folder, "blobs", sha.ToLowerInvariant());
    public bool HasBlob(string sha) => IsSha(sha) && File.Exists(BlobPath(sha));

    public async Task<bool> PutBlobAsync(string sha, Stream body)
    {
        var temp = BlobPath(sha) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            await using (var fs = File.Create(temp))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await fs.WriteAsync(buffer.AsMemory(0, read));
                }
            }
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), sha, StringComparison.OrdinalIgnoreCase))
                return false;
            File.Move(temp, BlobPath(sha), overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
