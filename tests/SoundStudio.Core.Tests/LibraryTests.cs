using SoundStudio.Core.Library;

namespace SoundStudio.Core.Tests;

public class LibraryTests
{
    [Theory]
    [InlineData("zmb_tomb.english.sabs", BankCategory.Zombies, "Origins", "", "english", true)]
    [InlineData("zmb_tomb_load.all.sabs", BankCategory.Zombies, "Origins", "Loading screen", "all", true)]
    [InlineData("mpl_nuketown_2020.all.sabl", BankCategory.Multiplayer, "Nuketown 2025", "", "all", false)]
    [InlineData("zmb_returned_tranzit.all.sabl", BankCategory.Zombies, "Returned Tranzit", "", "all", false)]
    [InlineData("cmn_root.all.sabl", BankCategory.Common, "Core sounds", "", "all", false)]
    [InlineData("wpn_gaz_quad.all.sabl", BankCategory.Weapons, "Gaz Quad", "", "all", false)]
    public void Describes_bank_file_names(string file, BankCategory category, string map, string part, string lang, bool streamed)
    {
        var d = BankNaming.Describe(file);
        Assert.Equal(category, d.Category);
        Assert.Equal(map, d.MapName);
        Assert.Equal(part, d.Part);
        Assert.Equal(lang, d.Language);
        Assert.Equal(streamed, d.IsStreamed);
    }

    [Fact]
    public void Search_terms_must_all_match()
    {
        var terms = GameLibrary.ParseQuery("  Death  US ");
        Assert.Equal(["death", "us"], terms);
    }

    [SkippableFact]
    public async Task Scans_game_folder_and_reuses_cache()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var cachePath = Path.Combine(Path.GetTempPath(), $"soundstudio-cache-{Guid.NewGuid():N}.json");
        try
        {
            var scanner = new LibraryScanner(cachePath);
            var first = await scanner.ScanAsync(GameFolder.Path);
            Assert.Equal(GameFolder.Banks().Count(), first.Banks.Count);
            Assert.All(first.Banks, b => Assert.Null(b.Error));
            Assert.True(File.Exists(cachePath));

            var reports = new List<ScanProgress>();
            var second = await scanner.ScanAsync(GameFolder.Path, new SyncProgress<ScanProgress>(reports.Add));
            Assert.All(reports, r => Assert.True(r.FromCache));
            Assert.Equal(first.ModifiedCount, second.ModifiedCount);
            Assert.Equal(
                first.AllSounds.Where(s => s.IsModified).Select(s => (s.Bank.FileName, s.Index)),
                second.AllSounds.Where(s => s.IsModified).Select(s => (s.Bank.FileName, s.Index)));

            var hits = second.AllSounds.Where(s => s.Matches(GameLibrary.ParseQuery("vox_us_death_00"))).ToList();
            Assert.Contains(hits, s => s.Bank.FileName == "cmn_root.all.sabs" && s.IsModified);
        }
        finally
        {
            File.Delete(cachePath);
        }
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
