using NAudio.Wave;

namespace SoundStudio.Audio;

/// <summary>Uncompressed 16-bit interleaved PCM, ready to play, draw or export.</summary>
public sealed class DecodedAudio
{
    public DecodedAudio(byte[] pcm16, int sampleRate, int channels)
    {
        if (channels <= 0 || sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels), "Invalid audio format.");
        Pcm16 = pcm16;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public byte[] Pcm16 { get; }
    public int SampleRate { get; }
    public int Channels { get; }

    /// <summary>Samples per channel.</summary>
    public int FrameCount => Pcm16.Length / (2 * Channels);
    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / SampleRate);
    public WaveFormat WaveFormat => new(SampleRate, 16, Channels);

    /// <summary>
    /// Min/max envelope for drawing a waveform: <paramref name="columns"/> pairs of (min, max) in -1..1,
    /// taken across all channels.
    /// </summary>
    public float[] ComputePeaks(int columns)
    {
        var peaks = new float[columns * 2];
        var frames = FrameCount;
        if (frames == 0 || columns <= 0)
            return peaks;

        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(Pcm16.AsSpan());
        for (var c = 0; c < columns; c++)
        {
            var start = (int)((long)frames * c / columns) * Channels;
            var end = (int)((long)frames * (c + 1) / columns) * Channels;
            if (end <= start)
                end = Math.Min(start + Channels, samples.Length);
            short min = 0, max = 0;
            for (var i = start; i < end; i++)
            {
                var s = samples[i];
                if (s < min) min = s;
                if (s > max) max = s;
            }
            peaks[c * 2] = min / 32768f;
            peaks[c * 2 + 1] = max / 32767f;
        }
        return peaks;
    }

    public void SaveWav(string path)
    {
        using var writer = new WaveFileWriter(path, WaveFormat);
        writer.Write(Pcm16, 0, Pcm16.Length);
    }
}
