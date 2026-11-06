namespace SoundSpotter;

public enum SpotKind
{
    /// <summary>A read began at (or within one sector of) a sound's first byte: it just started playing.</summary>
    Start,
    /// <summary>A read landed mid-sound with no recent read of that sound (seek, resume, or a missed first read).</summary>
    StartMidway,
    /// <summary>A further chunk of a sound that is already streaming.</summary>
    Continue,
    /// <summary>One read spanning many sounds: a level load or a whole-bank read (typical for <c>.sabl</c>).</summary>
    Bulk,
    /// <summary>Header, padding, entry table or checksums.</summary>
    Metadata,
}

public sealed record SpotEvent(SpotKind Kind, DateTime Time, BankMap Bank, SpotEntry? Entry, long Offset, long Length, int SoundCount, string Process)
{
    public bool IsPlay => Kind is SpotKind.Start or SpotKind.StartMidway;

    public override string ToString()
    {
        var t = Time.ToString("HH:mm:ss.fff");
        return Kind switch
        {
            SpotKind.Bulk => $"{t}  [bulk ] {Bank.Name}: {SoundCount} sounds in one {Length / 1024.0:0} KB read",
            SpotKind.Metadata => $"{t}  [meta ] {Bank.Name} @0x{Offset:X} +{Length}",
            _ => $"{t}  [{Label(Kind)}] {Entry!.DisplayName,-48} {Entry.Duration.TotalSeconds,6:0.00}s  {Bank.Name} #{Entry.Index + 1}" +
                 (Kind == SpotKind.Continue ? $"  @+{Offset - Entry.Offset} ({Length / 1024.0:0} KB)" : $"  ({Length / 1024.0:0} KB read)") +
                 (Kind != SpotKind.Continue && Entry.Name is { } n ? $"\n               {n}" : ""),
        };
    }

    private static string Label(SpotKind k) => k switch
    {
        SpotKind.Start => "play ",
        SpotKind.StartMidway => "play~",
        _ => "cont ",
    };
}

/// <summary>
/// Turns raw (file, offset, length) reads into "this sound started" events. Pure logic with no ETW, so it is testable.
/// </summary>
public sealed class ReadInterpreter
{
    private readonly BankIndex _index;
    private readonly Dictionary<(string Bank, int Index), DateTime> _lastTouched = new();

    public ReadInterpreter(BankIndex index) => _index = index;

    /// <summary>A read touching more sounds than this is reported as one <see cref="SpotKind.Bulk"/> event.</summary>
    public int BulkThreshold { get; init; } = 8;

    /// <summary>Reads this close to a sound's start still count as its first read (sector-aligned reads).</summary>
    public int StartSlack { get; init; } = 4096;

    /// <summary>A sound not read for this long (plus its duration) is considered stopped; the next read of it is a new play.</summary>
    public TimeSpan IdleGap { get; init; } = TimeSpan.FromSeconds(3);

    public SpotEvent? Process(string? path, long offset, long length, DateTime time, string process = "")
    {
        var bank = _index.Get(path);
        if (bank == null || length <= 0) return null;

        var hits = bank.Overlapping(offset, length);
        if (hits.Count == 0)
            return new SpotEvent(SpotKind.Metadata, time, bank, null, offset, length, 0, process);
        if (hits.Count > BulkThreshold)
        {
            foreach (var h in hits) _lastTouched[(bank.Name, h.Index)] = time;
            return new SpotEvent(SpotKind.Bulk, time, bank, null, offset, length, hits.Count, process);
        }

        // The sound a read is "for" is the one it covers most: an aligned read may begin in the
        // previous sound's tail, and read-ahead may spill into the next sound.
        var end = offset + length;
        var primary = hits.MaxBy(h => Math.Min(end, h.End) - Math.Max(offset, h.Offset))!;
        var key = (bank.Name, primary.Index);
        var recent = _lastTouched.TryGetValue(key, out var last) && time - last <= IdleGap + primary.Duration;
        foreach (var h in hits) _lastTouched[(bank.Name, h.Index)] = time;

        var kind = offset <= primary.Offset + StartSlack ? SpotKind.Start
            : recent ? SpotKind.Continue
            : SpotKind.StartMidway;
        return new SpotEvent(kind, time, bank, primary, offset, length, hits.Count, process);
    }
}
