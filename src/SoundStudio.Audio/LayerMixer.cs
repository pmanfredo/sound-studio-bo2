namespace SoundStudio.Audio;

/// <summary>One sound in a mix.</summary>
public sealed record MixLayer(DecodedAudio Audio)
{
    /// <summary>Volume change for this layer in dB (0 = as is).</summary>
    public double GainDb { get; init; }

    /// <summary>When this layer starts, from the start of the mix.</summary>
    public TimeSpan Offset { get; init; }

    public bool Muted { get; init; }

    /// <summary>How much is cut off the start and the end of <see cref="Audio"/>.</summary>
    public TimeSpan TrimStart { get; init; }
    public TimeSpan TrimEnd { get; init; }

    /// <summary>Fade in at the start and fade out at the end of the (trimmed) clip.</summary>
    public TimeSpan FadeIn { get; init; }
    public TimeSpan FadeOut { get; init; }
}

/// <summary>The result of mixing: the audio, and what the master volume ended up being.</summary>
public sealed record MixResult(DecodedAudio Audio, double AppliedMasterDb, bool WasLimited)
{
    /// <summary>Peak level of the mix in dBFS before any limiting (≥ 0 means it would distort).</summary>
    public double PeakDb { get; init; }
}

/// <summary>Layers several sounds on top of each other into one, in the format a bank entry needs.</summary>
public static class LayerMixer
{
    /// <summary>
    /// Sums the unmuted layers (each converted to <paramref name="sampleRate"/>/<paramref name="channels"/>, with its
    /// own volume and start offset), then applies <paramref name="masterGainDb"/>. With
    /// <paramref name="preventClipping"/> the master volume is lowered just enough that nothing distorts;
    /// otherwise peaks are clipped. Each layer is trimmed first, then faded, then placed at its offset. The mix is as long as the longest layer, or <paramref name="length"/> if given.
    /// </summary>
    public static MixResult Mix(IReadOnlyList<MixLayer> layers, int sampleRate, int channels, double masterGainDb = 0,
        bool preventClipping = true, TimeSpan? length = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        var active = layers.Where(l => !l.Muted && l.Audio.FrameCount > 0)
            .Select(l =>
            {
                var audio = ReplacementEncoder.Convert(l.Audio, sampleRate, channels);
                var from = Math.Clamp((int)Math.Round(l.TrimStart.TotalSeconds * sampleRate), 0, audio.FrameCount);
                var to = Math.Clamp(audio.FrameCount - (int)Math.Round(l.TrimEnd.TotalSeconds * sampleRate), from, audio.FrameCount);
                return (Layer: l, Audio: audio, From: from, Frames: to - from,
                    Start: (int)Math.Round(Math.Max(0, l.Offset.TotalSeconds) * sampleRate));
            })
            .Where(a => a.Frames > 0)
            .ToList();

        var frames = length is { } fixedLength
            ? (int)Math.Round(fixedLength.TotalSeconds * sampleRate)
            : active.Count == 0 ? 0 : active.Max(a => a.Start + a.Frames);

        var mix = new float[(long)frames * channels];
        foreach (var (layer, audio, from, clipFrames, start) in active)
        {
            var gain = (float)Math.Pow(10, layer.GainDb / 20);
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(audio.Pcm16.AsSpan());
            var fadeIn = Math.Min(clipFrames, (int)Math.Round(Math.Max(0, layer.FadeIn.TotalSeconds) * sampleRate));
            var fadeOut = Math.Min(clipFrames, (int)Math.Round(Math.Max(0, layer.FadeOut.TotalSeconds) * sampleRate));
            var count = Math.Min(clipFrames, frames - start);
            for (var f = 0; f < count; f++)
            {
                var g = gain;
                if (f < fadeIn)
                    g *= (float)f / fadeIn;
                if (clipFrames - f <= fadeOut)
                    g *= (float)(clipFrames - f - 1) / fadeOut;
                var src = (from + f) * channels;
                var dest = (start + f) * channels;
                for (var c = 0; c < channels; c++)
                    mix[dest + c] += samples[src + c] / 32768f * g;
            }
        }

        var peak = 0f;
        foreach (var s in mix)
            peak = Math.Max(peak, Math.Abs(s));

        var master = (float)Math.Pow(10, masterGainDb / 20);
        var appliedDb = masterGainDb;
        var limited = false;
        const float ceiling = 0.989f; // about -0.1 dBFS
        if (preventClipping && peak * master > ceiling)
        {
            master = ceiling / peak;
            appliedDb = 20 * Math.Log10(master);
            limited = true;
        }

        var pcm = new byte[mix.Length * 2];
        for (var i = 0; i < mix.Length; i++)
        {
            var v = (int)Math.Round(Math.Clamp(mix[i] * master, -1f, 1f) * 32767f);
            pcm[i * 2] = (byte)v;
            pcm[i * 2 + 1] = (byte)(v >> 8);
        }

        return new MixResult(new DecodedAudio(pcm, sampleRate, channels), appliedDb, limited)
        {
            PeakDb = peak <= 0 ? double.NegativeInfinity : 20 * Math.Log10(peak * Math.Pow(10, masterGainDb / 20)),
        };
    }
}
