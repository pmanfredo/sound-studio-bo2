using System.Runtime.InteropServices;

namespace SoundStudio.Audio;

/// <summary>Small, non-destructive edits used by the recorder: trim, find the voice, normalize.</summary>
public static class AudioEdit
{
    /// <summary>Copies frames [start, end) with 10 ms fades at both ends so cuts don't click.</summary>
    public static DecodedAudio Trim(DecodedAudio audio, int startFrame, int endFrame)
    {
        startFrame = Math.Clamp(startFrame, 0, audio.FrameCount);
        endFrame = Math.Clamp(endFrame, startFrame, audio.FrameCount);
        var bytesPerFrame = audio.Channels * 2;
        var pcm = audio.Pcm16[(startFrame * bytesPerFrame)..(endFrame * bytesPerFrame)];
        var result = new DecodedAudio(pcm, audio.SampleRate, audio.Channels);
        ApplyEdgeFades(result, audio.SampleRate / 100);
        return result;
    }

    /// <summary>
    /// Finds where the sound actually is: the first and last frames louder than the threshold
    /// (−40 dBFS, or 35 dB under the peak for loud takes), padded so breaths and tails aren't cut.
    /// Returns the whole clip if it's all quiet.
    /// </summary>
    public static (int Start, int End) FindSoundBounds(DecodedAudio audio, double padBeforeSeconds = 0.08, double padAfterSeconds = 0.15)
    {
        var samples = new short[audio.Pcm16.Length / 2];
        Buffer.BlockCopy(audio.Pcm16, 0, samples, 0, samples.Length * 2);
        var frames = audio.FrameCount;
        if (frames == 0)
            return (0, 0);

        var peak = 0;
        foreach (var s in samples)
            peak = Math.Max(peak, Math.Abs((int)s));
        var threshold = Math.Max(328, (int)(peak * 0.0178)); // max(-40 dBFS, peak - 35 dB)

        int FrameLevel(int f)
        {
            var level = 0;
            for (var c = 0; c < audio.Channels; c++)
                level = Math.Max(level, Math.Abs((int)samples[f * audio.Channels + c]));
            return level;
        }

        var first = 0;
        while (first < frames && FrameLevel(first) < threshold)
            first++;
        if (first == frames)
            return (0, frames);
        var last = frames - 1;
        while (last > first && FrameLevel(last) < threshold)
            last--;

        var start = Math.Max(0, first - (int)(padBeforeSeconds * audio.SampleRate));
        var end = Math.Min(frames, last + 1 + (int)(padAfterSeconds * audio.SampleRate));
        return (start, end);
    }

    /// <summary>Scales so the loudest sample sits at <paramref name="peakDb"/> dBFS (default −1 dB).</summary>
    public static DecodedAudio Normalize(DecodedAudio audio, double peakDb = -1.0)
    {
        var samples = MemoryMarshal.Cast<byte, short>(audio.Pcm16.AsSpan());
        var peak = 0;
        foreach (var s in samples)
            peak = Math.Max(peak, Math.Abs((int)s));
        if (peak < 16)
            return audio; // silence: nothing to scale

        var gain = 32767 * Math.Pow(10, peakDb / 20) / peak;
        var output = new byte[audio.Pcm16.Length];
        var outSamples = MemoryMarshal.Cast<byte, short>(output.AsSpan());
        for (var i = 0; i < samples.Length; i++)
            outSamples[i] = (short)Math.Clamp(Math.Round(samples[i] * gain), short.MinValue, short.MaxValue);
        return new DecodedAudio(output, audio.SampleRate, audio.Channels);
    }

    /// <summary>Peak level of the clip in dBFS (−∞ for silence).</summary>
    public static double PeakDb(DecodedAudio audio)
    {
        var peak = 0;
        foreach (var s in MemoryMarshal.Cast<byte, short>(audio.Pcm16.AsSpan()))
            peak = Math.Max(peak, Math.Abs((int)s));
        return peak == 0 ? double.NegativeInfinity : 20 * Math.Log10(peak / 32768.0);
    }

    private static void ApplyEdgeFades(DecodedAudio audio, int fadeFrames)
    {
        var samples = MemoryMarshal.Cast<byte, short>(audio.Pcm16.AsSpan());
        var frames = audio.FrameCount;
        fadeFrames = Math.Min(fadeFrames, frames / 2);
        for (var f = 0; f < fadeFrames; f++)
        {
            var gain = (double)f / fadeFrames;
            for (var c = 0; c < audio.Channels; c++)
            {
                samples[f * audio.Channels + c] = (short)(samples[f * audio.Channels + c] * gain);
                var tail = (frames - 1 - f) * audio.Channels + c;
                samples[tail] = (short)(samples[tail] * gain);
            }
        }
    }
}
