using System.Security.Cryptography;
using SoundStudio.Core.Bo2;
using SoundStudio.Core.Editing;

namespace SoundStudio.Core.Tests;

public sealed class BankEditorTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("soundstudio-edit-");
    private readonly BankEditor _editor;

    public BankEditorTests()
    {
        _editor = new BankEditor(
            new OriginalsStore(Path.Combine(_dir.FullName, "originals")),
            new ChangeHistory(Path.Combine(_dir.FullName, "history.jsonl")),
            author: "tester");
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private string CopyCleanBank(string name = "test.all.sabl")
    {
        var dest = Path.Combine(_dir.FullName, "game", name);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(GameFolder.SmallCleanBank(), dest, overwrite: true);
        return dest;
    }

    private static string Sha(string path) { using var fs = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(fs)); }

    private static SoundChange RandomChange(string bankPath, int index, int size = 5000)
    {
        using var bank = SabBank.Open(bankPath);
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        return new SoundChange(index, bank.Entries[index].Id, data, size / 2, "test.wav");
    }

    [SkippableFact]
    public void Apply_then_revert_restores_the_bank_byte_for_byte()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var path = CopyCleanBank();
        var originalSha = Sha(path);

        _editor.Apply(path, [RandomChange(path, 0), RandomChange(path, 2, 9999)]);
        Assert.NotEqual(originalSha, Sha(path));
        using (var changed = SabBank.Open(path))
        {
            Assert.True(changed.Entries[0].IsModifiedOnDisk());
            Assert.False(changed.Entries[1].IsModifiedOnDisk());
            Assert.True(changed.Entries[2].IsModifiedOnDisk());
        }

        var result = _editor.Revert(path, [0, 1, 2]);
        Assert.Equal(2, result.Reverted);
        Assert.Equal(1, result.AlreadyOriginal);
        Assert.Empty(result.MissingOriginals);
        Assert.Equal(originalSha, Sha(path));

        var history = _editor.History.ReadAll();
        Assert.Equal([ChangeAction.Replaced, ChangeAction.Replaced, ChangeAction.Reverted, ChangeAction.Reverted], history.Select(h => h.Action));
        Assert.All(history, h => Assert.Equal("tester", h.Author));
        Assert.Equal("test.wav", history[0].Detail);
    }

    [SkippableFact]
    public void Second_change_keeps_the_first_original()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var path = CopyCleanBank();
        var originalSha = Sha(path);
        _editor.Apply(path, [RandomChange(path, 1)]);
        _editor.Apply(path, [RandomChange(path, 1, 7000)]); // replace again: original must still be the game audio
        _editor.Revert(path, [1]);
        Assert.Equal(originalSha, Sha(path));
    }

    [SkippableFact]
    public void Sounds_modified_by_another_tool_report_missing_original_until_imported()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var clean = CopyCleanBank("clean.all.sabl");
        var path = CopyCleanBank();

        // Simulate the old tool: change audio without saving an original.
        var bank = SabBank.Open(path);
        bank.Entries[1].Replace(new byte[1234], 617);
        SabWriter.SaveInPlace(bank).Dispose();

        var result = _editor.Revert(path, [1]);
        Assert.Equal([1], result.MissingOriginals);

        Assert.Equal(1, _editor.ImportOriginals(clean, path));
        Assert.Equal(0, _editor.ImportOriginals(path, path)); // a modified copy never yields "originals"

        Assert.Equal(1, _editor.Revert(path, [1]).Reverted);
        Assert.Equal(Sha(clean), Sha(path));
        Assert.Contains(_editor.History.ReadAll(), h => h.Action == ChangeAction.OriginalRecovered);
    }

    [SkippableFact]
    public void Locked_bank_gives_a_friendly_error_and_stays_intact()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var path = CopyCleanBank();
        var originalSha = Sha(path);
        var change = RandomChange(path, 0);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) // like the game holding it open
        {
            var ex = Assert.Throws<BankLockedException>(() => _editor.Apply(path, [change]));
            Assert.Contains("Close Black Ops II", ex.Message);
        }
        Assert.Equal(originalSha, Sha(path));
        Assert.False(File.Exists(path + ".saving"));
        Assert.Empty(_editor.History.ReadAll());
    }

    [SkippableFact]
    public void Stale_index_is_rejected()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var path = CopyCleanBank();
        Assert.Throws<InvalidOperationException>(() => _editor.Apply(path, [new SoundChange(0, 0xDEADBEEF, new byte[10], 5)]));
    }
}
