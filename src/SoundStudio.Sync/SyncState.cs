using SoundStudio.Core.Bo2;

namespace SoundStudio.Sync;

public enum DifferenceKind
{
    /// <summary>They changed this sound; yours is the original game audio.</summary>
    TheyChanged,

    /// <summary>You both changed this sound, differently.</summary>
    BothChanged,
}

/// <summary>A sound another install has that differs from yours, and could be fetched.</summary>
public sealed record Difference(string OwnerId, string OwnerName, ManifestEntry Theirs, DifferenceKind Kind);

/// <summary>Builds manifest entries from local game files and compares manifests.</summary>
public static class SyncState
{
    /// <summary>Describes one sound as it is in this game right now (reads its audio to fingerprint it).</summary>
    public static ManifestEntry Describe(string bankPath, int index, uint id, string? source)
    {
        var sound = SoundReader.Read(bankPath, index, id);
        return new ManifestEntry
        {
            Bank = Path.GetFileName(bankPath),
            Index = index,
            Id = id,
            OriginalChecksum = Convert.ToHexString(sound.Checksum),
            Hash = SyncProtocol.Sha256Hex(sound.Data),
            Size = sound.Data.Length,
            Format = sound.Format,
            Channels = sound.Channels,
            SampleRate = sound.SampleRate,
            SampleCount = sound.SampleCount,
            Source = source,
            SoundName = SoundNameDatabase.TryGetPath(id),
        };
    }

    /// <summary>The current audio of a sound, if it still has the given fingerprint (to answer a request).</summary>
    public static byte[]? ReadIfCurrent(string soundFolder, AudioRequest request)
    {
        if (!SyncProtocol.IsSafeBankName(request.Bank))
            return null;
        var path = Path.Combine(soundFolder, request.Bank);
        if (!File.Exists(path))
            return null;
        try
        {
            var data = SoundReader.Read(path, request.Index, request.Id).Data;
            return SyncProtocol.Sha256Hex(data) == request.Hash.ToLowerInvariant() ? data : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// What others have that you don't: for every sound in their manifests, compared with your own manifest.
    /// Identical sounds are skipped, and so are sounds that don't exist in your game or come from a different
    /// game version (their original checksum must match yours).
    /// </summary>
    /// <param name="mine">Your modified sounds (anything not listed is original).</param>
    /// <param name="others">Everyone else's manifests.</param>
    /// <param name="localOriginalChecksum">Your bank's checksum for a sound, or null if you don't have it.</param>
    public static List<Difference> Compare(
        IReadOnlyDictionary<SoundKey, ManifestEntry> mine,
        IEnumerable<Manifest> others,
        Func<SoundKey, string?> localOriginalChecksum)
    {
        var result = new List<Difference>();
        foreach (var manifest in others)
        {
            foreach (var theirs in manifest.Sounds)
            {
                if (SyncProtocol.Validate(theirs) != null)
                    continue;
                if (mine.TryGetValue(theirs.Key, out var my))
                {
                    if (string.Equals(my.Hash, theirs.Hash, StringComparison.OrdinalIgnoreCase))
                        continue; // you already have exactly this
                    if (!string.Equals(my.OriginalChecksum, theirs.OriginalChecksum, StringComparison.OrdinalIgnoreCase))
                        continue; // different game version
                    result.Add(new Difference(manifest.ClientId, manifest.Name, theirs, DifferenceKind.BothChanged));
                }
                else
                {
                    var checksum = localOriginalChecksum(theirs.Key);
                    if (checksum == null || !string.Equals(checksum, theirs.OriginalChecksum, StringComparison.OrdinalIgnoreCase))
                        continue; // not in your game, or a different version of it
                    result.Add(new Difference(manifest.ClientId, manifest.Name, theirs, DifferenceKind.TheyChanged));
                }
            }
        }
        return result;
    }
}
