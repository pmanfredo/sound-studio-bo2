using System.Collections.Concurrent;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundStudio.App.Services;
using SoundStudio.Audio;
using SoundStudio.Core;
using SoundStudio.Core.Bo2;
using SoundStudio.Core.Editing;
using SoundStudio.Sync;

namespace SoundStudio.App.ViewModels;

/// <summary>
/// Sync glue. This install publishes what it has changed (a manifest, no audio). Others' manifests are compared
/// with the game here, and only real differences are offered under "Available from others". Get asks the owner
/// for that sound; it's staged under Pending changes when it arrives.
/// </summary>
public sealed partial class MainViewModel
{
    public SyncViewModel Sync { get; private set; } = null!;

    /// <summary>Screenshot/automation mode: don't start hosting/joining from saved settings.</summary>
    public bool SuppressSync { get; set; }

    [RelayCommand]
    private void OpenSync() => Dialogs.ShowSync(Sync);

    // ---- updates -------------------------------------------------------------------------------------------

    private UpdateService? _updates;

    public string VersionText => $"v{UpdateService.CurrentVersion}";

    [ObservableProperty]
    private string? _updateReadyVersion;

    /// <summary>Looks for a new version in the background; never blocks or bothers the user if offline.</summary>
    private async Task CheckForUpdatesAsync()
    {
        _updates = new UpdateService(_settings.UpdateSource is { Length: > 0 } custom ? custom : UpdateService.BuiltInSource);
        if (!_updates.IsEnabled)
            return;
        UpdateReadyVersion = await _updates.CheckAndDownloadAsync();
        if (UpdateReadyVersion != null)
            Dialogs.Notify($"Update {UpdateReadyVersion} is ready", "Click \"Restart to update\" at the bottom right when it suits you.", NoticeKind.Info);
    }

    [RelayCommand]
    private async Task RestartToUpdateAsync()
    {
        if (_updates == null || UpdateReadyVersion == null)
            return;
        if (HasPendingChanges && !await Dialogs.ConfirmAsync("Restart to update?",
                "You have staged changes that aren't applied yet; they'll be lost. Apply them first if you want to keep them.",
                "Restart anyway"))
            return;
        _updates.RestartToUpdate();
    }

    // ---- wiring ----------------------------------------------------------------------------------------------

    private void InitializeSync()
    {
        Sync = new SyncViewModel(_settings)
        {
            ManifestProvider = BuildManifest,
            AudioProvider = request => Library is { } lib ? SyncState.ReadIfCurrent(lib.SoundFolder, request) : null,
            ManifestsHandler = OnManifestsAsync,
            AudioHandler = OnAudioAsync,
        };
        Sync.AutoGetEnabled += (_, _) => _ = RefreshOffersAsync();
    }

    /// <summary>Called after every (re)scan: our state may have changed, and so may what's worth offering.</summary>
    private void OnLibraryStateChanged()
    {
        Sync.ManifestChanged();
        _ = RefreshOffersAsync();
    }

    // ---- this install's manifest -----------------------------------------------------------------------------

    /// <summary>Fingerprints per sound version (bank file + its timestamp + index): computed once, reused.</summary>
    private readonly ConcurrentDictionary<(string Path, long Ticks, int Index), ManifestEntry> _entryCache = new();

    /// <summary>Every modified sound with its fingerprint. Runs on a background thread; reads each sound once.</summary>
    private List<ManifestEntry> BuildManifest()
    {
        var rows = _allRows; // the list is replaced on rescan, never changed in place
        var history = _historyBySound;
        var result = new List<ManifestEntry>();
        foreach (var row in rows.Where(r => r.IsModified))
        {
            var bank = row.Info.Bank;
            var key = (bank.FilePath, bank.LastWriteTimeUtc.Ticks, row.Info.Index);
            try
            {
                var entry = _entryCache.GetOrAdd(key, _ =>
                {
                    var source = history[(row.BankFileName, row.Info.Index)]
                        .Where(h => h.Action == ChangeAction.Replaced && h.Id == row.Info.Id)
                        .OrderBy(h => h.TimeUtc).LastOrDefault()?.Detail;
                    return SyncState.Describe(bank.FilePath, row.Info.Index, row.Info.Id, source);
                });
                result.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException) { }
        }
        return result;
    }

    // ---- what others have ----------------------------------------------------------------------------------

    private IReadOnlyList<Manifest> _others = [];
    private readonly List<SoundRowViewModel> _offerRows = [];
    private LibraryNodeViewModel? _offerNode;
    private readonly ConcurrentDictionary<(string Path, long Ticks, int Index), string> _checksumCache = new();
    private int _offerRefresh;

    [ObservableProperty] private int _offerCount;

    private Task OnManifestsAsync(IReadOnlyList<Manifest> others)
    {
        _others = others;
        return RefreshOffersAsync();
    }

