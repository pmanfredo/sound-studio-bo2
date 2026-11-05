using System.Globalization;

namespace SoundStudio.Core.Library;

public sealed record FileMatch(string FilePath, IReadOnlyList<SoundInfo> Sounds)
{
    public bool IsMatched => Sounds.Count > 0;
}

/// <summary>
/// Matches audio files to sounds by name, for bulk replacement. A file matches a sound when its name (before the
/// first dot, case-insensitive) equals the sound's name, so all of these match <c>vox_us_death_00</c>:
/// <c>vox_us_death_00.wav</c>, <c>VOX_US_DEATH_00.mp3</c>, <c>vox_us_death_00.SN55.pc.flac</c>.
/// A file named after a sound ID (<c>8E14225F.wav</c>) matches that ID.
/// </summary>
public static class ReplacementMatcher
{
    public static string KeyForFile(string path)
    {
        var name = Path.GetFileName(path);
        var dot = name.IndexOf('.');
        return (dot > 0 ? name[..dot] : name).Trim().ToLowerInvariant();
    }

    public static IReadOnlyList<FileMatch> Match(IEnumerable<string> files, IEnumerable<SoundInfo> sounds)
    {
        var soundList = sounds.ToList();
        var byName = soundList.ToLookup(s => s.Name.ToLowerInvariant());
        var byId = soundList.ToLookup(s => s.Id);

        return files
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .Select(file =>
            {
                var key = KeyForFile(file);
                var matches = byName[key].ToList();
                if (matches.Count == 0 && key.Length == 8 &&
                    uint.TryParse(key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
                    matches = byId[id].ToList();
                return new FileMatch(file, matches);
            })
            .ToList();
    }
}
