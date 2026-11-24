using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundStudio.App.Services;
using SoundStudio.Core;
using SoundStudio.Sync;
using Wpf.Ui.Controls;

namespace SoundStudio.App.ViewModels;

public enum SyncMode { Off, Host, Join }

public sealed record RouteItem(string Label, SymbolRegular Icon, string Url);

/// <summary>
/// Sharing sounds with other installs: Host mode (built-in server reachable over LAN, Tailscale, a Cloudflare
/// quick tunnel or the user's own address) or Join mode. Sync compares what each install has and moves audio
/// only when someone asks for it; the main view model supplies this install's state and handles what arrives.
/// </summary>
public sealed partial class SyncViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private SyncServer? _server;
    private SyncClient? _client;
    private QuickTunnel? _tunnel;
    private List<HostRoute> _routes = [];

    public SyncViewModel(AppSettings settings)
    {
        _settings = settings;
        _displayName = settings.DisplayName;
        _shareOverInternet = settings.ShareOverInternet;
        _autoGet = settings.AutoGetFromOthers;
        _hideMine = settings.HideMine;
        _hideFromOthers = settings.HideFromOthers;
        _publicAddress = settings.PublicAddress ?? "";
    }

    public IUserDialogs Dialogs { get; set; } = new SilentDialogs();

    /// <summary>SOUNDSTUDIO_SYNC_LOOPBACK=1: host on 127.0.0.1 only (testing; no firewall prompt).</summary>
    private static bool LoopbackOnly => Environment.GetEnvironmentVariable("SOUNDSTUDIO_SYNC_LOOPBACK") == "1";

    // ---- what the main view model plugs in (see SyncClient) ----

    /// <summary>This install's modified sounds (called on a background thread).</summary>
    public Func<List<ManifestEntry>>? ManifestProvider { get; set; }

    /// <summary>Audio for a sound someone asked for, if this install still has that version (background thread).</summary>
    public Func<AudioRequest, byte[]?>? AudioProvider { get; set; }

    /// <summary>Everyone else's manifests changed (called on the UI thread).</summary>
    public Func<IReadOnlyList<Manifest>, Task>? ManifestsHandler { get; set; }

    /// <summary>Requested audio arrived (called on the UI thread).</summary>
    public Func<WantedAudio, byte[], Task>? AudioHandler { get; set; }

    /// <summary>The install's sounds changed: re-publish what it has.</summary>
    public void ManifestChanged() => _client?.ManifestChanged();

    /// <summary>Asks other installs for these sounds; they arrive when their owner is online.</summary>
    public void Request(IEnumerable<(string OwnerId, string OwnerName, ManifestEntry Entry)> sounds)
    {
        _client?.Request(sounds);
        UpdateStatus();
    }

    public void CancelRequest(SoundKey key)
    {
        _client?.CancelRequest(key);
        UpdateStatus();
    }

    public IReadOnlyList<WantedAudio> Wanted => _client?.Wanted ?? [];

    /// <summary>Whether another install is connected right now (when the host knows).</summary>
    public bool? IsOnline(string clientId) =>
        Peers.FirstOrDefault(p => p.ClientId == clientId) is { } peer ? peer.Online : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOff), nameof(IsHost), nameof(IsJoined))]
    private SyncMode _mode = SyncMode.Off;

    public bool IsOff => Mode == SyncMode.Off;
    public bool IsHost => Mode == SyncMode.Host;
    public bool IsJoined => Mode == SyncMode.Join;

    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string _statusText = "Sync off";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _inviteCode;
    [ObservableProperty] private string _joinCodeText = "";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _shareOverInternet;
    [ObservableProperty] private string? _tunnelStatus;
    /// <summary>Fetch everything others changed automatically (it still waits under Pending changes).</summary>
    [ObservableProperty] private bool _autoGet;
    /// <summary>Others don't see your sounds' names and can't hear them until they apply them.</summary>
    [ObservableProperty] private bool _hideMine;
    /// <summary>Don't show or play what others send until it's applied.</summary>
    [ObservableProperty] private bool _hideFromOthers;

    public event EventHandler? HideFromOthersChanged;

    partial void OnHideMineChanged(bool value)
    {
        _settings.HideMine = value;
        SaveSettings();
        _client?.ManifestChanged();
    }

    partial void OnHideFromOthersChanged(bool value)
    {
        _settings.HideFromOthers = value;
        SaveSettings();
        HideFromOthersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Optional stable address (named Cloudflare tunnel, reverse proxy...). Empty = not used.</summary>
    [ObservableProperty] private string _publicAddress;
    [ObservableProperty] private string? _publicAddressStatus;
    [ObservableProperty] private bool _publicAddressOk;

    /// <summary>The local address a tunnel or proxy should forward to.</summary>
    public string ForwardTarget => $"http://localhost:{(_server?.Port is > 0 and var p ? p : _settings.HostPort)}";

    partial void OnPublicAddressChanged(string value)
    {
        var normalized = Reachability.NormalizePublicUrl(value, out var error);
        PublicAddressOk = false;
        PublicAddressStatus = error;
        if (error != null)
            return;
        _settings.PublicAddress = normalized;
        SaveSettings();
        if (normalized != null && normalized != value)
            PublicAddress = normalized; // show the cleaned-up form
        if (IsHost)
            Forget(RefreshRoutesAsync());
    }

    /// <summary>Checks the stable address really reaches this PC's sync server (and not something else).</summary>
    [RelayCommand]
    private async Task CheckPublicAddressAsync()
    {
        var url = _settings.PublicAddress;
        if (url == null)
            return;
        if (_server == null)
        {
            PublicAddressStatus = "Start hosting first, then check.";
            return;
        }
        PublicAddressStatus = "Checking…";
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = await http.GetStringAsync(url + "/api/hello");
            var hello = System.Text.Json.JsonSerializer.Deserialize<HostHello>(json, SyncProtocol.Json);
            PublicAddressOk = hello?.HostId == _settings.HostId;
            PublicAddressStatus = PublicAddressOk
                ? "Works: others can reach you at this address."
                : "Something answered, but it isn't this PC's Sound Studio. Check where the address forwards to.";
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            PublicAddressStatus = $"Couldn't reach it ({ex.Message}). Make sure it forwards to {ForwardTarget}.";
        }
    }

    public ObservableCollection<RouteItem> Routes { get; } = [];
    public ObservableCollection<PeerInfo> Peers { get; } = [];

    /// <summary>Short text for the toolbar chip.</summary>
    public string ChipText => Mode switch
    {
        SyncMode.Host => Peers.Count(p => p.Online) switch { 0 => "Hosting", var n => $"Hosting · {n} connected" },
        SyncMode.Join => IsConnected ? $"Synced with {_settings.JoinHostName}" : "Sync offline",
        _ => "Share",
    };

    partial void OnDisplayNameChanged(string value)
    {
        _settings.DisplayName = string.IsNullOrWhiteSpace(value) ? Environment.UserName : value.Trim();
        SaveSettings();
    }

    /// <summary>Raised when auto-get is switched on, so waiting differences can be fetched right away.</summary>
    public event EventHandler? AutoGetEnabled;

    partial void OnAutoGetChanged(bool value)
    {
        _settings.AutoGetFromOthers = value;
        SaveSettings();
        if (value)
            AutoGetEnabled?.Invoke(this, EventArgs.Empty);
    }

    partial void OnShareOverInternetChanged(bool value)
    {
        _settings.ShareOverInternet = value;
        SaveSettings();
        if (IsHost)
            Forget(value ? StartTunnelAsync() : StopTunnelAsync());
    }

    partial void OnModeChanged(SyncMode value) => OnPropertyChanged(nameof(ChipText));
    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(ChipText));

    // ---- lifecycle -----------------------------------------------------------------------------------------

    /// <summary>Resumes hosting/joining from settings at startup.</summary>
    public async Task RestoreAsync()
    {
        if (_settings.SyncRole == "Host")
            await StartHostingAsync();
        else if (_settings.SyncRole == "Join" && _settings.JoinHostId != null && _settings.JoinToken != null)
            StartClient(new SyncConnection(_settings.JoinHostId, _settings.JoinHostName ?? "Host", _settings.JoinToken, [.. _settings.JoinUrls]));
    }

    [RelayCommand]
    private async Task StartHostingAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        ErrorText = null;
        try
        {
            _settings.HostId ??= Guid.NewGuid().ToString("N");
            _settings.HostToken ??= SyncProtocol.NewToken();
            _server = new SyncServer(new SyncServerOptions
            {
                HostId = _settings.HostId,
                HostName = _settings.DisplayName,
                Token = _settings.HostToken,
                DataFolder = Path.Combine(AppPaths.SyncFolder, "server"),
                Port = _settings.HostPort,
                LoopbackOnly = LoopbackOnly,
            });
            _server.ExtraUrls = () => _routes.Select(r => r.Url).ToList();
            _server.PeersChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(RefreshPeers);
            await _server.StartAsync();

            // The host syncs with its own server like everyone else.
            StartClient(new SyncConnection(_settings.HostId, _settings.DisplayName, _settings.HostToken, [$"http://127.0.0.1:{_server.Port}"]));
            Mode = SyncMode.Host;
            _settings.SyncRole = "Host";
            SaveSettings();
            await RefreshRoutesAsync();
            if (ShareOverInternet)
                await StartTunnelAsync();
        }
        catch (IOException ex)
        {
            ErrorText = $"Couldn't start hosting on port {_settings.HostPort}: {ex.Message}. Is another copy of Sound Studio hosting?";
            await StopAllAsync();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorText = "Couldn't start hosting: " + ex.Message;
            await StopAllAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task JoinAsync()
    {
        ErrorText = null;
        if (!SoundStudio.Sync.InviteCode.TryParse(JoinCodeText, out var code) || code == null)
        {
            ErrorText = "That doesn't look like an invite code. It starts with \"SS1.\"; ask the host to copy it again.";
            return;
        }
        await StopAllAsync();
        _settings.JoinHostId = code.HostId;
        _settings.JoinHostName = code.HostName;
        _settings.JoinToken = code.Token;
        _settings.JoinUrls = [.. code.Urls];
        _settings.SyncRole = "Join";
        SaveSettings();
        JoinCodeText = "";
        StartClient(SyncConnection.FromInvite(code));
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (IsHost && Peers.Count > 0 && !await Dialogs.ConfirmAsync("Stop hosting?",
                "Others can't sync until you host again. Their invite code will still work when you do.", "Stop hosting"))
            return;
        await StopAllAsync();
        _settings.SyncRole = "Off";
        SaveSettings();
    }

    /// <summary>New secret: old invite codes stop working (e.g. a code was shared by mistake).</summary>
    [RelayCommand]
    private async Task ResetInviteAsync()
    {
        if (!await Dialogs.ConfirmAsync("Make a new invite code?",
                "Everyone using the current code will be disconnected until they join with the new one.", "New code"))
            return;
        _settings.HostToken = SyncProtocol.NewToken();
        SaveSettings();
        await StopAllAsync();
        await StartHostingAsync();
    }

    [RelayCommand]
    private void CopyInvite()
    {
        if (InviteCode == null)
            return;
        Clipboard.SetText(InviteCode);
        Dialogs.Notify("Invite code copied", "Send it to the person you want to share with; they paste it under Join.");
    }

    [RelayCommand]
    private void SyncNow() => _client?.Kick();

    private void StartClient(SyncConnection connection)
    {
        _client = new SyncClient(connection, _settings.ClientId, _settings.DisplayName, AppPaths.SyncFolder)
        {
            ManifestProvider = () => ManifestProvider?.Invoke() ?? [],
            AudioProvider = r => AudioProvider?.Invoke(r),
            OnManifests = manifests =>
            {
                var handler = ManifestsHandler;
                return handler == null ? Task.CompletedTask : Application.Current.Dispatcher.InvokeAsync(() => handler(manifests)).Task.Unwrap();
            },
            OnAudio = (wanted, data) =>
            {
                var handler = AudioHandler;
                return handler == null ? Task.CompletedTask : Application.Current.Dispatcher.InvokeAsync(() => handler(wanted, data)).Task.Unwrap();
            },
        };
        _client.StatusChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(UpdateStatus);
        _client.HostDetailsReceived += (_, details) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (Mode == SyncMode.Join)
            {
                _settings.JoinUrls = [.. _client!.Connection.Urls];
                SaveSettings();
                SetPeers(details.Peers);
            }
        });
        _client.Start();

        // Joiners never listen for anything, so Windows never asks them about the firewall. If the host's
        // home-network address changes, they reconnect via Tailscale/internet and learn the new one.
        if (Mode != SyncMode.Host && connection.HostId == _settings.JoinHostId)
            Mode = SyncMode.Join;
        UpdateStatus();
    }

    private async Task StopAllAsync()
    {
        await StopTunnelAsync();
        if (_client != null)
            await _client.DisposeAsync();
        if (_server != null)
            await _server.DisposeAsync();
        _client = null;
        _server = null;
        _routes = [];
        Routes.Clear();
        Peers.Clear();
        InviteCode = null;
        Mode = SyncMode.Off;
        UpdateStatus();
    }

    // ---- host: addresses and tunnel ---------------------------------------------------------------------

    private async Task RefreshRoutesAsync()
    {
        if (_server == null)
            return;
        _routes = LoopbackOnly
            ? [new HostRoute(RouteKind.Lan, $"http://127.0.0.1:{_server.Port}", "This PC only (test mode)")]
            : await Reachability.RoutesAsync(_server.Port, _tunnel?.Url, publicUrl: _settings.PublicAddress);
        OnPropertyChanged(nameof(ForwardTarget));
        Routes.Clear();
        foreach (var r in _routes)
        {
            var icon = r.Kind switch
            {
                RouteKind.Lan => SymbolRegular.Home24,
                RouteKind.Tailscale => SymbolRegular.ShieldLock24,
                _ => SymbolRegular.Globe24,
            };
            Routes.Add(new RouteItem(r.Label, icon, r.Url));
        }
        InviteCode = _routes.Count == 0
            ? null
            : new SoundStudio.Sync.InviteCode(_settings.HostId!, _settings.DisplayName, _settings.HostToken!, _routes.Select(r => r.Url).ToList()).ToString();
        UpdateStatus();
    }

    private async Task StartTunnelAsync()
    {
        if (_server == null || _tunnel != null)
            return;
        var exe = QuickTunnel.FindCloudflared(_settings.CloudflaredPath);
        if (exe == null)
        {
            TunnelStatus = "cloudflared isn't installed. Get it from Cloudflare (developers.cloudflare.com → cloudflared) or turn this off and use Tailscale.";
            return;
        }
        TunnelStatus = "Starting Cloudflare tunnel…";
        _tunnel = new QuickTunnel();
        _tunnel.Exited += (_, _) => Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            if (_tunnel != null && ShareOverInternet)
            {
                TunnelStatus = "The Cloudflare tunnel stopped.";
                _tunnel = null;
                await RefreshRoutesAsync();
            }
        });
        try
        {
            var url = await _tunnel.StartAsync(exe, _server.Port);
            TunnelStatus = "Internet address is ready. It changes each time you host, so send the new code to anyone not on your network or Tailscale.";
            _ = url;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            TunnelStatus = "Couldn't start the tunnel: " + ex.Message;
            await _tunnel.DisposeAsync();
            _tunnel = null;
        }
        await RefreshRoutesAsync();
    }

    private async Task StopTunnelAsync()
    {
        if (_tunnel == null)
            return;
        var t = _tunnel;
        _tunnel = null;
        await t.DisposeAsync();
        TunnelStatus = null;
        await RefreshRoutesAsync();
    }

    private void RefreshPeers()
    {
        if (_server != null)
            SetPeers(_server.Peers);
    }

    private void SetPeers(IEnumerable<PeerInfo> peers)
    {
        Peers.Clear();
        foreach (var p in peers.Where(p => p.ClientId != _settings.ClientId).OrderByDescending(p => p.Online).ThenBy(p => p.Name))
            Peers.Add(p);
        OnPropertyChanged(nameof(ChipText));
        UpdateStatus();
    }

    // ---- status --------------------------------------------------------------------------------------------

    private void UpdateStatus()
    {
        var c = _client;
        IsConnected = c?.Status == SyncStatus.Connected;
        var waiting = c?.Wanted.Count ?? 0;
        var waitingText = waiting > 0 ? $" · waiting for {waiting} requested sound{(waiting == 1 ? "" : "s")}" : "";
        var online = Peers.Count(p => p.Online);
        (StatusText, DetailText) = Mode switch
        {
            SyncMode.Host => (Peers.Count == 0 ? "Hosting · waiting for others to join" : $"Hosting · {online} of {Peers.Count} connected",
                $"Others can see which sounds you've changed and ask for them{waitingText}."),
            SyncMode.Join => c?.Status switch
            {
                SyncStatus.Connected => ($"Connected to {_settings.JoinHostName} via {RouteName(c.ActiveUrl)}",
                    (c.LastSyncUtc is { } t ? $"Last synced {t.ToLocalTime():HH:mm:ss}" : "Synced") + waitingText),
                SyncStatus.Offline => ($"Can't reach {_settings.JoinHostName}, retrying",
                    (c.LastError ?? "") + waitingText),
                _ => ($"Connecting to {_settings.JoinHostName}…", waitingText),
            },
            _ => ("Sync off", "Host to share your sounds, or join someone else with their invite code."),
        };
        OnPropertyChanged(nameof(ChipText));
    }

    private static string RouteName(string? url) => url == null ? "?" : Reachability.Classify(url) switch
    {
        RouteKind.Lan => "your home network",
        RouteKind.Tailscale => "Tailscale",
        _ => "the internet",
    };

    public bool IsActive => _client != null;

    /// <summary>Connection state and queue, for diagnostics.</summary>
    public string Diagnostics => $"status={_client?.Status} url={_client?.ActiveUrl} error={_client?.LastError} wanted={_client?.Wanted.Count}";

    /// <summary>Runs a background task, turning failures into a visible message instead of a crash.</summary>
    private async void Forget(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { ErrorText = ex.Message; }
    }

    private void SaveSettings()
    {
        try { _settings.Save(); }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync() => await StopAllAsync();
}
