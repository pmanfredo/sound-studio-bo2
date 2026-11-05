using SoundStudio.Core.Bo2;

namespace SoundStudio.Sync;

/// <summary>Turns local bank edits into <see cref="SyncChange"/>s and checks incoming ones against local files.</summary>
public static class SyncChanges
{
    /// <summary>Describes "this sound now has this (game-encoded) audio". Reads format and checksum from the bank.</summary>
    public static (SyncChange Change, byte[] Data) Replace(string bankPath, int index, uint id, byte[] data, int sampleCount, string? sourceName)
    {
        var sound = SoundReader.Read(bankPath, index, id);
        var change = new SyncChange
        {
            ClientId = "", Author = "", // stamped by the client
            Action = SyncAction.Replace,
            Bank = Path.GetFileName(bankPath),
            Index = index,
            Id = id,
            OriginalChecksum = Convert.ToHexString(sound.Checksum),
            Format = sound.Format,
            Channels = sound.Channels,
            SampleRate = sound.SampleRate,
            SampleCount = sampleCount,
            BlobSha256 = SyncProtocol.Sha256Hex(data),
            BlobSize = data.Length,
            SourceName = sourceName,
            SoundName = SoundNameDatabase.TryGetPath(id),
        };
        return (change, data);
    }

    /// <summary>"Share what's in my game right now" for one sound.</summary>
    public static (SyncChange Change, byte[] Data) Current(string bankPath, int index, uint id, string? sourceName)
    {
        var sound = SoundReader.Read(bankPath, index, id);
        return Replace(bankPath, index, id, sound.Data, sound.SampleCount, sourceName);
    }

    public static SyncChange Revert(string bankPath, int index, uint id)
    {
        var sound = SoundReader.Read(bankPath, index, id);
        return new SyncChange
        {
            ClientId = "", Author = "",
            Action = SyncAction.Revert,
            Bank = Path.GetFileName(bankPath),
            Index = index,
            Id = id,
            OriginalChecksum = Convert.ToHexString(sound.Checksum),
            Format = sound.Format,
            Channels = sound.Channels,
            SampleRate = sound.SampleRate,
            SoundName = SoundNameDatabase.TryGetPath(id),
        };
    }

    /// <summary>
    /// Can this change be applied here? The bank must exist and hold the same sound (same id at the same index,
    /// same original checksum), otherwise the two installs aren't the same game version.
    /// </summary>
    public static string? CheckCompatible(SyncChange change, string soundFolder)
    {
        if (SyncProtocol.Validate(change) is { } problem)
            return problem;
        var bankPath = Path.Combine(soundFolder, change.Bank);
        if (!File.Exists(bankPath))
            return $"You don't have {change.Bank}.";
        try
        {
            var local = SoundReader.Read(bankPath, change.Index, change.Id);
            if (!string.Equals(Convert.ToHexString(local.Checksum), change.OriginalChecksum, StringComparison.OrdinalIgnoreCase))
                return "Your copy of this sound is from a different game version.";
            if (change.Action == SyncAction.Replace && (local.Format != change.Format || local.Channels != change.Channels))
                return "The sound's format doesn't match your game files.";
            return null;
        }
        catch (InvalidOperationException)
        {
            return $"{change.Bank} doesn't contain this sound.";
        }
    }
}
