using System.Numerics;

namespace SoundSpotter;

/// <summary>
/// Minimal landmark ("constellation") audio fingerprinting, Shazam-style: spectrogram peaks are paired into
/// (f1, f2, dt) hashes; a query matches the sound whose hashes line up at one consistent time offset.
/// This is a research prototype to measure accuracy, index size and speed, not a tuned matcher.
/// </summary>
public sealed class Fingerprinter
{
    public const int SampleRate = 11025;
    public const int FrameSize = 1024;
    public const int Hop = 256;                 // ~23 ms per frame
    private const int Bins = FrameSize / 2;
    private const int FanOut = 5;               // pairs per anchor peak
    private const int MaxDt = 63;               // target zone: up to ~1.5 s ahead
    private static readonly int[] BandEdges = [2, 10, 20, 40, 80, 160, 511];
    private static readonly float[] Window = Enumerable.Range(0, FrameSize)
        .Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameSize - 1)))).ToArray();

    public readonly record struct Peak(int Frame, int Bin);
    public readonly record struct Landmark(uint Hash, int Frame);

    /// <summary>Downmixes 16-bit PCM to mono and resamples to <see cref="SampleRate"/> (box-filtered linear).</summary>
    public static float[] ToMono11k(byte[] pcm16, int sampleRate, int channels)
    {
        var frames = pcm16.Length / 2 / channels;
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
                sum += BitConverter.ToInt16(pcm16, (i * channels + c) * 2);
            mono[i] = sum / channels / 32768f;
        }
        return Resample(mono, sampleRate, SampleRate);
    }

    public static float[] Resample(float[] input, double fromRate, double toRate)
    {
        if (Math.Abs(fromRate - toRate) < 1e-6) return input;
        var ratio = fromRate / toRate;
        var radius = Math.Max(0, (int)Math.Floor(ratio / 2)); // crude anti-alias: average over the source span
        var output = new float[(int)(input.Length / ratio)];
        for (var i = 0; i < output.Length; i++)
        {
            var center = (int)(i * ratio);
            float sum = 0; var n = 0;
            for (var j = Math.Max(0, center - radius); j <= Math.Min(input.Length - 1, center + radius); j++) { sum += input[j]; n++; }
            output[i] = n == 0 ? 0 : sum / n;
        }
        return output;
    }

    /// <summary>Spectrogram peaks: per frame, the loudest bin in each band if it stands out locally.</summary>
    public static List<Peak> Peaks(float[] mono)
    {
        var frameCount = mono.Length < FrameSize ? 0 : 1 + (mono.Length - FrameSize) / Hop;
        var mags = new float[frameCount][];
        var buffer = new Complex[FrameSize];
        for (var f = 0; f < frameCount; f++)
        {
            for (var i = 0; i < FrameSize; i++) buffer[i] = new Complex(mono[f * Hop + i] * Window[i], 0);
            Fft(buffer);
            var m = new float[Bins];
            for (var b = 0; b < Bins; b++) m[b] = (float)Math.Log(1e-6 + buffer[b].Magnitude);
            mags[f] = m;
        }

        var peaks = new List<Peak>();
        for (var f = 0; f < frameCount; f++)
        {
            var frameMean = mags[f].Average();
            for (var band = 0; band < BandEdges.Length - 1; band++)
            {
                var best = BandEdges[band];
                for (var b = BandEdges[band]; b < BandEdges[band + 1]; b++)
                    if (mags[f][b] > mags[f][best]) best = b;
                var v = mags[f][best];
                if (v < frameMean + 1.0f || v < -4f) continue; // must stand out from the frame and from silence
                var isMax = true;
                for (var df = -3; df <= 3 && isMax; df++)
                {
                    var g = f + df;
                    if (df == 0 || g < 0 || g >= frameCount) continue;
                    for (var db = -2; db <= 2; db++)
                    {
                        var c = best + db;
                        if (c >= 0 && c < Bins && mags[g][c] > v) { isMax = false; break; }
                    }
                }
                if (isMax) peaks.Add(new Peak(f, best));
            }
        }
        return peaks;
    }

    public static List<Landmark> Landmarks(float[] mono) => Landmarks(Peaks(mono));

    public static List<Landmark> Landmarks(List<Peak> peaks)
    {
        var result = new List<Landmark>(peaks.Count * FanOut);
        for (var i = 0; i < peaks.Count; i++)
        {
            var paired = 0;
            for (var j = i + 1; j < peaks.Count && paired < FanOut; j++)
            {
                var dt = peaks[j].Frame - peaks[i].Frame;
                if (dt == 0) continue;
                if (dt > MaxDt) break;
                result.Add(new Landmark(((uint)peaks[i].Bin << 15) | ((uint)peaks[j].Bin << 6) | (uint)dt, peaks[i].Frame));
                paired++;
            }
        }
        return result;
    }

    private static void Fft(Complex[] a)
    {
        var n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (var k = 0; k < len / 2; k++)
                {
                    var u = a[i + k];
                    var v = a[i + k + len / 2] * w;
                    a[i + k] = u + v;
                    a[i + k + len / 2] = u - v;
                    w *= wl;
                }
            }
        }
    }
}

/// <summary>Hash → (sound, frame) postings, with offset-histogram voting at query time.</summary>
public sealed class FingerprintIndex
{
    private readonly Dictionary<uint, List<ulong>> _postings = new();

    public long LandmarkCount { get; private set; }
    public int SoundCount { get; private set; }

    /// <summary>Approximate bytes for a compact on-disk form: 4-byte hash + 4-byte sound + 2-byte frame per landmark.</summary>
    public long CompactBytes => LandmarkCount * 10;

    public void Add(int soundId, IEnumerable<Fingerprinter.Landmark> landmarks)
    {
        SoundCount++;
        foreach (var lm in landmarks)
        {
            if (!_postings.TryGetValue(lm.Hash, out var list)) _postings[lm.Hash] = list = new List<ulong>(2);
            list.Add(((ulong)(uint)soundId << 32) | (uint)lm.Frame);
            LandmarkCount++;
        }
    }

    /// <summary>Best candidates by number of hashes agreeing on one time offset.</summary>
    public List<(int SoundId, int Score)> Query(List<Fingerprinter.Landmark> query, int top = 3)
    {
        var votes = new Dictionary<(int, int), int>();
        foreach (var lm in query)
        {
            if (!_postings.TryGetValue(lm.Hash, out var list) || list.Count > 2000) continue; // skip hashes too common to inform
            foreach (var p in list)
            {
                var key = ((int)(p >> 32), (int)(uint)p - lm.Frame);
                votes[key] = votes.GetValueOrDefault(key) + 1;
            }
        }
        return votes.GroupBy(v => v.Key.Item1)
            .Select(g => (SoundId: g.Key, Score: g.Max(x => x.Value)))
            .OrderByDescending(x => x.Score).Take(top).ToList();
    }
}
