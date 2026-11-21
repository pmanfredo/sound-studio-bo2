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
/// Host mode: a small HTTP + WebSocket server inside the app. It holds each install's manifest (what it has
/// changed, no audio) and relays audio only when someone asks for a sound: the request waits here until the
/// install that has the sound uploads it. Every request except the hello needs the invite token.
/// </summary>
/// <remarks>
/// Live messages on <c>/api/live</c>: <c>manifest:&lt;clientId&gt;</c> (someone's state changed),
/// <c>request:&lt;ownerId&gt;</c> (someone wants audio from that install), <c>blob:&lt;sha256&gt;</c> (audio arrived).
/// </remarks>
public sealed class SyncServer : IAsyncDisposable
{
    private readonly SyncServerOptions _options;
    private readonly ServerStore _store;
    private readonly ConcurrentDictionary<Guid, LiveSocket> _sockets = new();
    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new();
    private WebApplication? _app;

    /// <summary>A WebSocket plus a lock: WebSockets allow only one send at a time.</summary>
    private sealed class LiveSocket(WebSocket socket, string clientId)
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        public WebSocket Socket { get; } = socket;
        public string ClientId { get; } = clientId;

        public async Task SendAsync(string text)
        {
            await _send.WaitAsync();
            try { await Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { _send.Release(); }
        }
    }

    public SyncServer(SyncServerOptions options)
    {
        _options = options;
        _store = new ServerStore(options.DataFolder);
    }

    /// <summary>Extra public addresses to advertise (e.g. the current Cloudflare tunnel URL).</summary>
    public Func<IReadOnlyList<string>> ExtraUrls { get; set; } = () => [];

    public int Port { get; private set; }

    /// <summary>Everyone who has connected, with whether they're connected right now.</summary>
    public IReadOnlyCollection<PeerInfo> Peers
    {
        get
        {
            var online = _sockets.Values.Select(s => s.ClientId).ToHashSet();
            return _peers.Values.Select(p => p with { Online = online.Contains(p.ClientId) }).ToList();
        }
    }

    public event EventHandler? PeersChanged;

