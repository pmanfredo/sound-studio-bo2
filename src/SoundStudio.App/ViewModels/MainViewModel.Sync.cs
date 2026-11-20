using System.IO;
using CommunityToolkit.Mvvm.Input;
using SoundStudio.App.Services;
using SoundStudio.Audio;
using SoundStudio.Core;
using SoundStudio.Sync;

namespace SoundStudio.App.ViewModels;

/// <summary>Sync glue: what we apply gets shared; what friends share gets staged (or applied).</summary>
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

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
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

    private void InitializeSync()
    {
        Sync = new SyncViewModel(_settings) { IncomingHandler = HandleIncomingAsync, ShareAllHandler = ShareAllModifiedAsync };
    }

    // ---- outgoing ------------------------------------------------------------------------------------------

    /// <summary>Shares sounds that were just applied locally (not ones that came from a friend).</summary>
    private void PublishApplied(string bankPath, IEnumerable<(int Index, uint Id, PendingChange Change)> applied)
    {
        if (!Sync.CanPublish)
            return;
        var outgoing = new List<(SyncChange, byte[]?)>();
        foreach (var (index, id, change) in applied.Where(a => a.Change.FromAuthor == null))
        {
            try
            {
                var (c, data) = SyncChanges.Replace(bankPath, index, id, change.Prepared.EncodedData,
                    change.Prepared.SampleCount, change.Detail);
                outgoing.Add((c, data));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        }
        Sync.Publish(outgoing);
    }

    private void PublishReverted(string bankPath, IEnumerable<SoundRowViewModel> rows)
    {
        if (!Sync.CanPublish)
            return;
        var outgoing = new List<(SyncChange, byte[]?)>();
        foreach (var row in rows)
        {
            try { outgoing.Add((SyncChanges.Revert(bankPath, row.Info.Index, row.Info.Id), null)); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        }
        Sync.Publish(outgoing);
    }

    /// <summary>Sends every modified sound as it is right now, e.g. so a friend who just joined gets your mods.</summary>
    private async Task<int> ShareAllModifiedAsync()
    {
        var rows = _allRows.Where(r => r.IsModified).ToList();
        if (rows.Count == 0)
        {
            Dialogs.Notify("Nothing to share", "None of your sounds are modified.", NoticeKind.Info);
            return 0;
        }
        if (!await Dialogs.ConfirmAsync("Share all your modified sounds?",
                $"{rows.Count} modified sounds will be sent to everyone you sync with. They'll be able to review them before applying.",
                $"Share {rows.Count}"))
            return 0;

        var outgoing = await Task.Run(() =>
        {
            var list = new List<(SyncChange, byte[]?)>();
            foreach (var row in rows)
            {
                try
                {
                    var (c, data) = SyncChanges.Current(row.Info.Bank.FilePath, row.Info.Index, row.Info.Id, "shared from game files");
                    list.Add((c, data));
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
            }
            return list;
        });
        Sync.Publish(outgoing);
        Dialogs.Notify("Sharing", $"{outgoing.Count} sounds are on their way.");
        return outgoing.Count;
    }

    // ---- incoming ------------------------------------------------------------------------------------------

    private async Task HandleIncomingAsync(IReadOnlyList<IncomingChange> incoming)
    {
        if (Library == null)
            return;
        var problems = new List<string>();
        var staged = new List<SoundRowViewModel>();
        var authors = new HashSet<string>();

        // Only the latest change per sound matters.
        var latest = incoming.GroupBy(i => (i.Change.Bank, i.Change.Index, i.Change.Id)).Select(g => g.Last());
        foreach (var item in latest)
        {
            var c = item.Change;
            var name = c.SoundName != null ? Path.GetFileName(c.SoundName).Split('.')[0] : $"{c.Bank} #{c.Index + 1}";
            var problem = SyncChanges.CheckCompatible(c, Library.SoundFolder);
            var row = _allRows.FirstOrDefault(r => r.BankFileName == c.Bank && r.Info.Index == c.Index && r.Info.Id == c.Id);
            if (problem != null || row == null)
            {
                problems.Add($"{name}: {problem ?? "not found"}");
                continue;
            }
            if (row.Pending is { FromAuthor: null })
            {
                problems.Add($"{name}: kept your own staged change");
                continue;
            }

            try
            {
                var prepared = await Task.Run(() => PrepareIncoming(item, row));
                if (prepared == null)
                {
                    if (c.Action == SyncAction.Revert && row.IsModified)
                        problems.Add($"{name}: can't revert here, the original isn't saved");
                    continue;
                }
                var author = SyncProtocol.CleanName(c.Author);
                SetPending(row, new PendingChange(row, prepared) { FromAuthor = author, IsRevert = c.Action == SyncAction.Revert });
                staged.Add(row);
                authors.Add(author);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                problems.Add($"{name}: {ex.Message}");
            }
        }
        RefreshPendingState();

        var who = string.Join(" & ", authors);
        if (staged.Count > 0)
        {
            if (Sync.AutoApplyIncoming)
            {
                await ApplyRowsAsync(staged, confirmLong: false);
            }
            else
            {
                Dialogs.Notify($"{who} shared {staged.Count} sound{(staged.Count == 1 ? "" : "s")}",
                    "They're staged under Pending changes. Listen, then Apply to game.", NoticeKind.Info);
            }
            if (SelectedRow != null && staged.Contains(SelectedRow))
                await Player.LoadAsync(SelectedRow, autoPlay: false);
        }
        if (problems.Count > 0)
            Dialogs.Notify($"{problems.Count} shared change{(problems.Count == 1 ? "" : "s")} skipped", problems[0], NoticeKind.Warning);
    }

    /// <summary>Builds a staged replacement from a friend's change; null if there's nothing to do.</summary>
    private PreparedReplacement? PrepareIncoming(IncomingChange item, SoundRowViewModel row)
    {
        var c = item.Change;
        byte[] encoded;
        int samples;
        if (c.Action == SyncAction.Replace)
        {
            encoded = item.Data ?? throw new InvalidDataException("The audio is missing.");
            samples = c.SampleCount;
        }
        else
        {
            if (!row.IsModified)
                return null; // already original here
            var original = _editor.Originals.Load(row.BankFileName, c.Index, c.Id, Convert.FromHexString(c.OriginalChecksum));
            if (original == null)
                return null;
            encoded = original.Data;
            samples = original.Info.SampleCount;
        }

        var audio = AudioDecoder.Decode(encoded, row.Info.Format, row.Info.Channels, row.Info.SampleRate);
        var folder = Path.Combine(AppPaths.SyncFolder, "incoming");
        Directory.CreateDirectory(folder);
        var wav = Path.Combine(folder, $"{row.Name} (from {SyncProtocol.CleanName(c.Author)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}).wav");
        audio.SaveWav(wav);
        return new PreparedReplacement(wav, audio, encoded, samples, row.Info.Format, audio.Duration);
    }

}
