using SoundStudio.Core.Bo2;

namespace SoundStudio.Core.Editing;

/// <summary>New, already game-encoded audio for one sound.</summary>
public sealed record SoundChange(int Index, uint Id, byte[] Data, int SampleCount, string? Detail = null);

public sealed record RevertResult(int Reverted, int AlreadyOriginal, IReadOnlyList<int> MissingOriginals);

/// <summary>Thrown when a bank can't be written, usually because the game has it open.</summary>
public sealed class BankLockedException(string bankFileName, Exception inner)
    : IOException($"{bankFileName} is in use. Close Black Ops II (and Plutonium) and try again.", inner)
{
    public string BankFileName { get; } = bankFileName;
}

/// <summary>
/// The only place that writes to game files. Every write: saves originals first, writes atomically
/// (temp file + swap), and logs to the change history.
/// </summary>
public sealed class BankEditor(OriginalsStore originals, ChangeHistory history, string author)
{
    public OriginalsStore Originals { get; } = originals;
    public ChangeHistory History { get; } = history;

    public void Apply(string bankPath, IReadOnlyList<SoundChange> changes)
    {
        if (changes.Count == 0)
            return;
        var bankFileName = Path.GetFileName(bankPath);
        var records = new List<ChangeRecord>();

        var bank = SabBank.Open(bankPath);
        try
        {
            foreach (var change in changes)
            {
                var entry = FindEntry(bank, change.Index, change.Id);
                if (!entry.Format.CanReplace())
                    throw new InvalidOperationException($"{entry.DisplayName} is {entry.Format.DisplayName()} audio, which can't be replaced.");

                Originals.TryCapture(entry); // no-op if already saved or the sound was modified before we saw it
                entry.Replace(change.Data, change.SampleCount);
                records.Add(Record(ChangeAction.Replaced, bankFileName, entry, change.Detail));
            }
            Save(bank, bankFileName).Dispose();
        }
        finally
        {
            bank.Dispose();
        }
        History.Append(records);
    }

    public RevertResult Revert(string bankPath, IEnumerable<int> indices)
    {
        var bankFileName = Path.GetFileName(bankPath);
        var records = new List<ChangeRecord>();
        var missing = new List<int>();
        var alreadyOriginal = 0;

        var bank = SabBank.Open(bankPath);
        try
        {
            foreach (var index in indices.Distinct())
            {
                if (index < 0 || index >= bank.Entries.Count)
                    continue;
                var entry = bank.Entries[index];
                if (!entry.IsModifiedOnDisk())
                {
                    alreadyOriginal++;
                    continue;
                }
                var original = Originals.Load(bankFileName, entry.Index, entry.Id, entry.Checksum);
                if (original == null)
                {
                    missing.Add(index);
                    continue;
                }
                entry.Replace(original.Data, original.Info.SampleCount);
                records.Add(Record(ChangeAction.Reverted, bankFileName, entry, null));
            }

            if (records.Count > 0)
                Save(bank, bankFileName).Dispose();
        }
        finally
        {
            bank.Dispose();
        }
        History.Append(records);
        return new RevertResult(records.Count, alreadyOriginal, missing);
    }

    /// <summary>Recovers originals for modified sounds from a clean copy of the same bank.</summary>
    public int ImportOriginals(string cleanBankPath, string targetBankPath)
    {
        using var clean = SabBank.Open(cleanBankPath);
        using var target = SabBank.Open(targetBankPath);
        var bankFileName = Path.GetFileName(targetBankPath);
        var before = target.Entries.Where(e => Originals.Has(bankFileName, e.Index, e.Id)).Select(e => e.Index).ToHashSet();
        var count = Originals.ImportFrom(clean, target, e => e.IsModifiedOnDisk());
        if (count > 0)
        {
            History.Append(target.Entries
                .Where(e => !before.Contains(e.Index) && Originals.Has(bankFileName, e.Index, e.Id))
                .Select(e => Record(ChangeAction.OriginalRecovered, bankFileName, e, $"From {cleanBankPath}")));
        }
        return count;
    }

    private static SabEntry FindEntry(SabBank bank, int index, uint id)
    {
        if (index < 0 || index >= bank.Entries.Count || bank.Entries[index].Id != id)
            throw new InvalidOperationException("The sound bank changed since it was loaded. Rescan and try again.");
        return bank.Entries[index];
    }

    private static SabBank Save(SabBank bank, string bankFileName)
    {
        var path = bank.FilePath!;
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
        var needed = bank.FileLength + bank.Entries.Sum(e => (long)(e.HasPendingReplacement ? e.ReadData().Length : 0));
        if (free < needed)
            throw new IOException($"Not enough disk space to save {bankFileName} safely (needs about {needed / 1048576} MB free).");

        try
        {
            return SabWriter.SaveInPlace(bank);
        }
        catch (Exception ex) when (ex is IOException { HResult: unchecked((int)0x80070020) or unchecked((int)0x80070021) } or UnauthorizedAccessException)
        {
            // 0x20 = sharing violation, 0x21 = lock violation: the game (or something else) has the file open.
            throw new BankLockedException(bankFileName, ex);
        }
    }

    private ChangeRecord Record(ChangeAction action, string bankFileName, SabEntry entry, string? detail) =>
        new(DateTime.UtcNow, action, bankFileName, entry.Index, entry.Id, entry.Name, detail, author);
}
