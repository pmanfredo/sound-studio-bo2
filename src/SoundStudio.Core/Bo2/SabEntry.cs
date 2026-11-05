namespace SoundStudio.Core.Bo2;

/// <summary>One sound inside a T6 sound bank (0x14-byte table record).</summary>
public sealed class SabEntry
{
    internal static readonly int[] SampleRateTable = [8000, 12000, 16000, 24000, 32000, 44100, 48000, 96000, 192000];

    private byte[]? _replacement;

    internal SabEntry(SabBank bank, int index)
    {
        Bank = bank;
        Index = index;
    }

    public SabBank Bank { get; }

    /// <summary>Position in the bank's entry table.</summary>
    public int Index { get; }

    /// <summary>Hash of the sound's asset path; looked up in <see cref="SoundNameDatabase"/>.</summary>
    public uint Id { get; internal set; }

    /// <summary>Size of the audio data in bytes.</summary>
    public int Size { get; internal set; }

    /// <summary>Absolute offset of the audio data in the bank file.</summary>
    public long Offset { get; internal set; }

    public int SampleCount { get; set; }
    public byte SampleRateFlag { get; set; }
    public byte ChannelCount { get; set; }

    /// <summary>Raw loop byte (0 or 1 in practice). Kept raw so unchanged files round-trip exactly.</summary>
    public byte LoopRaw { get; set; }

    public AudioFormat Format { get; set; }

    /// <summary>MD5 of the audio data as stored in the bank's checksum table.</summary>
    public byte[] Checksum { get; internal set; } = new byte[16];

    public bool Loop => LoopRaw != 0;

    public int SampleRate
    {
        get => SampleRateFlag < SampleRateTable.Length ? SampleRateTable[SampleRateFlag] : 0;
        set
        {
            var index = Array.IndexOf(SampleRateTable, value);
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Sample rate is not supported by Black Ops II.");
            SampleRateFlag = (byte)index;
        }
    }

    public TimeSpan Duration => SampleRate == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)SampleCount / SampleRate);

    /// <summary>True when new audio has been set in memory but not yet written to disk.</summary>
    public bool HasPendingReplacement => _replacement != null;

    /// <summary>Reads the audio data: the pending replacement if there is one, otherwise the bytes on disk.</summary>
    public byte[] ReadData() => _replacement ?? Bank.ReadRange(Offset, Size);

    /// <summary>Reads the audio data currently stored in the bank file, ignoring any pending replacement.</summary>
    public byte[] ReadOriginalData() => Bank.ReadRange(Offset, Size);

    /// <summary>
    /// Stages new, already game-encoded audio data for this sound. Nothing is written until the bank is saved.
    /// </summary>
    public void Replace(byte[] encodedData, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(encodedData);
        if (encodedData.Length == 0)
            throw new ArgumentException("Replacement audio is empty.", nameof(encodedData));
        _replacement = encodedData;
        SampleCount = sampleCount;
    }

    internal byte[]? PendingReplacement => _replacement;

    /// <summary>
    /// True when the audio stored on disk no longer matches the bank's checksum for it, i.e. the sound was
    /// replaced by a tool that kept the original checksum (this app, or the old Sound Studio by default).
    /// Reads the sound's data, so call it off the UI thread for large banks.
    /// </summary>
    public bool IsModifiedOnDisk() =>
        !System.Security.Cryptography.MD5.HashData(ReadOriginalData()).AsSpan().SequenceEqual(Checksum);

    public string? Name => SoundNameDatabase.TryGetPath(Id);

    /// <summary>File name without folders, or a fallback like "Sound #12.flac".</summary>
    public string DisplayName
    {
        get
        {
            var path = Name;
            return path != null ? Path.GetFileName(path) : $"Sound #{Index + 1}{Format.FileExtension()}";
        }
    }

    public override string ToString() => $"{DisplayName} [{Id:X8}] {Format.DisplayName()} {Duration.TotalSeconds:0.00}s";
}
