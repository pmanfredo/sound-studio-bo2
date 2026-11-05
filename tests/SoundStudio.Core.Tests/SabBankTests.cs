using System.Security.Cryptography;
using SoundStudio.Core.Bo2;

namespace SoundStudio.Core.Tests;

public class SabBankTests
{
    /// <summary>The key safety guarantee: reading and re-saving an unchanged bank reproduces it byte for byte.</summary>
    [SkippableTheory]
    [MemberData(nameof(GameFolder.BankData), MemberType = typeof(GameFolder))]
    public void Unchanged_bank_round_trips_byte_for_byte(string fileName)
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var path = Path.Combine(GameFolder.Path, fileName);

        byte[] original;
        using (var fs = File.OpenRead(path))
            original = SHA256.HashData(fs);

        using var bank = SabBank.Open(path);
        using var hashing = new HashingStream();
        SabWriter.Write(bank, hashing);

        Assert.Equal(new FileInfo(path).Length, hashing.Length);
        Assert.Equal(Convert.ToHexString(original), Convert.ToHexString(hashing.Finish()));
    }

    [SkippableFact]
    public void Replacing_a_sound_changes_only_that_sound_and_keeps_its_original_checksum()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        using var bank = SabBank.Open(GameFolder.SmallCleanBank());
        var target = bank.Entries[1];
        var before = bank.Entries.Select(e => e.ReadData()).ToList();
        var originalChecksum = target.Checksum.ToArray();

        var newAudio = new byte[before[1].Length + 12345];
        Random.Shared.NextBytes(newAudio);
        target.Replace(newAudio, sampleCount: target.SampleCount + 100);
        Assert.True(bank.HasPendingChanges);

        var ms = new MemoryStream();
        SabWriter.Write(bank, ms);
        ms.Position = 0;
        using var saved = SabBank.Open(ms);

        Assert.Equal(bank.Entries.Count, saved.Entries.Count);
        for (var i = 0; i < saved.Entries.Count; i++)
        {
            var e = saved.Entries[i];
            Assert.Equal(bank.Entries[i].Id, e.Id);
            if (i == 1)
            {
                Assert.Equal(newAudio, e.ReadData());
                Assert.Equal(target.SampleCount, e.SampleCount);
                Assert.Equal(originalChecksum, e.Checksum);
                Assert.True(e.IsModifiedOnDisk());
            }
            else
            {
                Assert.Equal(before[i], e.ReadData());
                Assert.False(e.IsModifiedOnDisk());
            }
        }
        Assert.Equal(0, saved.FileLength % SabBank.SectionAlignment);
        Assert.Equal(saved.FileLength, saved.DeclaredLength);
    }

    [SkippableFact]
    public void Recompute_mode_updates_the_checksum()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        using var bank = SabBank.Open(GameFolder.SmallCleanBank());
        var data = new byte[4096];
        Random.Shared.NextBytes(data);
        bank.Entries[0].Replace(data, 2048);

        var ms = new MemoryStream();
        SabWriter.Write(bank, ms, ChecksumMode.Recompute);
        ms.Position = 0;
        using var saved = SabBank.Open(ms);
        Assert.Equal(MD5.HashData(data), saved.Entries[0].Checksum);
        Assert.False(saved.Entries[0].IsModifiedOnDisk());
    }

    [SkippableFact]
    public void SaveInPlace_writes_safely_and_reopens()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var dir = Directory.CreateTempSubdirectory("soundstudio-test-");
        try
        {
            var copy = Path.Combine(dir.FullName, "copy.all.sabl");
            File.Copy(GameFolder.SmallCleanBank(), copy);

            var bank = SabBank.Open(copy);
            var data = new byte[777];
            Random.Shared.NextBytes(data);
            bank.Entries[0].Replace(data, 300);

            using var reopened = SabWriter.SaveInPlace(bank);
            Assert.Equal(data, reopened.Entries[0].ReadData());
            Assert.False(reopened.HasPendingChanges);
            Assert.False(File.Exists(copy + ".saving"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Rejects_non_bank_data()
    {
        var ex = Assert.Throws<InvalidDataException>(() => SabBank.Open(new MemoryStream(new byte[0x1000])));
        Assert.Contains("magic", ex.Message);
    }

    [Fact]
    public void Name_database_resolves_known_ids()
    {
        Assert.True(SoundNameDatabase.Count > 300_000);
        Assert.EndsWith("vox_us_death_00.SN55.pc.snd", SoundNameDatabase.TryGetPath(0x8E14225F));
    }
}
