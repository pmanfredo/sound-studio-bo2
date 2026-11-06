using SoundSpotter;

namespace SoundSpotter.Tests;

public class FingerprintTests
{
    private const int Rate = Fingerprinter.SampleRate;

    /// <summary>A random "sound": a few gliding tones with random note changes and an envelope.</summary>
    private static float[] Synth(int seed, double seconds)
    {
        var rng = new Random(seed);
        var n = (int)(seconds * Rate);
        var s = new float[n];
        for (var voice = 0; voice < 3; voice++)
        {
            double phase = 0, freq = 200 + rng.NextDouble() * 3000;
            for (var i = 0; i < n; i++)
            {
                if (i % (Rate / 8) == 0) freq = 200 + rng.NextDouble() * 3000;
                phase += 2 * Math.PI * freq / Rate;
                s[i] += (float)(0.25 * Math.Sin(phase) * (0.5 + 0.5 * Math.Sin(i * 0.0007 + voice)));
            }
        }
        return s;
    }

    private static FingerprintIndex Library(int count, out List<float[]> sounds)
    {
        sounds = Enumerable.Range(0, count).Select(i => Synth(i, 4)).ToList();
        var index = new FingerprintIndex();
        for (var i = 0; i < count; i++) index.Add(i, Fingerprinter.Landmarks(sounds[i]));
        return index;
    }

    [Fact]
    public void A_window_of_a_sound_finds_that_sound()
    {
        var index = Library(30, out var sounds);
        for (var t = 0; t < 30; t += 3)
        {
            var q = sounds[t].Skip(Rate).Take(Rate * 3 / 2).ToArray();
            Assert.Equal(t, index.Query(Fingerprinter.Landmarks(q))[0].SoundId);
        }
    }

    [Fact]
    public void Two_overlapping_sounds_both_rank_in_the_top_three()
    {
        var index = Library(30, out var sounds);
        var mix = sounds[4].Zip(sounds[17], (a, b) => a + b).Skip(Rate / 2).Take(Rate * 2).ToArray();
        var top = index.Query(Fingerprinter.Landmarks(mix)).Select(h => h.SoundId).ToList();
        Assert.Contains(4, top);
        Assert.Contains(17, top);
    }

    [Fact]
    public void Silence_matches_nothing()
    {
        var index = Library(10, out _);
        Assert.Empty(index.Query(Fingerprinter.Landmarks(new float[Rate * 2])));
    }

    [Fact]
    public void Resampling_keeps_duration()
    {
        var s = Synth(1, 1);
        var down = Fingerprinter.Resample(s, Rate * 4, Rate);
        Assert.Equal(s.Length / 4, down.Length);
        var pcm = new byte[48000 * 2 * 2]; // 1 s stereo 48 kHz silence
        Assert.InRange(Fingerprinter.ToMono11k(pcm, 48000, 2).Length, Rate - 2, Rate + 2);
    }
}
