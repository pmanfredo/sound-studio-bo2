using SoundStudio.Audio;
using SoundStudio.Core.Audio;
using SoundStudio.Core.Bo2;
using SoundStudio.Core.Library;

namespace SoundStudio.Core.Tests;

public class VolumeAndTypeTests
{
    private static DecodedAudio Tone(double amplitude, int frames = 4800)
    {
        var s = new short[frames];
        for (var i = 0; i < frames; i++)
            s[i] = (short)(amplitude * 32767 * Math.Sin(i * 0.05));
        var b = new byte[frames * 2];
        Buffer.BlockCopy(s, 0, b, 0, b.Length);
        return new DecodedAudio(b, 48000, 1);
    }

    [Fact]
    public void Gain_changes_level_by_the_requested_decibels()
    {
        var quiet = Tone(0.1);
        var before = AudioEdit.PeakDb(quiet);
        var (louder, applied) = AudioEdit.ApplyGain(quiet, 6);
        Assert.Equal(6, applied, 2);
        Assert.Equal(before + 6, AudioEdit.PeakDb(louder), 1);

        var (softer, appliedDown) = AudioEdit.ApplyGain(quiet, -12);
        Assert.Equal(-12, appliedDown, 2);
        Assert.Equal(before - 12, AudioEdit.PeakDb(softer), 1);
    }

    [Fact]
    public void Boosts_stop_short_of_distortion_unless_asked()
    {
        var loud = Tone(0.8); // about -1.9 dBFS
        var (safe, applied) = AudioEdit.ApplyGain(loud, 6);
        Assert.True(applied < 6 && applied > 1, $"applied {applied}");
        Assert.InRange(AudioEdit.PeakDb(safe), -0.6, -0.4);

        var (_, forced) = AudioEdit.ApplyGain(loud, 6, preventClipping: false);
        Assert.Equal(6, forced, 2);
    }

    [Fact]
    public void Prepared_replacement_carries_gain_and_keeps_it_when_trimmed()
    {
        var dir = Directory.CreateTempSubdirectory("ss-gain-");
        try
        {
            var wav = Path.Combine(dir.FullName, "in.wav");
            Tone(0.1, 48000).SaveWav(wav);
            var plain = ReplacementEncoder.Prepare(wav, AudioFormat.Flac, 1, 48000);
            var options = new ConvertOptions { GainDb = -6 };
            var quieter = ReplacementEncoder.Prepare(wav, AudioFormat.Flac, 1, 48000, options);
            Assert.Equal(-6, quieter.AppliedGainDb, 2);
            Assert.Equal(AudioEdit.PeakDb(plain.Audio) - 6, AudioEdit.PeakDb(quieter.Audio), 1);

            // What the game gets decodes to exactly what we previewed.
            var decoded = FlacDecoder.Decode(quieter.EncodedData);
            var expected = new short[quieter.Audio.Pcm16.Length / 2];
            Buffer.BlockCopy(quieter.Audio.Pcm16, 0, expected, 0, quieter.Audio.Pcm16.Length);
            Assert.Equal(expected, decoded.Samples);

            var trimmed = ReplacementEncoder.Prepare(wav, AudioFormat.Flac, 1, 48000, quieter.Options with { TrimTo = TimeSpan.FromSeconds(0.5) });
            Assert.Equal(-6, trimmed.AppliedGainDb, 2);
            Assert.Equal(24000, trimmed.SampleCount);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(@"raw\sound\evt\zombie_global\hellhounds\spawn\spn_flux_r.LN55.pc.snd", "Events & effects", "zombie_global")]
    [InlineData(@"raw\english\sound\vox\scripted\zmb_tomb\vox_maxi_audio_log_1_1_d_0.SN40.pc.snd", "Voices", "zmb_tomb")]
    [InlineData(@"devraw\sound\vox\dds\exerts\us\death\vox_us_death_00.SN55.pc.snd", "Voices", "combat chatter")]
    [InlineData(@"raw\sound\wpn\lmg\fire\lmg_fire_00.SN55.pc.snd", "Weapons", "lmg")]
    [InlineData(@"raw\sound\amb\alarms\blackout\carrier_alarm_modern.LL55.pc.snd", "Ambience", "alarms")]
    [InlineData(@"raw\sound\xyz_new\thing.pc.snd", "Xyz New", "")]
    [InlineData(null, "Unnamed", "")]
    public void Classifies_sounds_by_their_game_folder(string? path, string group, string subgroup)
    {
        var type = SoundTypes.Describe(path);
        Assert.Equal(group, type.Group);
        Assert.Equal(subgroup, type.Subgroup);
    }
}
