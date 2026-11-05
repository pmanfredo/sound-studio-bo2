using System.Diagnostics;
using NAudio.Wave;
using SoundStudio.Audio;
using SoundStudio.Core.Audio;
using SoundStudio.Core.Bo2;
using SoundStudio.Core.Editing;
using SoundStudio.Core.Library;

namespace SoundStudio.Core.Tests;

public sealed class ReplacementTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("soundstudio-replace-");
    public void Dispose() => _dir.Delete(recursive: true);

    private static readonly string? FfmpegExe = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Downloads\v1.6.1-windows\ffmpeg\bin\ffmpeg.exe"),
    }.FirstOrDefault(File.Exists);

    /// <summary>Speech-like test signal: tones, noise bursts, silence, a full-scale click and DC.</summary>
    private static short[] TestSignal(int frames, int channels, int seed = 1)
    {
        var rng = new Random(seed);
        var s = new short[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            for (var c = 0; c < channels; c++)
            {
                double v = i switch
                {
                    < 3000 => 0,
                    < 20000 => 0.6 * Math.Sin(i * (0.03 + c * 0.01)) + 0.1 * (rng.NextDouble() - 0.5),
                    < 20010 => c == 0 ? 1 : -1,
                    < 30000 => 0.25,
                    _ => (rng.NextDouble() - 0.5) * 1.9,
                };
                s[i * channels + c] = (short)Math.Clamp(v * 32767, short.MinValue, short.MaxValue);
            }
        }
        return s;
    }

    [Theory]
    [InlineData(1, 48000, 41_000)]
    [InlineData(2, 48000, 52_225)]  // odd length: last block is partial
    [InlineData(1, 44100, 1_000)]   // shorter than one block
    [InlineData(2, 32000, 1024)]    // exactly one block
    public void Flac_encoder_round_trips_exactly(int channels, int rate, int frames)
    {
        var input = TestSignal(frames, channels);
        var flac = FlacEncoder.Encode(input, channels, rate);
        var decoded = FlacDecoder.Decode(flac);
        Assert.Equal(rate, decoded.SampleRate);
        Assert.Equal(channels, decoded.Channels);
        Assert.Equal(frames, decoded.DeclaredTotalSamples);
        Assert.Equal(input, decoded.Samples);
        Assert.True(flac.Length < input.Length * 2, "should compress");
    }

    [SkippableFact]
    public void Flac_encoder_output_is_read_identically_by_windows_and_ffmpeg()
    {
        var input = TestSignal(60_000, 2, seed: 7);
        var flac = FlacEncoder.Encode(input, 2, 48000);

        NAudio.MediaFoundation.MediaFoundationApi.Startup();
        using (var mf = new StreamMediaFoundationReader(new MemoryStream(flac)))
        {
            var ms = new MemoryStream();
            mf.CopyTo(ms);
            Assert.Equal(input, ToShorts(ms.ToArray()));
        }

        Skip.If(FfmpegExe == null, "ffmpeg not found");
        var path = Path.Combine(_dir.FullName, "t.flac");
        File.WriteAllBytes(path, flac);
        var psi = new ProcessStartInfo(FfmpegExe!, $"-hide_banner -loglevel error -i \"{path}\" -f s16le -")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var output = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(output);
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        Assert.Equal(input, ToShorts(output.ToArray()));
    }

    [Fact]
    public void Converts_rate_and_channels_and_trims()
    {
        var src = new DecodedAudio(Bytes(TestSignal(44100 * 2, 2)), 44100, 2); // 2 s stereo @ 44.1k
        var wav = Path.Combine(_dir.FullName, "in.wav");
        src.SaveWav(wav);

        var mono = ReplacementEncoder.Prepare(wav, AudioFormat.Pcm16, channels: 1, sampleRate: 48000);
        Assert.Equal(1, mono.Audio.Channels);
        Assert.Equal(48000, mono.Audio.SampleRate);
        Assert.InRange(mono.Duration.TotalSeconds, 1.99, 2.01);
        Assert.Equal(mono.Audio.Pcm16, mono.EncodedData); // PCM goes in headerless

        var trimmed = ReplacementEncoder.Prepare(wav, AudioFormat.Flac, 2, 48000, new ConvertOptions { TrimTo = TimeSpan.FromSeconds(0.5) });
        Assert.Equal(24000, trimmed.SampleCount);
        Assert.Equal(2.0, trimmed.SourceDuration.TotalSeconds, 0.01);
        var back = FlacDecoder.Decode(trimmed.EncodedData);
        Assert.Equal(24000, back.FrameCount);
        Assert.Equal(0, back.Samples[^1]); // faded out
    }

    [SkippableFact]
    public void Replacement_goes_into_a_bank_and_plays_back_exactly()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        foreach (var bankName in new[] { "zmb_tomb_load.english.sabs", "wpn_slingshot.all.sabl" }) // FLAC bank, PCM bank
        {
            var bankPath = Path.Combine(_dir.FullName, bankName);
            File.Copy(Path.Combine(GameFolder.Path, bankName), bankPath);
            int index; uint id; AudioFormat format; int channels, rate;
            using (var b = SabBank.Open(bankPath))
            {
                var e = b.Entries[0];
                (index, id, format, channels, rate) = (e.Index, e.Id, e.Format, e.ChannelCount, e.SampleRate);
            }

            var wav = Path.Combine(_dir.FullName, "voice.wav");
            new DecodedAudio(Bytes(TestSignal(30_000, 1)), 22050, 1).SaveWav(wav);
            var prepared = ReplacementEncoder.Prepare(wav, format, channels, rate);

            var editor = new BankEditor(new OriginalsStore(Path.Combine(_dir.FullName, "o")),
                new ChangeHistory(Path.Combine(_dir.FullName, "h.jsonl")), "t");
            editor.Apply(bankPath, [new SoundChange(index, id, prepared.EncodedData, prepared.SampleCount, "voice.wav")]);

            var readBack = AudioDecoder.Decode(SoundReader.Read(bankPath, index, id));
            Assert.Equal(prepared.Audio.Pcm16, readBack.Pcm16);
            using var reopened = SabBank.Open(bankPath);
            Assert.Equal(prepared.SampleCount, reopened.Entries[index].SampleCount);
            Assert.True(reopened.Entries[index].IsModifiedOnDisk());
        }
    }

    [Fact]
    public void Matches_files_to_sounds_by_name_or_id()
    {
        var bank = new BankInfo
        {
            FilePath = @"C:\x\cmn_root.all.sabs", FileLength = 0, LastWriteTimeUtc = default,
            Description = BankNaming.Describe("cmn_root.all.sabs"),
        };
        SoundInfo Sound(uint id, int index) => new()
        {
            Bank = bank, Index = index, Id = id, Format = AudioFormat.Flac, SampleCount = 1, SampleRate = 48000,
            Channels = 1, Loop = false, Size = 1,
        };
        var death00 = Sound(0x8E14225F, 0); // vox_us_death_00 in the name database
        var death01 = Sound(0x5A9C3F60, 1); // vox_us_death_01
        var matches = ReplacementMatcher.Match(
            [@"D:\a\VOX_US_DEATH_00.SN55.pc.wav", @"D:\a\5A9C3F60.mp3", @"D:\a\readme.wav"], [death00, death01]);

        Assert.Equal([death01], matches.Single(m => m.FilePath.EndsWith("5A9C3F60.mp3")).Sounds);
        Assert.Equal([death00], matches.Single(m => m.FilePath.Contains("DEATH_00")).Sounds);
        Assert.False(matches.Single(m => m.FilePath.EndsWith("readme.wav")).IsMatched);
    }

    private static byte[] Bytes(short[] s) { var b = new byte[s.Length * 2]; Buffer.BlockCopy(s, 0, b, 0, b.Length); return b; }
    private static short[] ToShorts(byte[] b) { var s = new short[b.Length / 2]; Buffer.BlockCopy(b, 0, s, 0, s.Length * 2); return s; }
}