    /// <summary>Compares everyone else's manifest with this game and updates the offers shown.</summary>
    private async Task RefreshOffersAsync()
    {
        if (Library == null)
            return;
        var version = Interlocked.Increment(ref _offerRefresh);
        var rows = _allRows;
        var others = _others;
        var rowByKey = rows.ToDictionary(r => new SoundKey(r.BankFileName, r.Info.Index, r.Info.Id));
        var differences = await Task.Run(() =>
        {
            var mine = BuildManifest().ToDictionary(e => e.Key);
            return SyncState.Compare(mine, others, key => rowByKey.TryGetValue(key, out var row) ? OriginalChecksum(row) : null);
        });
        if (version != _offerRefresh || !ReferenceEquals(rows, _allRows))
            return; // a newer refresh or a rescan superseded this one

        var ignored = _settings.IgnoredOffers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = Sync.Wanted.ToDictionary(w => w.Entry.Key, w => w.Entry.Hash, EqualityComparer<SoundKey>.Default);
        var byKey = differences
            .Where(d => !ignored.Contains(IgnoreKey(d.Theirs)))
            .GroupBy(d => d.Theirs.Key)
            // If several people changed the same sound, offer the most recently updated version.
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => others.First(m => m.ClientId == d.OwnerId).UpdatedUtc).First());

        _offerRows.Clear();
        foreach (var row in rows)
        {
            var key = new SoundKey(row.BankFileName, row.Info.Index, row.Info.Id);
            if (byKey.TryGetValue(key, out var diff))
            {
                var requested = wanted.TryGetValue(key, out var hash) && hash.Equals(diff.Theirs.Hash, StringComparison.OrdinalIgnoreCase);
                if (row.Offer?.Difference != diff)
                    row.Offer = new OfferViewModel(diff, requested, Sync);
                _offerRows.Add(row);
            }
            else if (row.Offer != null)
            {
                row.Offer = null;
            }
        }
        OfferCount = _offerRows.Count;
        foreach (var node in Tree)
            node.RefreshCounts();
        if (SelectedNode == _offerNode)
            await ApplyFilterAsync();

        if (Sync.AutoGet)
            RequestOffers(_offerRows.Where(r => r.Offer is { Requested: false, Difference.Kind: DifferenceKind.TheyChanged }
                                                && r.Pending?.FromAuthor == null).ToList(), quiet: true);
    }

    /// <summary>A sound's original checksum from its bank (cached per bank version).</summary>
    private string? OriginalChecksum(SoundRowViewModel row)
    {
        var bank = row.Info.Bank;
        try
        {
            return _checksumCache.GetOrAdd((bank.FilePath, bank.LastWriteTimeUtc.Ticks, row.Info.Index),
                _ => Convert.ToHexString(SoundReader.Read(bank.FilePath, row.Info.Index, row.Info.Id).Checksum));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException)
        {
            return null;
        }
    }

    private static string IgnoreKey(ManifestEntry e) => $"{e.Bank}|{e.Index}|{e.Id}|{e.Hash}";

    // ---- get / ignore ----------------------------------------------------------------------------------------

    [RelayCommand]
    private void GetSelected()
    {
        if (SelectedRow is { Offer: not null } row)
            RequestOffers([row], quiet: false);
    }

    [RelayCommand]
    private void GetAllShown() =>
        RequestOffers(VisibleRows.Where(r => r.Offer is { Requested: false }).ToList(), quiet: false);

    private void RequestOffers(IReadOnlyList<SoundRowViewModel> rows, bool quiet)
    {
        rows = rows.Where(r => r.Offer is { Requested: false }).ToList();
        if (rows.Count == 0)
            return;
        Sync.Request(rows.Select(r => (r.Offer!.Difference.OwnerId, r.Offer.Difference.OwnerName, r.Offer.Difference.Theirs)));
        foreach (var row in rows)
            row.Offer!.Requested = true;
        if (quiet)
            return;
        var owners = rows.Select(r => r.Offer!.Difference.OwnerName).Distinct().ToList();
        Dialogs.Notify(rows.Count == 1 ? "Requested" : $"Requested {rows.Count} sounds",
            $"They'll appear under Pending changes when {string.Join(" & ", owners)} {(owners.Count == 1 ? "is" : "are")} online.", NoticeKind.Info);
    }

    [RelayCommand]
    private void CancelRequestSelected()
    {
        if (SelectedRow is not { Offer.Requested: true } row)
            return;
        Sync.CancelRequest(row.Offer.Difference.Theirs.Key);
        row.Offer.Requested = false;
    }

    /// <summary>Don't offer this version again (a newer version from them will be offered).</summary>
    [RelayCommand]
    private async Task IgnoreSelectedAsync()
    {
        if (SelectedRow is not { Offer: not null } row)
            return;
        var key = IgnoreKey(row.Offer.Difference.Theirs);
        if (!_settings.IgnoredOffers.Contains(key))
            _settings.IgnoredOffers.Add(key);
        if (row.Offer.Requested)
            Sync.CancelRequest(row.Offer.Difference.Theirs.Key);
        try { _settings.Save(); }
        catch (IOException) { }
        await RefreshOffersAsync();
    }

    // ---- requested audio arrives -----------------------------------------------------------------------------

    private readonly List<string> _arrivedFrom = [];
    private DispatcherTimer? _arrivedNotice;

    private async Task OnAudioAsync(WantedAudio wanted, byte[] data)
    {
        var key = wanted.Entry.Key;
        var row = _allRows.FirstOrDefault(r => r.BankFileName == key.Bank && r.Info.Index == key.Index && r.Info.Id == key.Id);
        if (row == null || !row.CanReplace || row.Info.Format != wanted.Entry.Format || row.Info.Channels != wanted.Entry.Channels)
            return;
        if (row.Pending is { FromAuthor: null })
        {
            Dialogs.Notify($"Kept your own change to {row.Name}", $"{wanted.OwnerName}'s version arrived, but you have your own change staged.", NoticeKind.Warning);
            return;
        }

        try
        {
            var prepared = await Task.Run(() => PrepareReceived(row, data, wanted));
            SetPending(row, new PendingChange(row, prepared) { FromAuthor = SyncProtocol.CleanName(wanted.OwnerName) });
            RefreshPendingState();
            if (row.Offer != null)
                row.Offer.Requested = false;
            if (SelectedRow == row)
                await Player.LoadAsync(row, Player.AutoPlay && !SuppressAutoPlay);

            // One notice for a batch rather than one per sound.
            _arrivedFrom.Add(wanted.OwnerName);
            _arrivedNotice ??= CreateArrivedNotice();
            _arrivedNotice.Stop();
            _arrivedNotice.Start();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            Dialogs.Notify($"Couldn't use {row.Name}", ex.Message, NoticeKind.Warning);
        }
    }

    private DispatcherTimer CreateArrivedNotice()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var count = _arrivedFrom.Count;
            var who = string.Join(" & ", _arrivedFrom.Distinct());
            _arrivedFrom.Clear();
            if (count > 0)
                Dialogs.Notify($"{count} sound{(count == 1 ? "" : "s")} from {who}",
                    "They're under Pending changes. Listen, then Apply to game.", NoticeKind.Info);
        };
        return timer;
    }

    /// <summary>Game-encoded audio from someone else, as a staged replacement (kept as a WAV for trimming etc.).</summary>
    private static PreparedReplacement PrepareReceived(SoundRowViewModel row, byte[] data, WantedAudio wanted)
    {
        var audio = AudioDecoder.Decode(data, row.Info.Format, row.Info.Channels, row.Info.SampleRate);
        var folder = Path.Combine(AppPaths.SyncFolder, "incoming");
        Directory.CreateDirectory(folder);
        var wav = Path.Combine(folder, $"{row.Name} (from {SyncProtocol.CleanName(wanted.OwnerName)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}).wav");
        audio.SaveWav(wav);
        return new PreparedReplacement(wav, audio, data, wanted.Entry.SampleCount, row.Info.Format, audio.Duration);
    }
}

