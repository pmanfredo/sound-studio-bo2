namespace SoundStudio.Core.Tests;

/// <summary>Locates real BO2 banks. Override with the SOUNDSTUDIO_BO2_SOUND env var.</summary>
internal static class GameFolder
{
    public static readonly string Path =
        Environment.GetEnvironmentVariable("SOUNDSTUDIO_BO2_SOUND")
        ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            @"Plutonium\Black_Ops_II\pluto_t6_full_game\sound");

    public static bool Available => Directory.Exists(Path);

    public static IEnumerable<string> Banks() =>
        Available
            ? Directory.EnumerateFiles(Path).Where(f => f.EndsWith(".sabs") || f.EndsWith(".sabl")).OrderBy(f => f)
            : [];

    /// <summary>Smallest bank with at least <paramref name="minEntries"/> sounds and no existing mods.</summary>
    public static string SmallCleanBank(int minEntries = 3) =>
        Banks().OrderBy(f => new FileInfo(f).Length).First(f =>
        {
            using var b = Bo2.SabBank.Open(f);
            return b.Entries.Count >= minEntries && !b.Entries.Any(e => e.IsModifiedOnDisk());
        });

    public static IEnumerable<object[]> BankData() => Banks().Select(f => new object[] { System.IO.Path.GetFileName(f) });
}
