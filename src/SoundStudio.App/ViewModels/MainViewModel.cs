using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundStudio.App.Services;
using SoundStudio.Core;
using SoundStudio.Core.Bo2;
using SoundStudio.Core.Editing;
using SoundStudio.Core.Library;
using Wpf.Ui.Controls;

namespace SoundStudio.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly LibraryScanner _scanner = new(AppPaths.ScanCacheFile);
    private readonly BankEditor _editor;
    private readonly DispatcherTimer _searchDebounce;
    private CancellationTokenSource? _filterCts;
    private List<SoundRowViewModel> _allRows = [];
    private ILookup<(string Bank, int Index), ChangeRecord> _historyBySound = Array.Empty<ChangeRecord>().ToLookup(r => (r.Bank, r.Index));

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _editor = new BankEditor(
            new OriginalsStore(AppPaths.OriginalsFolder),
            new ChangeHistory(AppPaths.HistoryFile),
            settings.DisplayName);
        Player = new PlayerViewModel(settings, _editor.Originals);
        InitializeSync();
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); _ = ApplyFilterAsync(); };
    }

    /// <summary>Set by the window once it exists.</summary>
    public IUserDialogs Dialogs
    {
        get => _dialogs;
        set { _dialogs = value; Player.Dialogs = value; Sync.Dialogs = value; }
    }
    private IUserDialogs _dialogs = new SilentDialogs();

    public PlayerViewModel Player { get; }

    /// <summary>Screenshot/automation mode: load sounds into the player but never start playback.</summary>
    public bool SuppressAutoPlay
    {
        get => !Player.AllowAutoPlay;
        set => Player.AllowAutoPlay = !value;
    }

    // ---- state -----------------------------------------------------------------------------------------

    public ObservableCollection<LibraryNodeViewModel> Tree { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoadingOverlay))]
    private GameLibrary? _library;

    [ObservableProperty] private string? _soundFolder;
    [ObservableProperty] private bool _needsFolder;
    [ObservableProperty] private string? _folderError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoadingOverlay))]
    private bool _isScanning;

    [ObservableProperty] private double _scanProgress;
    [ObservableProperty] private string _scanStatus = "";

    /// <summary>True while writing to game files; blocks the UI with a short message.</summary>
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private IReadOnlyList<SoundRowViewModel> _visibleRows = [];

    /// <summary>Full-screen progress only for the very first load; rescans keep the library visible.</summary>
    public bool ShowLoadingOverlay => IsScanning && Library == null;

    public bool HasNoResults => Library != null && VisibleRows.Count == 0;

    /// <summary>Offers in the current list that haven't been requested yet (for "Get N shown").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOffersShown))]
    private int _offersShownCount;
    public bool HasOffersShown => OffersShownCount > 0;

    /// <summary>Empty-list message: different when nothing is searched (e.g. no modified sounds left).</summary>
    public string EmptyTitle => IsSearching ? "No sounds match" : SelectedNode?.Kind switch
    {
        LibraryNodeKind.Pending => "No staged changes",
        LibraryNodeKind.Available => "Nothing new from others",
        _ => "Nothing modified here",
    };
    public string EmptyHint => IsSearching
        ? "Try fewer words, or part of a name like \"death\" or \"vox\"."
        : SelectedNode?.Kind switch
        {
            LibraryNodeKind.Pending => "Drag an audio file onto any sound, or use Replace from folder.",
            LibraryNodeKind.Available => Sync.IsActive
                ? "When someone you sync with changes a sound you don't have, it shows up here."
                : "Use Share at the top to host or join; sounds others have changed show up here.",
            _ => "Every sound in this list is the original game audio.",
        };
    private bool IsSearching => GameLibrary.ParseQuery(SearchText).Length > 0;
    [ObservableProperty] private string _resultText = "";

    /// <summary>Modified sounds in the current list that have a saved original.</summary>
    [ObservableProperty] private int _revertableVisibleCount;

    /// <summary>Modified sounds anywhere whose original audio isn't saved yet.</summary>
    [ObservableProperty] private int _missingOriginalsCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private SoundRowViewModel? _selectedRow;

    public bool HasSelection => SelectedRow != null;

    public ObservableCollection<HistoryItemViewModel> SelectedHistory { get; } = [];

    [ObservableProperty] private bool _hasSelectedHistory;

    [ObservableProperty] private LibraryNodeViewModel? _selectedNode;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _modifiedOnly;

    public string SummaryText => Library is { } lib
        ? $"{lib.Banks.Count} banks · {lib.AllSounds.Count:N0} sounds · {lib.ModifiedCount:N0} modified"
        : "";

    partial void OnLibraryChanged(GameLibrary? value) => OnPropertyChanged(nameof(SummaryText));
    partial void OnSelectedNodeChanged(LibraryNodeViewModel? value) => _ = ApplyFilterAsync();
    partial void OnModifiedOnlyChanged(bool value) => _ = ApplyFilterAsync();
    partial void OnSelectedRowChanged(SoundRowViewModel? value)
    {
        LoadSelectedHistory();
        LoadVolumeForSelection();
        if (!ReferenceEquals(value, Player.Row))
            _ = Player.LoadAsync(value, Player.AutoPlay && !SuppressAutoPlay);
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    // ---- startup / folder --------------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        _ = Task.Run(SoundNameDatabase.Preload);

        var folder = _settings.SoundFolder;
        if (!LibraryScanner.LooksLikeSoundFolder(folder))
            folder = AppPaths.CandidateSoundFolders().FirstOrDefault(LibraryScanner.LooksLikeSoundFolder);

        if (folder == null)
        {
            NeedsFolder = true;
            return;
        }
        await LoadFolderAsync(folder);
        if (!SuppressSync)
        {
            await Sync.RestoreAsync();
            _ = CheckForUpdatesAsync();
        }
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var folder = Dialogs.PickFolder("Select the Black Ops II \"sound\" folder",
            SoundFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        if (folder == null)
            return;

        folder = ResolveSoundFolder(folder);
        if (folder == null)
        {
            FolderError = "That folder has no .sabs or .sabl sound banks. Pick the game's \"sound\" folder.";
            NeedsFolder = true;
            return;
        }
        await LoadFolderAsync(folder);
    }

    /// <summary>Accepts either the sound folder itself or the game folder that contains it.</summary>
    private static string? ResolveSoundFolder(string folder)
    {
        if (LibraryScanner.LooksLikeSoundFolder(folder))
            return folder;
        var sub = Path.Combine(folder, "sound");
        return LibraryScanner.LooksLikeSoundFolder(sub) ? sub : null;
    }

    [RelayCommand]
    private Task RescanAsync() => SoundFolder != null ? LoadFolderAsync(SoundFolder) : Task.CompletedTask;

    private async Task LoadFolderAsync(string folder)
    {
        if (IsScanning)
            return;

        FolderError = null;
        NeedsFolder = false;
        SoundFolder = folder;
        _settings.SoundFolder = folder;
        TrySaveSettings();

        // Keep the user's place across rescans.
        var nodePath = SelectedNode != null ? NodePath(SelectedNode) : null;
        var selectedKey = SelectedRow != null ? (SelectedRow.BankFileName, SelectedRow.Info.Index) : default;

        IsScanning = true;
        ScanProgress = 0;
        ScanStatus = "Reading sound banks…";
        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                ScanProgress = 100.0 * p.BanksDone / Math.Max(1, p.BankCount);
                ScanStatus = p.FromCache
                    ? $"Loading {p.BanksDone}/{p.BankCount} banks…"
                    : $"Checking for modified sounds {p.BanksDone}/{p.BankCount}: {p.CurrentBank}";
            });
            var library = await _scanner.ScanAsync(folder, progress);
            await Task.Run(SoundNameDatabase.Preload);
            await LoadHistoryAsync();
            BuildTree(library, nodePath);
            Library = library;

            if (selectedKey.BankFileName != null)
            {
                await ApplyFilterAsync();
                SelectedRow = VisibleRows.FirstOrDefault(r => r.BankFileName == selectedKey.BankFileName && r.Info.Index == selectedKey.Index);
            }

            OnLibraryStateChanged();
            var failed = library.Banks.Count(b => b.Error != null);
            ScanStatus = failed > 0 ? $"{failed} bank(s) could not be read" : "Ready";
        }
        catch (Exception ex)
        {
            ScanStatus = "Scan failed: " + ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void TrySaveSettings()
    {
        try { _settings.Save(); }
        catch (IOException) { /* not fatal */ }
    }

    // ---- tree --------------------------------------------------------------------------------------------

    private void BuildTree(GameLibrary library, IReadOnlyList<string>? restorePath)
    {
        // Staged changes survive a rescan (e.g. after reverting other sounds).
        var staged = _pendingRows.ToDictionary(r => (r.BankFileName, r.Info.Index, r.Info.Id), r => r.Pending!);
        _pendingRows.Clear();

        var rowsByBank = library.Banks.ToDictionary(
            b => b,
            b => b.Sounds.Select(s => new SoundRowViewModel(s, b.Description.MapName)).ToList());
        _allRows = rowsByBank.Values.SelectMany(r => r).ToList();
        RefreshOriginalFlags();
        foreach (var row in _allRows)
        {
            if (staged.TryGetValue((row.BankFileName, row.Info.Index, row.Info.Id), out var change))
                SetPending(row, change.With(change.Prepared, row));
        }

        Tree.Clear();
        var all = new LibraryNodeViewModel(LibraryNodeKind.AllSounds, "All sounds", SymbolRegular.Library24, _allRows);
        Tree.Add(all);
        Tree.Add(new LibraryNodeViewModel(LibraryNodeKind.Modified, "Modified", SymbolRegular.Edit24,
            _allRows.Where(r => r.IsModified).ToList())
        {
            ToolTip = "Sounds whose audio differs from the original game audio",
        });
        _pendingNode = new LibraryNodeViewModel(LibraryNodeKind.Pending, "Pending changes", SymbolRegular.ArrowSync24, _pendingRows)
        {
            ToolTip = "Replacements you've staged but not yet applied to the game",
        };
        Tree.Add(_pendingNode);
        _offerRows.Clear();
        _offerNode = new LibraryNodeViewModel(LibraryNodeKind.Available, "Available from others", SymbolRegular.CloudArrowDown24, _offerRows)
        {
            ToolTip = "Sounds that people you sync with have changed and you don't have yet",
        };
        Tree.Add(_offerNode);

        foreach (var category in library.Banks.GroupBy(b => b.Description.Category).OrderBy(g => g.Key))
        {
            var categoryRows = category.SelectMany(b => rowsByBank[b]).ToList();
            var categoryNode = new LibraryNodeViewModel(LibraryNodeKind.Category,
                BankNaming.CategoryName(category.Key), CategoryIcon(category.Key), categoryRows)
            {
                IsExpanded = category.Key == BankCategory.Zombies,
            };

            foreach (var map in category.GroupBy(b => b.Description.MapName).OrderBy(g => g.Key))
            {
                var banks = map.OrderBy(b => b.Description.Part).ThenBy(b => b.Description.Language)
                    .ThenBy(b => b.Description.IsStreamed).ToList();
                if (banks.Count == 1)
                {
                    categoryNode.Children.Add(BankNode(banks[0], rowsByBank[banks[0]], map.Key, banks[0].Description.ShortLabel));
                    continue;
                }

                var mapNode = new LibraryNodeViewModel(LibraryNodeKind.Map, map.Key, SymbolRegular.Folder24,
                    banks.SelectMany(b => rowsByBank[b]).ToList());
                foreach (var bank in banks)
                    mapNode.Children.Add(BankNode(bank, rowsByBank[bank], bank.Description.ShortLabel, null));
                categoryNode.Children.Add(mapNode);
            }
            Tree.Add(categoryNode);
        }

        Tree.Add(BuildTypeTree());

        var target = restorePath != null ? FindByPath(restorePath) : null;
        target ??= all;
        for (var parent = FindParent(target); parent != null; parent = FindParent(parent))
            parent.IsExpanded = true;
        target.IsSelected = true;
        SelectedNode = target;
        RefreshPendingState();
    }

    /// <summary>
    /// "By type": every sound grouped by what it is (from its game asset path) instead of which bank it's in,
    /// e.g. Voices → zmb_tomb, Weapons → lmg. Small subgroups are folded into "other" to keep the tree short.
    /// </summary>
    private LibraryNodeViewModel BuildTypeTree()
    {
        var root = new LibraryNodeViewModel(LibraryNodeKind.TypeRoot, "By type", SymbolRegular.Tag24, _allRows)
        {
            ToolTip = "Sounds grouped by what they are, from the game's own folder names",
        };
        foreach (var group in _allRows.GroupBy(r => r.Info.Type.Group).OrderByDescending(g => g.Count()))
        {
            var rows = group.ToList();
            var groupNode = new LibraryNodeViewModel(LibraryNodeKind.TypeGroup, group.Key, TypeIcon(group.Key), rows);
            var subgroups = rows.GroupBy(r => r.Info.Type.Subgroup).OrderByDescending(g => g.Count()).ToList();
            if (subgroups.Count > 1)
            {
                var small = new List<SoundRowViewModel>();
                foreach (var sub in subgroups)
                {
                    if (sub.Count() < 5 || sub.Key.Length == 0)
                    {
                        small.AddRange(sub);
                        continue;
                    }
                    groupNode.Children.Add(new LibraryNodeViewModel(LibraryNodeKind.TypeSubgroup, sub.Key, SymbolRegular.Folder24, sub.ToList()));
                }
                if (small.Count > 0)
                    groupNode.Children.Add(new LibraryNodeViewModel(LibraryNodeKind.TypeSubgroup, "other", SymbolRegular.Folder24, small));
            }
            root.Children.Add(groupNode);
        }
        return root;
    }

    private static SymbolRegular TypeIcon(string group) => group switch
    {
        "Voices" => SymbolRegular.PersonVoice24,
        "Music" => SymbolRegular.MusicNote224,
        "Weapons" or "Bullets & projectiles" or "Explosions" => SymbolRegular.Target24,
        "Ambience" => SymbolRegular.WeatherPartlyCloudyDay24,
        "Zombies (creatures & level)" => SymbolRegular.Fire24,
        "Footsteps & movement" => SymbolRegular.Run24,
        "Menus & UI" => SymbolRegular.Apps24,
        _ => SymbolRegular.Tag24,
    };

    private static LibraryNodeViewModel BankNode(BankInfo bank, IReadOnlyList<SoundRowViewModel> rows, string title, string? subtitle) =>
        new(LibraryNodeKind.Bank, title, bank.Description.IsStreamed ? SymbolRegular.Album24 : SymbolRegular.MusicNote224, rows)
        {
            Bank = bank,
            Subtitle = subtitle,
            ToolTip = bank.Error != null ? $"{bank.FileName}\nCould not be read: {bank.Error}" : bank.FileName,
        };

    private static SymbolRegular CategoryIcon(BankCategory category) => category switch
    {
        BankCategory.Zombies => SymbolRegular.Fire24,
        BankCategory.Multiplayer => SymbolRegular.Target24,
        BankCategory.Common => SymbolRegular.Layer24,
        BankCategory.Weapons => SymbolRegular.Wrench24,
        _ => SymbolRegular.Folder24,
    };

    public IEnumerable<LibraryNodeViewModel> AllNodes() => Flatten(Tree);

    private static IEnumerable<LibraryNodeViewModel> Flatten(IEnumerable<LibraryNodeViewModel> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    public LibraryNodeViewModel? FindParent(LibraryNodeViewModel child) =>
        AllNodes().FirstOrDefault(n => n.Children.Contains(child));

    private List<string> NodePath(LibraryNodeViewModel node)
    {
        var path = new List<string>();
        for (var n = node; n != null; n = FindParent(n))
            path.Insert(0, n.Title);
        return path;
    }

    private LibraryNodeViewModel? FindByPath(IReadOnlyList<string> path)
    {
        IEnumerable<LibraryNodeViewModel> level = Tree;
        LibraryNodeViewModel? found = null;
        foreach (var title in path)
        {
            found = level.FirstOrDefault(n => n.Title == title);
            if (found == null)
                return null;
            level = found.Children;
        }
        return found;
    }

    // ---- filtering ---------------------------------------------------------------------------------------

    private async Task ApplyFilterAsync()
    {
        _filterCts?.Cancel();
        var cts = _filterCts = new CancellationTokenSource();

        var source = SelectedNode?.Rows ?? _allRows;
        var terms = GameLibrary.ParseQuery(SearchText);
        var modifiedOnly = ModifiedOnly;
        // Under Pending changes and Available from others, hidden changes are masked, so a search must not find them.
        // (A hidden offer for a sound you're changing yourself waits until you've applied or discarded yours.)
        var kind = SelectedNode?.Kind ?? LibraryNodeKind.AllSounds;
        bool Excluded(SoundRowViewModel r) => kind switch
        {
            LibraryNodeKind.Pending => r.Pending?.IsHidden == true && terms.Length > 0,
            LibraryNodeKind.Available => r.Offer?.IsHidden == true && (terms.Length > 0 || r.Pending is { IsHidden: false }),
            _ => false,
        };

        List<SoundRowViewModel> result;
        try
        {
            result = await Task.Run(() =>
                source.Where(r => !Excluded(r) && (!modifiedOnly || r.IsModified) && (terms.Length == 0 || r.Info.Matches(terms))).ToList(),
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested)
            return;

        var previous = SelectedRow;
        foreach (var row in result)
            row.ShownIn = kind;
        VisibleRows = result;
        OffersShownCount = result.Count(r => r.VisibleOffer is { Requested: false });
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyHint));
        SelectedRow = previous != null && result.Contains(previous) ? previous : null;
        UpdateCounts();

        var scope = SelectedNode?.Title ?? "All sounds";
        if (SelectedNode == _pendingNode && result.Count == 0)
            ResultText = "Pending changes · none";
        ResultText = result.Count == source.Count
            ? $"{scope} · {result.Count:N0} {(result.Count == 1 ? "sound" : "sounds")}"
            : $"{scope} · {result.Count:N0} of {source.Count:N0} sounds";
    }

    private void UpdateCounts()
    {
        RevertableVisibleCount = VisibleRows.Count(r => r.CanRevert);
        MissingOriginalsCount = _allRows.Count(r => r.IsModified && !r.HasOriginal);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    // ---- originals, revert, history ------------------------------------------------------------------------

    private void RefreshOriginalFlags()
    {
        foreach (var row in _allRows)
            row.HasOriginal = row.IsModified && _editor.Originals.Has(row.BankFileName, row.Info.Index, row.Info.Id);
        UpdateCounts();
    }

    private async Task LoadHistoryAsync()
    {
        var records = await Task.Run(_editor.History.ReadAll);
        _historyBySound = records.ToLookup(r => (r.Bank, r.Index));
    }

    private void LoadSelectedHistory()
    {
        SelectedHistory.Clear();
        HasSelectedHistory = false;
        if (SelectedRow is not { } row)
            return;
        foreach (var record in _historyBySound[(row.BankFileName, row.Info.Index)]
                     .Where(r => r.Id == row.Info.Id).OrderByDescending(r => r.TimeUtc).Take(20))
            SelectedHistory.Add(new HistoryItemViewModel(record));
        HasSelectedHistory = SelectedHistory.Count > 0;
    }

    [RelayCommand]
    private Task RevertSelectedAsync() =>
        SelectedRow is { CanRevert: true } row ? RevertAsync([row], confirm: false) : Task.CompletedTask;

    [RelayCommand]
    private Task RevertVisibleAsync() => RevertAsync(VisibleRows.Where(r => r.CanRevert).ToList(), confirm: true);

    private async Task RevertAsync(IReadOnlyList<SoundRowViewModel> rows, bool confirm)
    {
        if (rows.Count == 0 || IsBusy)
            return;
        if (confirm && !await Dialogs.ConfirmAsync(
                "Revert to original audio?",
                $"{rows.Count:N0} sound(s) in {rows.Select(r => r.BankFileName).Distinct().Count()} bank(s) will go back to the original game audio.\n\n" +
                "Your current versions are not kept. Make sure Black Ops II is closed.",
                $"Revert {rows.Count:N0}"))
            return;

        var reverted = 0;
        var errors = new List<string>();
        await RunBusyAsync("Reverting…", async () =>
        {
            foreach (var bank in rows.GroupBy(r => r.Info.Bank.FilePath))
            {
                BusyText = $"Reverting sounds in {Path.GetFileName(bank.Key)}…";
                try
                {
                    var result = await Task.Run(() => _editor.Revert(bank.Key, bank.Select(r => r.Info.Index)));
                    reverted += result.Reverted;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException)
                {
                    errors.Add(ex.Message);
                    if (ex is BankLockedException)
                        break; // the game is open; every other bank will fail the same way
                }
            }
        });

        if (errors.Count > 0)
            Dialogs.Notify(reverted > 0 ? $"Reverted {reverted:N0}, but some failed" : "Couldn't revert", errors[0], NoticeKind.Error);
        else
            Dialogs.Notify("Reverted", rows.Count == 1 ? $"{rows[0].Name} is back to the original audio." : $"{reverted:N0} sounds are back to the original audio.");
        await RescanAsync();
    }

    [RelayCommand]
    private async Task ImportOriginalsAsync()
    {
        if (Library == null || IsBusy)
            return;
        var picked = Dialogs.PickFolder(
            "Select a CLEAN copy of the Black Ops II \"sound\" folder (e.g. from a fresh download)", null);
        if (picked == null)
            return;
        var cleanFolder = ResolveSoundFolder(picked);
        if (cleanFolder == null)
        {
            Dialogs.Notify("No sound banks there", "Pick a folder that contains .sabs and .sabl files.", NoticeKind.Warning);
            return;
        }
        if (string.Equals(Path.GetFullPath(cleanFolder).TrimEnd('\\'), Path.GetFullPath(Library.SoundFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            Dialogs.Notify("That's the folder you're editing",
                "Pick a separate, unmodified copy of the game's sound folder.", NoticeKind.Warning);
            return;
        }

        var needed = _allRows.Where(r => r.IsModified && !r.HasOriginal).GroupBy(r => r.Info.Bank).ToList();
        var wanted = needed.Sum(g => g.Count());
        var recovered = 0;
        await RunBusyAsync("Recovering originals…", async () =>
        {
            foreach (var group in needed)
            {
                var clean = Path.Combine(cleanFolder, group.Key.FileName);
                if (!File.Exists(clean))
                    continue;
                BusyText = $"Checking {group.Key.FileName}…";
                try
                {
                    recovered += await Task.Run(() => _editor.ImportOriginals(clean, group.Key.FilePath));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
            }
        });

        RefreshOriginalFlags();
        await LoadHistoryAsync();
        LoadSelectedHistory();
        Dialogs.Notify(
            recovered > 0 ? "Originals recovered" : "No originals found",
            recovered > 0
                ? $"Saved the original audio for {recovered:N0} of {wanted:N0} modified sounds. You can now revert them."
                : "None of the sounds in that folder match the original game audio. It may be modified too.",
            recovered > 0 ? NoticeKind.Success : NoticeKind.Warning);
    }

    private async Task RunBusyAsync(string text, Func<Task> work)
    {
        IsBusy = true;
        BusyText = text;
        try { await work(); }
        finally { IsBusy = false; }
    }

    // ---- details actions -----------------------------------------------------------------------------------

    [RelayCommand]
    private void ShowInExplorer()
    {
        var path = SelectedRow?.Info.Bank.FilePath ?? SoundFolder;
        if (path == null)
            return;
        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    [RelayCommand]
    private void CopyName()
    {
        if (SelectedRow is { IsMasked: false })
            Clipboard.SetText(SelectedRow.Name);
    }
}

/// <summary>One line in the details panel's history list.</summary>
public sealed class HistoryItemViewModel(ChangeRecord record)
{
    public string Title => record.Action switch
    {
        ChangeAction.Replaced => "Replaced",
        ChangeAction.Reverted => "Reverted to original",
        ChangeAction.OriginalRecovered => "Original audio saved",
        _ => record.Action.ToString(),
    };

    public string Subtitle
    {
        get
        {
            var local = record.TimeUtc.ToLocalTime();
            var when = local.Date == DateTime.Today ? $"Today {local:HH:mm}" : local.ToString("d MMM yyyy, HH:mm");
            var parts = new List<string> { when, record.Author };
            if (record.Action == ChangeAction.Replaced && !string.IsNullOrEmpty(record.Detail))
                parts.Add(record.Detail);
            return string.Join(" · ", parts);
        }
    }

    public SymbolRegular Icon => record.Action switch
    {
        ChangeAction.Replaced => SymbolRegular.Edit24,
        ChangeAction.Reverted => SymbolRegular.ArrowUndo24,
        _ => SymbolRegular.Archive24,
    };
}