/// <summary>A version of a sound someone else has, shown in the details panel and the "Available" list.</summary>
public sealed partial class OfferViewModel(Difference difference, bool requested, SyncViewModel sync) : ObservableObject
{
    public Difference Difference { get; } = difference;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanGet))]
    private bool _requested = requested;

    public bool CanGet => !Requested;
    public string OwnerName => Difference.OwnerName;

    public string Title => Difference.Kind == DifferenceKind.BothChanged
        ? $"{OwnerName} changed this too"
        : $"Available from {OwnerName}";

    public string Explanation => Difference.Kind == DifferenceKind.BothChanged
        ? $"You both changed this sound, differently. Get {OwnerName}'s version to hear it next to yours."
        : $"{OwnerName} changed this sound; yours is the original.";

    public string TheirVersion
    {
        get
        {
            var e = Difference.Theirs;
            var seconds = e.SampleRate > 0 ? (double)e.SampleCount / e.SampleRate : 0;
            var length = seconds >= 60 ? $"{(int)(seconds / 60)}:{(int)seconds % 60:00}" : $"{seconds:0.00} s";
            return string.IsNullOrWhiteSpace(e.Source) ? length : $"{e.Source} · {length}";
        }
    }

    public string StatusText => Requested
        ? sync.IsOnline(Difference.OwnerId) == false
            ? $"Requested. It arrives when {OwnerName} is online."
            : "Requested…"
        : "";
}
