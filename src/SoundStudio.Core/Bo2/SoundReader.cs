namespace SoundStudio.Core.Bo2;

/// <summary>A sound's encoded audio plus what's needed to decode and verify it.</summary>
public sealed record SoundData(
    byte[] Data,
    AudioFormat Format,
    int Channels,
    int SampleRate,
    int SampleCount,
    byte[] Checksum);

public static class SoundReader
{
    /// <summary>Reads one sound straight from its bank file. Throws if the bank changed since it was scanned.</summary>
    public static SoundData Read(string bankPath, int index, uint id)
    {
        using var bank = SabBank.Open(bankPath);
        if (index < 0 || index >= bank.Entries.Count || bank.Entries[index].Id != id)
            throw new InvalidOperationException("The sound bank changed since it was loaded. Rescan (F5) and try again.");
        var e = bank.Entries[index];
        return new SoundData(e.ReadOriginalData(), e.Format, e.ChannelCount, e.SampleRate, e.SampleCount, e.Checksum);
    }
}
