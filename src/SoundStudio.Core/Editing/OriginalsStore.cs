using System.Security.Cryptography;
using System.Text.Json;
using SoundStudio.Core.Bo2;

namespace SoundStudio.Core.Editing;

/// <summary>Metadata saved next to an original sound's audio.</summary>
public sealed record OriginalSoundInfo(
    uint Id,
    int Index,
    int Size,
    int SampleCount,
    string Md5,
    DateTime CapturedUtc,
    string Source);

public sealed record OriginalSound(OriginalSoundInfo Info, byte[] Data);

/// <summary>
/// Keeps the untouched game audio of every sound before it is first changed, so it can be reverted.
/// Only the changed sounds are stored (a few KB each), not whole multi-hundred-MB banks.
/// Every stored original is verified against the bank's own MD5, so it is guaranteed to be the real game audio.
/// </summary>
/// <remarks>Layout: <c>{root}\{bank file name}\{index:D5}_{id:X8}.orig</c> + <c>.json</c>.</remarks>
public sealed class OriginalsStore(string root)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Root { get; } = root;

    public bool Has(string bankFileName, int index, uint id) => File.Exists(DataPath(bankFileName, index, id));

    public OriginalSound? Load(string bankFileName, int index, uint id, byte[] expectedChecksum)
    {
        var dataPath = DataPath(bankFileName, index, id);
        var metaPath = Path.ChangeExtension(dataPath, ".json");
        if (!File.Exists(dataPath) || !File.Exists(metaPath))
            return null;

        var data = File.ReadAllBytes(dataPath);
        if (!MD5.HashData(data).AsSpan().SequenceEqual(expectedChecksum))
            return null; // damaged or doesn't belong to this bank version: never write it back
        var info = JsonSerializer.Deserialize<OriginalSoundInfo>(File.ReadAllText(metaPath), Json);
        return info == null ? null : new OriginalSound(info, data);
    }

    /// <summary>
    /// Saves the entry's current on-disk audio as its original, if that audio is genuine (matches the bank's MD5)
    /// and no original is stored yet. Returns true when an original is (now) available.
    /// </summary>
    public bool TryCapture(SabEntry entry, string source = "Saved before first change")
    {
        var bankFileName = Path.GetFileName(entry.Bank.FilePath) ?? throw new InvalidOperationException("Bank has no file path.");
        if (Has(bankFileName, entry.Index, entry.Id))
            return true;
        return TryStore(bankFileName, entry.Index, entry.Id, entry.SampleCount, entry.ReadOriginalData(), entry.Checksum, source);
    }

    /// <summary>
    /// Recovers originals from a clean copy of a bank (e.g. from a fresh install). For each sound in
    /// <paramref name="target"/> without a stored original, takes the clean bank's audio for the same sound if its
    /// MD5 matches the checksum the target bank expects. Returns how many originals were recovered.
    /// </summary>
    public int ImportFrom(SabBank cleanBank, SabBank target, Func<SabEntry, bool>? filter = null)
    {
        var bankFileName = Path.GetFileName(target.FilePath) ?? throw new InvalidOperationException("Bank has no file path.");
        var source = $"Recovered from {cleanBank.FilePath}";
        var recovered = 0;
        foreach (var entry in target.Entries)
        {
            if ((filter != null && !filter(entry)) || Has(bankFileName, entry.Index, entry.Id))
                continue;
            if (entry.Index >= cleanBank.Entries.Count)
                continue;
            var clean = cleanBank.Entries[entry.Index];
            if (clean.Id != entry.Id)
                continue;
            if (TryStore(bankFileName, entry.Index, entry.Id, clean.SampleCount, clean.ReadOriginalData(), entry.Checksum, source))
                recovered++;
        }
        return recovered;
    }

    private bool TryStore(string bankFileName, int index, uint id, int sampleCount, byte[] data, byte[] expectedChecksum, string source)
    {
        var md5 = MD5.HashData(data);
        if (!md5.AsSpan().SequenceEqual(expectedChecksum))
            return false;

        var dataPath = DataPath(bankFileName, index, id);
        Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
        var info = new OriginalSoundInfo(id, index, data.Length, sampleCount, Convert.ToHexString(md5), DateTime.UtcNow, source);

        // Write data then metadata, each via temp file, so a partial write is never mistaken for an original.
        WriteAtomically(dataPath, data);
        WriteAtomically(Path.ChangeExtension(dataPath, ".json"), JsonSerializer.SerializeToUtf8Bytes(info, Json));
        return true;
    }

    private string DataPath(string bankFileName, int index, uint id) =>
        Path.Combine(Root, bankFileName, $"{index:D5}_{id:X8}.orig");

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }
}
