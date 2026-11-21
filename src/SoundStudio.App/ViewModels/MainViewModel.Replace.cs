using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundStudio.App.Services;
using SoundStudio.Audio;
using SoundStudio.Core.Editing;
using SoundStudio.Core.Library;

namespace SoundStudio.App.ViewModels;

/// <summary>Replacing sounds: staging converted audio, reviewing it, and applying it to the game.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Rows with staged changes; also the backing list of the "Pending changes" tree node.</summary>
    private readonly List<SoundRowViewModel> _pendingRows = [];
    private LibraryNodeViewModel? _pendingNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges), nameof(PendingSummary))]
    private int _pendingCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PendingSummary))]
    private int _pendingTooLongCount;

    public bool HasPendingChanges => PendingCount > 0;

    public string PendingSummary
    {
        get
        {
            var banks = _pendingRows.Select(r => r.BankFileName).Distinct().Count();
            return $"{PendingCount:N0} change{(PendingCount == 1 ? "" : "s")} ready in {banks} bank{(banks == 1 ? "" : "s")}";
        }
    }

    /// <summary>Shown over the list while dragging files: what a drop would do.</summary>
    [ObservableProperty] private string? _dropHint;

    // ---- entry points ----------------------------------------------------------------------------------

    [RelayCommand]
    private async Task ReplaceSelectedAsync()
    {
        if (SelectedRow is not { CanReplace: true } row)
            return;
        var file = Dialogs.PickFile($"Choose new audio for {row.Name}", ReplacementEncoder.FileFilter);
        if (file != null)
            await StageAsync([(row, file)], options: null);
    }

    /// <summary>Opens the recorder for the selected sound; "Use as replacement" stages the take.</summary>
    [RelayCommand]
    private void RecordSelected()
    {
        if (SelectedRow is not { CanReplace: true } row)
            return;
        Player.StopCommand.Execute(null);
        Dialogs.ShowRecorder(CreateRecorder(row));
    }

    /// <summary>A recorder whose "Use as replacement" stages the take for <paramref name="row"/>.</summary>
    public RecorderViewModel CreateRecorder(SoundRowViewModel row, bool demo = false) =>
        new(row, _settings, path => StageAsync([(row, path)], options: null), demo) { Dialogs = Dialogs };

    /// <summary>The last layer mix staged per sound, so "Layer sounds…" reopens with the same layers.</summary>
    private readonly Dictionary<SoundRowViewModel, LayerSession> _layerSessions = [];

    /// <summary>Opens the layer editor for the selected sound; "Use as replacement" stages the mix.</summary>
    [RelayCommand]
    private void LayerSelected()
    {
        if (SelectedRow is not { CanReplace: true } row)
            return;
        Player.StopCommand.Execute(null);
        var session = _layerSessions.TryGetValue(row, out var s) && row.Pending?.Prepared.SourcePath == s.MixPath ? s : null;
        Dialogs.ShowLayers(CreateLayers(row), session);
    }

    public LayersViewModel CreateLayers(SoundRowViewModel row) =>
        new(row, _settings, _editor.Originals, async (path, session) =>
        {
            _layerSessions[row] = session;
            await StageAsync([(row, path)], options: null);
        }) { Dialogs = Dialogs };

    [RelayCommand]
    private async Task ReplaceFromFolderAsync()
    {
        var folder = Dialogs.PickFolder("Choose a folder of audio files named after the sounds they replace", null);
        if (folder != null)
            await ReviewAndStageAsync(ExpandAudioFiles([folder]));
    }

    /// <summary>Files dropped on the list (<paramref name="target"/> = row under the cursor) or details panel.</summary>
    public async Task HandleDropAsync(string[] paths, SoundRowViewModel? target)
    {
        DropHint = null;
        var files = ExpandAudioFiles(paths);
        if (files.Count == 0)
        {
            Dialogs.Notify("Nothing to replace with", "Drop audio files (wav, mp3, flac, ogg...) or a folder of them.", NoticeKind.Warning);
            return;
        }
        var single = paths.Length == 1 && File.Exists(paths[0]);
        target ??= SelectedRow;
        if (single && target != null)
        {
            if (!target.CanReplace)
            {
                Dialogs.Notify("Can't replace this sound", $"{target.FormatName} sounds can't be replaced.", NoticeKind.Warning);
                return;
            }
            await StageAsync([(target, files[0])], options: null);
            return;
        }
        await ReviewAndStageAsync(files);
    }

    /// <summary>Text for the drop hint while dragging over the list.</summary>
    public void UpdateDropHint(string[]? paths, SoundRowViewModel? target)
    {
        if (paths == null || paths.Length == 0)
        {
            DropHint = null;
            return;
        }
        var single = paths.Length == 1 && File.Exists(paths[0]);
        target ??= SelectedRow;
        DropHint = single && target != null
            ? $"Drop to replace {target.Name}"
            : $"Drop to match {(paths.Length == 1 ? "these files" : $"{paths.Length} items")} to sounds by name";
    }

    public static List<string> ExpandAudioFiles(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Where(ReplacementEncoder.LooksLikeAudio));
            else if (File.Exists(path))
                files.Add(path);
        }
        return files;
    }

    // ---- bulk: match by name, review, stage ---------------------------------------------------------------

    private async Task ReviewAndStageAsync(IReadOnlyList<string> files)
    {
        if (Library == null || files.Count == 0)
            return;

        var scopeNode = SelectedNode is { Kind: not (LibraryNodeKind.Modified or LibraryNodeKind.AllSounds) } n && n != _pendingNode ? n : null;
        var scopeRows = scopeNode?.Rows ?? _allRows;
        var rowByInfo = _allRows.ToDictionary(r => r.Info);

        var matches = ReplacementMatcher.Match(files, scopeRows.Select(r => r.Info));
        var scopeName = scopeNode?.Title ?? "all sounds";
        if (scopeNode != null && !matches.Any(m => m.IsMatched))
        {
            matches = ReplacementMatcher.Match(files, _allRows.Select(r => r.Info));
            scopeName = "all sounds";
        }

        var items = matches.Select(m => new MatchItemViewModel(m.FilePath,
            m.Sounds.Select(s => rowByInfo[s]).Where(r => r.CanReplace).ToList())).ToList();
        var review = await Dialogs.ReviewReplacementsAsync(items, scopeName);
        if (review == null)
            return;

        var pairs = items.Where(i => i.IsSelected && i.IsMatched)
            .SelectMany(i => i.Rows.Select(r => (r, i.FilePath))).ToList();
        await StageAsync(pairs, review.TrimLong ? TrimToOriginal : null);
    }

    /// <summary>Marker: trim each replacement to its own sound's original length.</summary>
    private static readonly ConvertOptions TrimToOriginal = new();

    // ---- staging -----------------------------------------------------------------------------------------

    private async Task StageAsync(IReadOnlyList<(SoundRowViewModel Row, string Path)> items, ConvertOptions? options)
    {
        items = items.Where(i => i.Row.CanReplace).ToList();
        if (items.Count == 0 || IsBusy)
            return;

        var results = new (SoundRowViewModel Row, PreparedReplacement? Prepared, string? Error)[items.Count];
        var done = 0;
        await RunBusyAsync(items.Count == 1 ? "Converting…" : $"Converting 0/{items.Count}…", async () =>
        {
            using var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
            var tasks = items.Select(async (item, index) =>
            {
                await gate.WaitAsync();
                try
                {
                    var info = item.Row.Info;
                    var previous = item.Row.Pending?.Prepared is { } p && p.SourcePath == item.Path ? p.Options : new ConvertOptions();
                    var opts = ReferenceEquals(options, TrimToOriginal) ? previous with { TrimTo = info.Duration } : options;
                    var prepared = await Task.Run(() =>
                        ReplacementEncoder.Prepare(item.Path, info.Format, info.Channels, info.SampleRate, opts));
                    results[index] = (item.Row, prepared, null);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
                                               or InvalidOperationException or UnauthorizedAccessException
                                               or System.Runtime.InteropServices.COMException)
                {
                    results[index] = (item.Row, null, $"{Path.GetFileName(item.Path)}: {ex.Message}");
                }
                finally
                {
                    gate.Release();
                    var n = Interlocked.Increment(ref done);
                    if (items.Count > 1)
                        System.Windows.Application.Current.Dispatcher.Invoke(() => BusyText = $"Converting {n}/{items.Count}…");
                }
            });
            await Task.WhenAll(tasks);
        });

        var staged = 0;
        foreach (var (row, prepared, _) in results)
        {
            if (prepared == null)
                continue;
            var wasVolumeOnly = row.Pending is { IsVolumeOnly: true } old && old.Prepared.SourcePath == prepared.SourcePath;
            SetPending(row, new PendingChange(row, prepared) { IsVolumeOnly = wasVolumeOnly });
            staged++;
        }
        RefreshPendingState();

        var errors = results.Where(r => r.Error != null).Select(r => r.Error!).ToList();
        if (errors.Count > 0)
            Dialogs.Notify(staged > 0 ? $"Staged {staged}, {errors.Count} failed" : "Couldn't convert", errors[0], NoticeKind.Error);
        else if (staged == 1)
            Dialogs.Notify("Ready to apply", $"{results[0].Row.Name} will use {Path.GetFileName(items[0].Path)}. Click Apply to write it to the game.");
        else
            Dialogs.Notify("Ready to apply", $"{staged} sounds staged. Click Apply to write them to the game.");

        // Let the user hear it straight away.
        if (SelectedRow != null && results.Any(r => r.Row == SelectedRow && r.Prepared != null))
            await Player.LoadAsync(SelectedRow, Player.AutoPlay && !SuppressAutoPlay);
    }

    private void SetPending(SoundRowViewModel row, PendingChange? change)
    {
        row.Pending = change;
        if (change != null && !_pendingRows.Contains(row))
            _pendingRows.Add(row);
        else if (change == null)
            _pendingRows.Remove(row);
    }

    private void RefreshPendingState()
    {
        PendingCount = _pendingRows.Count;
        PendingTooLongCount = _pendingRows.Count(r => r.Pending!.IsTooLong);
        OnPropertyChanged(nameof(PendingSummary));
        foreach (var node in Tree)
            node.RefreshCounts(); // recursive: updates the blue pending badges everywhere
        if (SelectedNode == _pendingNode)
            _ = ApplyFilterAsync();
    }

    [RelayCommand]
    private async Task DiscardSelectedAsync()
    {
        if (SelectedRow is not { HasPending: true } row)
            return;
        SetPending(row, null);
        RefreshPendingState();
        await Player.LoadAsync(row, autoPlay: false);
    }

    [RelayCommand]
    private async Task DiscardAllAsync()
    {
        if (_pendingRows.Count == 0)
            return;
        if (_pendingRows.Count > 1 && !await Dialogs.ConfirmAsync("Discard all staged changes?",
                $"{_pendingRows.Count} staged replacements will be thrown away. The game files haven't been touched.", "Discard"))
            return;
        foreach (var row in _pendingRows.ToList())
            row.Pending = null;
        _pendingRows.Clear();
        RefreshPendingState();
        if (SelectedRow != null)
            await Player.LoadAsync(SelectedRow, autoPlay: false);
    }

    /// <summary>Re-converts the selected staged sound, cut to the original's length.</summary>
    [RelayCommand]
    private Task TrimSelectedAsync() =>
        SelectedRow is { Pending: { IsTooLong: true } p } row
            ? StageAsync([(row, p.Prepared.SourcePath)], TrimToOriginal)
            : Task.CompletedTask;

    [RelayCommand]
    private Task TrimAllTooLongAsync() =>
        StageAsync(_pendingRows.Where(r => r.Pending!.IsTooLong).Select(r => (r, r.Pending!.Prepared.SourcePath)).ToList(), TrimToOriginal);

    [RelayCommand]
    private void ReviewPending()
    {
        if (_pendingNode == null)
            return;
        _pendingNode.IsSelected = true;
        SelectedNode = _pendingNode;
    }

    // ---- apply -------------------------------------------------------------------------------------------

    [RelayCommand]
    private Task ApplyPendingAsync() => ApplyRowsAsync(_pendingRows.ToList(), confirmLong: true);

    private async Task ApplyRowsAsync(IReadOnlyList<SoundRowViewModel> rows, bool confirmLong)
    {
        rows = rows.Where(r => r.Pending != null).ToList();
        if (rows.Count == 0 || IsBusy)
            return;
        var tooLong = rows.Count(r => r.Pending!.IsTooLong);
        if (confirmLong && tooLong > 0 && !await Dialogs.ConfirmAsync("Some sounds are longer than the originals",
                $"{tooLong} staged sound(s) are longer than the sounds they replace. This is known to make some levels " +
                "load forever. You can cancel and use \"Trim all\" first, or apply anyway.", "Apply anyway"))
            return;

        var applied = 0;
        var errors = new List<string>();
        // Snapshot now: an incoming change arriving mid-save must not swap what we're writing.
        var snapshot = rows.Select(r => (Row: r, Change: r.Pending!)).ToList();
        var banks = snapshot.GroupBy(s => s.Row.Info.Bank.FilePath).ToList();
        await RunBusyAsync("Saving…", async () =>
        {
            var n = 0;
            foreach (var bank in banks)
            {
                n++;
                BusyText = $"Saving {Path.GetFileName(bank.Key)} ({n}/{banks.Count})…";
                var changes = bank.Select(s => new SoundChange(s.Row.Info.Index, s.Row.Info.Id, s.Change.Prepared.EncodedData,
                    s.Change.Prepared.SampleCount, s.Change.Detail)).ToList();
                try
                {
                    await Task.Run(() => _editor.Apply(bank.Key, changes));
                    foreach (var s in bank)
                    {
                        if (ReferenceEquals(s.Row.Pending, s.Change)) // keep anything newer that arrived meanwhile
                            SetPending(s.Row, null);
                    }
                    applied += changes.Count;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException)
                {
                    errors.Add(ex.Message);
                    if (ex is BankLockedException)
                        break;
                }
            }
        });
        RefreshPendingState();

        if (errors.Count > 0)
            Dialogs.Notify(applied > 0 ? $"Applied {applied}, but some failed" : "Couldn't apply", errors[0], NoticeKind.Error);
        else
            Dialogs.Notify("Applied", $"{applied} sound{(applied == 1 ? "" : "s")} updated in the game. Originals are saved, so you can revert any time.");
        await RescanAsync();
    }
}

/// <summary>One file in the bulk-replace review list.</summary>
public sealed partial class MatchItemViewModel(string filePath, IReadOnlyList<SoundRowViewModel> rows) : ObservableObject
{
    public string FilePath { get; } = filePath;
    public IReadOnlyList<SoundRowViewModel> Rows { get; } = rows;
    public string FileName => Path.GetFileName(FilePath);
    public bool IsMatched => Rows.Count > 0;

    [ObservableProperty] private bool _isSelected = rows.Count > 0;

    public string TargetText => Rows.Count switch
    {
        0 => $"No sound named \"{ReplacementMatcher.KeyForFile(FilePath)}\"",
        1 => $"{Rows[0].Name} · {Rows[0].MapName}",
        _ => $"{Rows[0].Name} · {Rows.Count} sounds in {string.Join(", ", Rows.Select(r => r.MapName).Distinct().Take(3))}",
    };
}

public sealed record ReviewResult(bool TrimLong);
