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
/// Sharing with friends: Host mode (built-in server + LAN beacon + Tailscale + optional Cloudflare quick
/// tunnel) or Join mode (client). Incoming changes are handed to <see cref="IncomingHandler"/>.
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
        _autoApplyIncoming = settings.AutoApplyIncoming;
        _publicAddress = settings.PublicAddress ?? "";
    }

    public IUserDialogs Dialogs { get; set; } = new SilentDialogs();

    /// <summary>SOUNDSTUDIO_SYNC_LOOPBACK=1: host on 127.0.0.1 only (testing; no firewall prompt).</summary>
    private static bool LoopbackOnly => Environment.GetEnvironmentVariable("SOUNDSTUDIO_SYNC_LOOPBACK") == "1";

    /// <summary>Set by the main view model: shares every modified sound.</summary>
    public Func<Task<int>>? ShareAllHandler { get; set; }

    [RelayCommand]
    private Task ShareAllAsync() => ShareAllHandler?.Invoke() ?? Task.FromResult(0);

    /// <summary>Set by the main view model: stages (or applies) received changes.</summary>
    public Func<IReadOnlyList<IncomingChange>, Task>? IncomingHandler { get; set; }

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
    [ObservableProperty] private bool _autoApplyIncoming;

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
                ? "Works: friends can reach you at this address."
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
        SyncMode.Host => Peers.Count switch { 0 => "Hosting", 1 => "Hosting · 1 friend", var n => $"Hosting · {n} friends" },
        SyncMode.Join => IsConnected ? $"Synced with {_settings.JoinHostName}" : "Sync offline",
        _ => "Share",
    };

    partial void OnDisplayNameChanged(string value)
    {
        _settings.DisplayName = string.IsNullOrWhiteSpace(value) ? Environment.UserName : value.Trim();
        SaveSettings();
    }

    partial void OnAutoApplyIncomingChanged(bool value)
    {
        _settings.AutoApplyIncoming = value;
        SaveSettings();
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
                "Your friends won't receive changes until you host again. Their invite code will still work when you do.", "Stop hosting"))
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
        Dialogs.Notify("Invite code copied", "Send it to your friend; they paste it under Join.");
    }

    [RelayCommand]
    private void SyncNow() => _client?.Kick();

    private void StartClient(SyncConnection connection)
    {
        _client = new SyncClient(connection, _settings.ClientId, _settings.DisplayName, AppPaths.SyncFolder)
        {
            OnChanges = changes =>
            {
                var handler = IncomingHandler;
                return handler == null ? Task.CompletedTask : Application.Current.Dispatcher.InvokeAsync(() => handler(changes)).Task.Unwrap();
            },
        };
        _client.StatusChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(UpdateStatus);
        _client.HostDetailsReceived += (_, details) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (Mode == SyncMode.Join)
            {
                _settings.JoinUrls = [.. _client!.Connection.Urls];
                SaveSettings();
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
            TunnelStatus = "Internet address is ready. It changes each time you host, so send the new code if a friend isn't on your network or Tailscale.";
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
        Peers.Clear();
        if (_server != null)
            foreach (var p in _server.Peers.Where(p => p.ClientId != _settings.ClientId).OrderBy(p => p.Name))
                Peers.Add(p);
        OnPropertyChanged(nameof(ChipText));
        UpdateStatus();
    }

    // ---- status --------------------------------------------------------------------------------------------

    private void UpdateStatus()
    {
        var c = _client;
        IsConnected = c?.Status == SyncStatus.Connected;
        var waiting = c?.OutboxCount ?? 0;
        var waitingText = waiting > 0 ? $" · {waiting} change{(waiting == 1 ? "" : "s")} waiting to send" : "";
        (StatusText, DetailText) = Mode switch
        {
            SyncMode.Host => (Peers.Count == 0 ? "Hosting · waiting for friends to join" : $"Hosting · {Peers.Count} connected",
                $"Changes you apply are shared automatically{waitingText}."),
            SyncMode.Join => c?.Status switch
            {
                SyncStatus.Connected => ($"Connected to {_settings.JoinHostName} via {RouteName(c.ActiveUrl)}",
                    (c.LastSyncUtc is { } t ? $"Last synced {t.ToLocalTime():HH:mm:ss}" : "Synced") + waitingText),
                SyncStatus.Offline => ($"Can't reach {_settings.JoinHostName}, retrying",
                    (c.LastError ?? "") + waitingText),
                _ => ($"Connecting to {_settings.JoinHostName}…", waitingText),
            },
            _ => ("Sync off", "Host to share your changes, or join a friend with their invite code."),
        };
        OnPropertyChanged(nameof(ChipText));
    }

    private static string RouteName(string? url) => url == null ? "?" : Reachability.Classify(url) switch
    {
        RouteKind.Lan => "your home network",
        RouteKind.Tailscale => "Tailscale",
        _ => "the internet",
    };

    // ---- outgoing ------------------------------------------------------------------------------------------

    public bool CanPublish => _client != null;

    /// <summary>Last connection error and queue size, for diagnostics.</summary>
    public string Diagnostics => $"status={_client?.Status} url={_client?.ActiveUrl} error={_client?.LastError} outbox={_client?.OutboxCount} seq={_client?.LastSeq}";

    public void Publish(IEnumerable<(SyncChange Change, byte[]? Data)> changes)
    {
        if (_client == null)
            return;
        var list = changes.ToList();
        if (list.Count == 0)
            return;
        _client.Publish(list);
        UpdateStatus();
    }

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
