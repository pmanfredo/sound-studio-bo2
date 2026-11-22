using SoundStudio.Audio;

namespace SoundStudio.Core.Tests;

public class LayerMixerTests
{
    private static DecodedAudio Constant(short value, int frames, int rate = 48000, int channels = 1)
    {
        var s = Enumerable.Repeat(value, frames * channels).ToArray();
        var b = new byte[s.Length * 2];
        Buffer.BlockCopy(s, 0, b, 0, b.Length);
        return new DecodedAudio(b, rate, channels);
    }

    private static short[] Samples(DecodedAudio a)
    {
        var s = new short[a.Pcm16.Length / 2];
        Buffer.BlockCopy(a.Pcm16, 0, s, 0, a.Pcm16.Length);
        return s;
    }

    [Fact]
    public void Layers_add_up_and_the_mix_is_as_long_as_the_longest()
    {
        var r = LayerMixer.Mix([new MixLayer(Constant(1000, 100)), new MixLayer(Constant(2000, 300))], 48000, 1);
        var s = Samples(r.Audio);
        Assert.Equal(300, r.Audio.FrameCount);
        Assert.InRange(s[50], 2999, 3001);
        Assert.InRange(s[200], 1999, 2001);
        Assert.False(r.WasLimited);
    }

    [Fact]
    public void Offset_delays_a_layer_and_extends_the_mix()
    {
        var r = LayerMixer.Mix([new MixLayer(Constant(1000, 100)),
            new MixLayer(Constant(1000, 100)) { Offset = TimeSpan.FromSeconds(50 / 48000.0) }], 48000, 1);
        var s = Samples(r.Audio);
        Assert.Equal(150, r.Audio.FrameCount);
        Assert.InRange(s[10], 999, 1001);
        Assert.InRange(s[75], 1999, 2001);
        Assert.InRange(s[140], 999, 1001);
    }

    [Fact]
    public void Layer_volume_mute_and_master_volume_apply()
    {
        var r = LayerMixer.Mix([
            new MixLayer(Constant(1000, 100)) { GainDb = 20 * Math.Log10(0.5) },
            new MixLayer(Constant(8000, 100)) { Muted = true },
        ], 48000, 1, masterGainDb: 20 * Math.Log10(2));
        Assert.InRange(Samples(r.Audio)[10], 999, 1001); // 1000 * 0.5 * 2
    }

    [Fact]
    public void Loud_mixes_are_lowered_instead_of_distorting()
    {
        var r = LayerMixer.Mix([new MixLayer(Constant(30000, 100)), new MixLayer(Constant(30000, 100))], 48000, 1, masterGainDb: 6);
        Assert.True(r.WasLimited);
        Assert.True(r.AppliedMasterDb < 0);
        Assert.InRange(Samples(r.Audio)[10], 32000, 32767);
        Assert.True(r.PeakDb > 0);

        var clipped = LayerMixer.Mix([new MixLayer(Constant(30000, 100)), new MixLayer(Constant(30000, 100))], 48000, 1, preventClipping: false);
        Assert.False(clipped.WasLimited);
        Assert.Equal(32767, Samples(clipped.Audio)[10]);
    }

    [Fact]
    public void Layers_are_converted_to_the_target_format_and_length()
    {
        var stereo44 = Constant(1000, 4410, rate: 44100, channels: 2);
        var r = LayerMixer.Mix([new MixLayer(stereo44), new MixLayer(Constant(500, 48000))], 48000, 1, length: TimeSpan.FromSeconds(0.5));
        Assert.Equal(1, r.Audio.Channels);
        Assert.Equal(48000, r.Audio.SampleRate);
        Assert.Equal(24000, r.Audio.FrameCount);
        Assert.InRange(Samples(r.Audio)[2000], 1450, 1550); // both layers present
        Assert.InRange(Samples(r.Audio)[10000], 499, 501);  // only the long one
    }

    [Fact]
    public void Nothing_to_mix_gives_silence_of_the_requested_length()
    {
        var r = LayerMixer.Mix([new MixLayer(Constant(1000, 100)) { Muted = true }], 48000, 2, length: TimeSpan.FromSeconds(0.01));
        Assert.Equal(480, r.Audio.FrameCount);
        Assert.All(Samples(r.Audio), v => Assert.Equal(0, v));
        Assert.Equal(0, LayerMixer.Mix([], 48000, 1).Audio.FrameCount);
    }

    private static DecodedAudio Ramp(int frames, int rate = 48000)
    {
        var b = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            b[i * 2] = (byte)(i * 10);
            b[i * 2 + 1] = (byte)((i * 10) >> 8);
        }
        return new DecodedAudio(b, rate, 1);
    }

    private static TimeSpan Frames(int n) => TimeSpan.FromSeconds(n / 48000.0);

    [Fact]
    public void Trim_keeps_only_the_middle_of_a_layer()
    {
        var r = LayerMixer.Mix([new MixLayer(Ramp(1000)) { TrimStart = Frames(100), TrimEnd = Frames(200), Offset = Frames(50) }], 48000, 1);
        var s = Samples(r.Audio);
        Assert.Equal(50 + 700, r.Audio.FrameCount);
        Assert.Equal(0, s[10]);
        Assert.InRange(s[50], 999, 1001);   // frame 100 of the source
        Assert.InRange(s[749], 7989, 7991); // frame 799
    }

    [Fact]
    public void Fades_ramp_the_clip_in_and_out()
    {
        var r = LayerMixer.Mix([new MixLayer(Constant(10000, 1000)) { FadeIn = Frames(100), FadeOut = Frames(100) }], 48000, 1);
        var s = Samples(r.Audio);
        Assert.Equal(0, s[0]);
        Assert.InRange(s[50], 4900, 5100);
        Assert.InRange(s[500], 9999, 10001);
        Assert.InRange(s[949], 4900, 5100);
        Assert.Equal(0, s[999]);
    }

    [Fact]
    public void Trimming_everything_leaves_nothing()
    {
        var r = LayerMixer.Mix([new MixLayer(Constant(1000, 100)) { TrimStart = Frames(60), TrimEnd = Frames(60) }], 48000, 1);
        Assert.Equal(0, r.Audio.FrameCount);
    }
}
