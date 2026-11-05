using SoundStudio.Core.Bo2;

namespace SoundStudio.Core.Library;

/// <summary>Lightweight description of one sound, independent of any open file.</summary>
public sealed class SoundInfo
{
    public required BankInfo Bank { get; init; }
    public required int Index { get; init; }
    public required uint Id { get; init; }
    public required AudioFormat Format { get; init; }
    public required int SampleCount { get; init; }
    public required int SampleRate { get; init; }
    public required byte Channels { get; init; }
    public required bool Loop { get; init; }
    public required int Size { get; init; }
    public bool IsModified { get; internal set; }

    /// <summary>Original asset path, e.g. <c>devraw\sound\vox\...\vox_us_death_00.SN55.pc.snd</c>, if known.</summary>
    public string? AssetPath => SoundNameDatabase.TryGetPath(Id);

    public string Name
    {
        get
        {
            var path = AssetPath;
            if (path == null)
                return $"Sound #{Index + 1}";
            var file = Path.GetFileName(path);
            // vox_us_death_00.SN55.pc.snd -> vox_us_death_00
            var dot = file.IndexOf('.');
            return dot > 0 ? file[..dot] : file;
        }
    }

    public string IdHex => Id.ToString("X8");
    public TimeSpan Duration => SampleRate == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)SampleCount / SampleRate);
    public string FormatName => Format.DisplayName();
    public bool CanReplace => Format.CanReplace();

    /// <summary>Lower-case text searched by the search box.</summary>
    internal string SearchText => _searchText ??= $"{AssetPath ?? Name} {IdHex} {Bank.FileName}".ToLowerInvariant();
    private string? _searchText;

    public bool Matches(IReadOnlyList<string> lowerCaseTerms)
    {
        var text = SearchText;
        foreach (var term in lowerCaseTerms)
            if (!text.Contains(term, StringComparison.Ordinal))
                return false;
        return true;
    }
}

public sealed class BankInfo
{
    public required string FilePath { get; init; }
    public required long FileLength { get; init; }
    public required DateTime LastWriteTimeUtc { get; init; }
    public required BankDescription Description { get; init; }
    public List<SoundInfo> Sounds { get; } = [];

    /// <summary>Set when the bank could not be read (corrupt, locked, wrong game...).</summary>
    public string? Error { get; init; }

    public string FileName => Path.GetFileName(FilePath);
    public int ModifiedCount => Sounds.Count(s => s.IsModified);
}

public sealed class GameLibrary
{
    public GameLibrary(string soundFolder, IReadOnlyList<BankInfo> banks)
    {
        SoundFolder = soundFolder;
        Banks = banks;
        AllSounds = banks.SelectMany(b => b.Sounds).ToList();
    }

    public string SoundFolder { get; }
    public IReadOnlyList<BankInfo> Banks { get; }
    public IReadOnlyList<SoundInfo> AllSounds { get; }
    public int ModifiedCount => AllSounds.Count(s => s.IsModified);

    /// <summary>Splits a search query into lower-case terms that must all match.</summary>
    public static string[] ParseQuery(string? query) =>
        (query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