    /// <summary>For tests and diagnostics: how many audio files the host is holding.</summary>
    public int BlobCount => _store.BlobCount;

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
            ExtraUrls(), Peers.ToList()), SyncProtocol.Json));

        // ---- manifests: what each install has changed (no audio) ----

        api.MapPut("/manifest", async (HttpRequest request) =>
        {
            var manifest = await JsonSerializer.DeserializeAsync<Manifest>(request.Body, SyncProtocol.Json);
            if (manifest == null)
                return Results.BadRequest("Invalid manifest.");
            if (manifest.ClientId != ClientIdOf(request.HttpContext))
                return Results.BadRequest("A manifest can only be published by its own install.");
            if (SyncProtocol.Validate(manifest) is { } problem)
                return Results.BadRequest(problem);
            var stored = manifest with { Name = SyncProtocol.CleanName(manifest.Name), UpdatedUtc = DateTime.UtcNow };
            _store.SaveManifest(stored);
            await NotifyAsync($"manifest:{stored.ClientId}");
            return Results.Ok();
        });

        api.MapGet("/manifests", () => Results.Json(_store.Manifests(), SyncProtocol.Json));

        // ---- requests: "please upload this sound" ----

        api.MapPost("/requests", async (HttpRequest request) =>
        {
            var items = await JsonSerializer.DeserializeAsync<List<AudioRequest>>(request.Body, SyncProtocol.Json);
            if (items == null || items.Count > SyncProtocol.MaxManifestSounds)
                return Results.BadRequest("Invalid request.");
            var requester = ClientIdOf(request.HttpContext);
            var ready = new List<string>();
            var owners = new HashSet<string>();
            foreach (var item in items)
            {
                if (SyncProtocol.Validate(item) is { } problem || item.RequesterId != requester)
                    return Results.BadRequest("Invalid request.");
                if (_store.HasBlob(item.Hash))
                    ready.Add(item.Hash); // already here (someone asked before): no need to bother the owner
                else if (_store.AddRequest(item))
                    owners.Add(item.OwnerId);
            }
            foreach (var owner in owners)
                await NotifyAsync($"request:{owner}");
            foreach (var sha in ready.Distinct())
                await NotifyAsync($"blob:{sha.ToLowerInvariant()}");
            return Results.Ok();
        });

        api.MapGet("/requests", (HttpRequest request) =>
            Results.Json(_store.RequestsFor(ClientIdOf(request.HttpContext)), SyncProtocol.Json));

        // ---- audio ----

        api.MapMethods("/blobs/{sha}", ["HEAD"], (string sha) =>
            _store.HasBlob(sha) ? Results.Ok() : Results.NotFound());

        api.MapGet("/blobs/{sha}", (string sha) =>
            _store.HasBlob(sha) ? Results.File(_store.BlobPath(sha), "application/octet-stream") : Results.NotFound());

        api.MapPut("/blobs/{sha}", async (string sha, HttpRequest request) =>
        {
            if (!ServerStore.IsSha(sha))
                return Results.BadRequest("Bad hash.");
            if (!_store.IsRequested(sha))
                return Results.BadRequest("Nobody asked for this sound."); // audio only travels on request
            var ok = await _store.PutBlobAsync(sha, request.Body);
            if (!ok)
                return Results.BadRequest("Content doesn't match its fingerprint.");
            _store.CompleteRequests(sha);
            await NotifyAsync($"blob:{sha.ToLowerInvariant()}");
            return Results.Ok();
        });

        // ---- live updates ----

        api.Map("/live", async (HttpContext http) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
                return Results.BadRequest();
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            var id = Guid.NewGuid();
            var live = new LiveSocket(socket, ClientIdOf(http));
            _sockets[id] = live;
            PeersChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                await live.SendAsync("hello");
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
                PeersChanged?.Invoke(this, EventArgs.Empty);
            }
            return Results.Empty;
        });
    }

    private static string ClientIdOf(HttpContext http)
    {
        var id = http.Request.Headers["X-Client-Id"].ToString();
        return id.Length is > 0 and <= 64 ? id : "";
    }

    private void TouchPeer(HttpContext http)
    {
        var clientId = ClientIdOf(http);
        if (clientId.Length == 0)
            return;
        var name = Uri.UnescapeDataString(http.Request.Headers["X-Client-Name"].ToString());
        var isNew = !_peers.ContainsKey(clientId);
        _peers[clientId] = new PeerInfo(clientId, SyncProtocol.CleanName(name), DateTime.UtcNow, Online: false);
        if (isNew)
            PeersChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task NotifyAsync(string message)
    {
        foreach (var (id, live) in _sockets)
        {
            try { await live.SendAsync(message); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException or InvalidOperationException)
            {
                _sockets.TryRemove(id, out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Say goodbye politely, but never let a half-dead connection stop the host from shutting down.
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

/// <summary>Host-side storage: manifests/{client}.json, requests.json, blobs/{sha256}.</summary>
internal sealed class ServerStore
{
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromDays(30);
    private readonly string _folder;
    private readonly Dictionary<string, Manifest> _manifests = [];
    private readonly List<AudioRequest> _requests = [];
    private readonly object _lock = new();

    public ServerStore(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(Path.Combine(folder, "blobs"));
        Directory.CreateDirectory(Path.Combine(folder, "manifests"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(folder, "manifests"), "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Manifest>(File.ReadAllText(file), SyncProtocol.Json) is { } m)
                    _manifests[m.ClientId] = m;
            }
            catch (JsonException) { }
        }
        try
        {
            var path = Path.Combine(folder, "requests.json");
            if (File.Exists(path) && JsonSerializer.Deserialize<List<AudioRequest>>(File.ReadAllText(path), SyncProtocol.Json) is { } saved)
                _requests.AddRange(saved.Where(r => DateTime.UtcNow - r.CreatedUtc < RequestLifetime));
        }
        catch (JsonException) { }
    }

    public List<Manifest> Manifests()
    {
        lock (_lock)
            return [.. _manifests.Values];
    }

    public void SaveManifest(Manifest manifest)
    {
        lock (_lock)
        {
            _manifests[manifest.ClientId] = manifest;
            WriteAtomically(Path.Combine(_folder, "manifests", Sanitize(manifest.ClientId) + ".json"),
                JsonSerializer.Serialize(manifest, SyncProtocol.Json));
        }
    }

    /// <summary>Adds a request unless the same one is already waiting. Returns true if it's new.</summary>
    public bool AddRequest(AudioRequest request)
    {
        lock (_lock)
        {
            if (_requests.Any(r => r.OwnerId == request.OwnerId && r.RequesterId == request.RequesterId &&
                                   r.Hash.Equals(request.Hash, StringComparison.OrdinalIgnoreCase)))
                return false;
            _requests.Add(request with { Hash = request.Hash.ToLowerInvariant(), CreatedUtc = DateTime.UtcNow });
            SaveRequests();
            return true;
        }
    }

    public List<AudioRequest> RequestsFor(string ownerId)
    {
        lock (_lock)
            return _requests.Where(r => r.OwnerId == ownerId && !HasBlob(r.Hash)).ToList();
    }

    public bool IsRequested(string sha)
    {
        lock (_lock)
            return _requests.Any(r => r.Hash.Equals(sha, StringComparison.OrdinalIgnoreCase));
    }

    public void CompleteRequests(string sha)
    {
        lock (_lock)
        {
            if (_requests.RemoveAll(r => r.Hash.Equals(sha, StringComparison.OrdinalIgnoreCase)) > 0)
                SaveRequests();
        }
    }

    private void SaveRequests() =>
        WriteAtomically(Path.Combine(_folder, "requests.json"), JsonSerializer.Serialize(_requests, SyncProtocol.Json));

    public static bool IsSha(string sha) => SyncProtocol.IsHex(sha, 64);
    public string BlobPath(string sha) => Path.Combine(_folder, "blobs", sha.ToLowerInvariant());
    public bool HasBlob(string sha) => IsSha(sha) && File.Exists(BlobPath(sha));
    public int BlobCount => Directory.EnumerateFiles(Path.Combine(_folder, "blobs")).Count(f => !f.EndsWith(".tmp"));

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

    private static string Sanitize(string s) => string.Concat(s.Where(char.IsLetterOrDigit));

    private static void WriteAtomically(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
