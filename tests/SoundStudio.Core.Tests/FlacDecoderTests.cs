using System.Diagnostics;
using NAudio.Wave;
using SoundStudio.Core.Audio;
using SoundStudio.Core.Bo2;

namespace SoundStudio.Core.Tests;

public class FlacDecoderTests
{
    /// <summary>ffmpeg from the original Sound Studio download, used as a reference decoder if present.</summary>
    private static readonly string? Ffmpeg = new[]
    {
        Environment.GetEnvironmentVariable("SOUNDSTUDIO_FFMPEG"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Downloads\v1.6.1-windows\ffmpeg\bin\ffmpeg.exe"),
    }.FirstOrDefault(File.Exists);

    /// <summary>Original game FLACs: must match Windows' decoder sample for sample.</summary>
    [SkippableFact]
    public void Matches_windows_decoder_on_original_game_audio()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        NAudio.MediaFoundation.MediaFoundationApi.Startup();
        using var bank = SabBank.Open(Path.Combine(GameFolder.Path, "zmb_tomb.english.sabs"));
        var checkedCount = 0;
        foreach (var entry in bank.Entries.Where(e => e.Format == AudioFormat.Flac).Take(40))
        {
            var data = entry.ReadOriginalData();
            var ours = FlacDecoder.Decode(data);
            var reference = DecodeWithMediaFoundation(data);
            Assert.Equal(reference.Length, ours.Samples.Length);
            Assert.True(reference.AsSpan().SequenceEqual(ours.Samples), $"{entry.DisplayName} differs");
            Assert.Equal(entry.SampleCount, ours.FrameCount);
            checkedCount++;
        }
        Assert.True(checkedCount > 10);
    }

    /// <summary>Re-encoded (modded) FLACs: must match ffmpeg sample for sample and have the full length.</summary>
    [SkippableFact]
    public void Matches_ffmpeg_on_modified_sounds()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        Skip.If(Ffmpeg == null, "ffmpeg not found.");
        var banks = new[] { "cmn_root.all.sabs", "mpl_code_post_gfx.english.sabs", "mpl_common.all.sabs" }
            .Select(n => SabBank.Open(Path.Combine(GameFolder.Path, n))).ToList();
        using var _ = new DisposeAll(banks);
        var modified = banks.SelectMany(b => b.Entries.Where(e => e.Format == AudioFormat.Flac && e.IsModifiedOnDisk()).Take(25)).ToList();
        Skip.If(modified.Count == 0, "No modified FLAC sounds to compare.");
        var saturatedSounds = 0;
        foreach (var entry in modified)
        {
            var data = entry.ReadOriginalData();
            var ours = FlacDecoder.Decode(data);
            var reference = DecodeWithFfmpeg(data);
            Assert.Equal(reference.Length, ours.Samples.Length);
            // ffmpeg agrees exactly, except where the old tool's broken residuals overflow: ffmpeg pins those at
            // full scale (+/-32767), while we wrap like libFLAC + a 16-bit cast and get continuous audio.
            var mismatches = Enumerable.Range(0, reference.Length).Where(i => reference[i] != ours.Samples[i]).ToList();
            var unexplained = mismatches.Where(i => reference[i] is not (short.MaxValue or short.MinValue)).ToList();
            Assert.True(unexplained.Count == 0, $"{entry.DisplayName}: {unexplained.Count} samples differ from ffmpeg (first at {unexplained.FirstOrDefault()})");
            if (mismatches.Count > 0)
                saturatedSounds++;
            Assert.InRange(ours.FrameCount, entry.SampleCount - 4096, entry.SampleCount + 4096);
        }
    }

    [SkippableFact]
    public void Decodes_every_flac_in_a_big_bank_quickly()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        using var bank = SabBank.Open(Path.Combine(GameFolder.Path, "mpl_code_post_gfx.english.sabs"));
        var sw = Stopwatch.StartNew();
        long frames = 0;
        foreach (var e in bank.Entries.Where(e => e.Format == AudioFormat.Flac).Take(500))
        {
            var audio = FlacDecoder.Decode(e.ReadOriginalData());
            Assert.True(audio.FrameCount > 0, e.DisplayName);
            frames += audio.FrameCount;
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"too slow: {sw.Elapsed}");
    }

    private static short[] DecodeWithMediaFoundation(byte[] data)
    {
        using var reader = new StreamMediaFoundationReader(new MemoryStream(data));
        var ms = new MemoryStream();
        reader.CopyTo(ms);
        return ToShorts(ms.ToArray());
    }

    private static short[] DecodeWithFfmpeg(byte[] data)
    {
        var input = Path.Combine(Path.GetTempPath(), $"ss-{Guid.NewGuid():N}.flac");
        File.WriteAllBytes(input, data);
        try
        {
            var psi = new ProcessStartInfo(Ffmpeg!, $"-hide_banner -loglevel error -i \"{input}\" -f s16le -")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit();
            return ToShorts(ms.ToArray());
        }
        finally
        {
            File.Delete(input);
        }
    }

    private sealed class DisposeAll(IEnumerable<IDisposable> items) : IDisposable
    {
        public void Dispose() { foreach (var i in items) i.Dispose(); }
    }

    private static short[] ToShorts(byte[] bytes)
    {
        var s = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, s, 0, s.Length * 2);
        return s;
    }
}
