using SoundStudio.Core.Bo2;

namespace SoundSpotter;

/// <summary>One sound's byte range inside a bank, detached from the open file.</summary>
public sealed record SpotEntry(string Bank, int Index, uint Id, long Offset, int Size, TimeSpan Duration, string? Name)
{
    public long End => Offset + Size;
    public string DisplayName => Name is { } n ? Path.GetFileName(n.Replace('/', '\\')) : $"0x{Id:X8}";
}

/// <summary>Sorted byte ranges of one bank, for offset → sound lookups.</summary>
public sealed class BankMap
{
    private readonly SpotEntry[] _byOffset;
    private readonly long[] _starts;

    public BankMap(string name, bool streamed, IEnumerable<SpotEntry> entries)
    {
        Name = name;
        Streamed = streamed;
        _byOffset = entries.Where(e => e.Size > 0).OrderBy(e => e.Offset).ToArray();
        _starts = _byOffset.Select(e => e.Offset).ToArray();
    }

    public string Name { get; }

    /// <summary>True for <c>.sabs</c> (streamed from disk at play time), false for <c>.sabl</c>.</summary>
    public bool Streamed { get; }

    public IReadOnlyList<SpotEntry> Entries => _byOffset;

    /// <summary>The sound whose [Offset, Offset+Size) contains <paramref name="offset"/>, or null (header, padding, tables).</summary>
    public SpotEntry? Find(long offset)
    {
        var i = Array.BinarySearch(_starts, offset);
        if (i < 0) i = ~i - 1;
        return i >= 0 && offset < _byOffset[i].End ? _byOffset[i] : null;
    }

    /// <summary>All sounds overlapping the read [offset, offset+length).</summary>
    public List<SpotEntry> Overlapping(long offset, long length)
    {
        var result = new List<SpotEntry>();
        if (length <= 0) return result;
        var end = offset + length;
        var i = Array.BinarySearch(_starts, offset);
        if (i < 0) i = Math.Max(0, ~i - 1);
        for (; i < _byOffset.Length && _byOffset[i].Offset < end; i++)
            if (_byOffset[i].End > offset)
                result.Add(_byOffset[i]);
        return result;
    }
}

/// <summary>Offset maps for every bank in the game's sound folder, keyed by file name.</summary>
public sealed class BankIndex
{
    private readonly Dictionary<string, BankMap> _maps = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<BankMap> Banks => _maps.Values;

    public void Add(BankMap map) => _maps[map.Name] = map;

    /// <summary>Looks a bank up by any path form ETW reports (\Device\HarddiskVolumeN\..., C:\..., or a bare name).</summary>
    public BankMap? Get(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var slash = path.LastIndexOfAny(['\\', '/']);
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        return _maps.GetValueOrDefault(name);
    }

    public static bool IsBankFile(string? path) =>
        path != null && (path.EndsWith(".sabs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sabl", StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads every bank's table once, then closes the files (the game must be free to use them).</summary>
    public static BankIndex Load(string soundFolder)
    {
        var index = new BankIndex();
        foreach (var file in Directory.EnumerateFiles(soundFolder).Where(IsBankFile).OrderBy(f => f))
        {
            using var bank = SabBank.Open(file);
            var name = Path.GetFileName(file);
            index.Add(new BankMap(name, file.EndsWith(".sabs", StringComparison.OrdinalIgnoreCase),
                bank.Entries.Select(e => new SpotEntry(name, e.Index, e.Id, e.Offset, e.Size, e.Duration, e.Name))));
        }
        return index;
    }
}
