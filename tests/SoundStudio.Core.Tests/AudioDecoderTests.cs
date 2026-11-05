using NAudio.Wave;
using SoundStudio.Audio;
using SoundStudio.Core.Bo2;

namespace SoundStudio.Core.Tests;

public class AudioDecoderTests
{
    private static (string Bank, SabEntry Entry, SabBank Owner) FindEntry(AudioFormat format, string bankName)
    {
        var bank = SabBank.Open(Path.Combine(GameFolder.Path, bankName));
        var entry = bank.Entries.First(e => e.Format == format && !e.IsModifiedOnDisk());
        return (bank.FilePath!, entry, bank);
    }

    [SkippableTheory]
    [InlineData(AudioFormat.Flac, "zmb_tomb.english.sabs")]
    [InlineData(AudioFormat.Pcm16, "zmb_tomb.all.sabl")]
    public void Decodes_game_audio_with_the_expected_length(AudioFormat format, string bankName)
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var (path, entry, owner) = FindEntry(format, bankName);
        using (owner)
        {
            var sound = SoundReader.Read(path, entry.Index, entry.Id);
            var audio = AudioDecoder.Decode(sound);

            Assert.Equal(entry.ChannelCount, audio.Channels);
            Assert.Equal(entry.SampleRate, audio.SampleRate);
            // The table's sample count should match the decoded length (allow a frame or two of slack).
            Assert.InRange(audio.FrameCount, entry.SampleCount - 2048, entry.SampleCount + 2048);

            var peaks = audio.ComputePeaks(400);
            Assert.Equal(800, peaks.Length);
            Assert.Contains(peaks, p => Math.Abs(p) > 0.01f); // not silence
        }
    }

    [SkippableFact]
    public void Exported_wav_reads_back_identically()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var (path, entry, owner) = FindEntry(AudioFormat.Flac, "zmb_tomb.english.sabs");
        using (owner)
        {
            var audio = AudioDecoder.Decode(SoundReader.Read(path, entry.Index, entry.Id));
            var wav = Path.Combine(Path.GetTempPath(), $"soundstudio-{Guid.NewGuid():N}.wav");
            try
            {
                audio.SaveWav(wav);
                var again = AudioDecoder.DecodeFile(wav);
                Assert.Equal(audio.FrameCount, again.FrameCount);
                Assert.Equal(audio.Channels, again.Channels);
                using var reader = new WaveFileReader(wav);
                Assert.Equal(16, reader.WaveFormat.BitsPerSample);
            }
            finally
            {
                File.Delete(wav);
            }
        }
    }

    [Fact]
    public void Peaks_of_silence_are_zero_and_rejects_xma()
    {
        var audio = new DecodedAudio(new byte[48000 * 2], 48000, 1);
        Assert.All(audio.ComputePeaks(10), p => Assert.Equal(0f, p));
        Assert.Equal(TimeSpan.FromSeconds(1), audio.Duration);
        Assert.Throws<NotSupportedException>(() => AudioDecoder.Decode(new byte[10], AudioFormat.Xma4, 1, 48000));
    }

    [SkippableFact]
    public void Player_loads_seeks_and_unloads()
    {
        Skip.IfNot(GameFolder.Available, "BO2 sound folder not found.");
        var audio = new DecodedAudio(new byte[48000 * 2 * 2 * 3], 48000, 2); // 3 s of stereo silence
        using var player = new AudioPlayer();
        player.Load(audio);
        Assert.Equal(TimeSpan.FromSeconds(3), player.Duration);
        player.Position = TimeSpan.FromSeconds(1.5);
        Assert.InRange(player.Position.TotalSeconds, 1.49, 1.51);
        player.Volume = 2f;
        Assert.Equal(1f, player.Volume);
        player.Unload();
        Assert.Null(player.Audio);
    }
}
