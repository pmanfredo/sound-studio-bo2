using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundStudio.Core.Audio;
using SoundStudio.Core.Bo2;

namespace SoundStudio.Audio;

public sealed record ConvertOptions
{
    /// <summary>Cut the audio to this length (with a short fade-out). Null keeps the full length.</summary>
    public TimeSpan? TrimTo { get; init; }

    /// <summary>Remove silence at the very start (common in recordings).</summary>
    public bool TrimLeadingSilence { get; init; }
}

/// <summary>A sound converted to exactly what a bank entry needs, ready to stage.</summary>
public sealed record PreparedReplacement(
    string SourcePath,
    DecodedAudio Audio,     // resampled/remixed PCM, what the player previews
    byte[] EncodedData,     // what goes into the bank
    int SampleCount,
    AudioFormat Format,
    TimeSpan SourceDuration)
{
    public TimeSpan Duration => Audio.Duration;
}

/// <summary>Turns any audio file into game-ready data for a specific sound.</summary>
public static class ReplacementEncoder
{
    /// <summary>Extensions offered in file pickers. Others still work when ffmpeg is available.</summary>
    public static readonly string[] CommonExtensions = [".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aac", ".wma", ".opus", ".mp4", ".webm"];

    public static string FileFilter =>
        "Audio files|" + string.Join(";", CommonExtensions.Select(e => "*" + e)) + "|All files|*.*";

    public static bool LooksLikeAudio(string path) =>
        CommonExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ||
        Path.GetExtension(path).ToLowerInvariant() is ".aif" or ".aiff" or ".mkv" or ".avi" or ".mov" or ".wmv";

    public static PreparedReplacement Prepare(string sourcePath, AudioFormat targetFormat, int channels, int sampleRate,
        ConvertOptions? options = null, CancellationToken ct = default)
    {
        if (!targetFormat.CanReplace() || targetFormat == AudioFormat.Mp3)
            throw new NotSupportedException($"{targetFormat.DisplayName()} sounds can't be replaced.");

        var source = Load(sourcePath, ct);
        ct.ThrowIfCancellationRequested();
        return PrepareFrom(sourcePath, source, targetFormat, channels, sampleRate, options);
    }

    /// <summary>Same as <see cref="Prepare"/>, from audio already in memory (e.g. a recording).</summary>
    public static PreparedReplacement PrepareFrom(string label, DecodedAudio source, AudioFormat targetFormat, int channels,
        int sampleRate, ConvertOptions? options = null)
    {
        options ??= new ConvertOptions();
        var audio = Convert(source, sampleRate, channels);
        if (options.TrimLeadingSilence)
            audio = TrimSilenceStart(audio);
        if (options.TrimTo is { } limit && audio.Duration > limit)
            audio = TrimWithFade(audio, limit);
        if (audio.FrameCount == 0)
            throw new InvalidDataException("The file has no audio.");

        var samples = new short[audio.Pcm16.Length / 2];
        Buffer.BlockCopy(audio.Pcm16, 0, samples, 0, audio.Pcm16.Length);
        var encoded = targetFormat == AudioFormat.Flac
            ? FlacEncoder.Encode(samples, channels, sampleRate)
            : audio.Pcm16; // headerless PCM

        return new PreparedReplacement(label, audio, encoded, audio.FrameCount, targetFormat, source.Duration);
    }

    /// <summary>Decodes a file: built-in FLAC decoder, Windows codecs, then ffmpeg as a last resort.</summary>
    public static DecodedAudio Load(string path, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".flac")
        {
            var flac = FlacDecoder.Decode(File.ReadAllBytes(path));
            var pcm = new byte[flac.Samples.Length * 2];
            Buffer.BlockCopy(flac.Samples, 0, pcm, 0, pcm.Length);
            return new DecodedAudio(pcm, flac.SampleRate, flac.Channels);
        }

        Exception? windowsError = null;
        if (ext is ".wav" or ".mp3" or ".m4a" or ".aac" or ".wma" or ".mp4" or ".aif" or ".aiff")
        {
            try
            {
                var decoded = AudioDecoder.DecodeFile(path);
                if (decoded.FrameCount > 0)
                    return decoded;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                windowsError = ex;
            }
        }

        if (Ffmpeg.IsAvailable)
            return Ffmpeg.Decode(path, 48000, 2, ct);

        throw new InvalidDataException(windowsError != null
            ? $"Couldn't read {Path.GetFileName(path)}: {windowsError.Message}"
            : $"{ext} files need ffmpeg. Convert it to WAV or MP3 first, or set the ffmpeg location in settings.");
    }

    /// <summary>Resamples (WDL resampler) and remixes channels to the target format.</summary>
    public static DecodedAudio Convert(DecodedAudio source, int sampleRate, int channels)
    {
        if (source.SampleRate == sampleRate && source.Channels == channels)
            return source;

        ISampleProvider provider = new RawSourceWaveStream(new MemoryStream(source.Pcm16), source.WaveFormat).ToSampleProvider();
        if (provider.WaveFormat.Channels != channels)
            provider = Remix(provider, channels);
        if (provider.WaveFormat.SampleRate != sampleRate)
            provider = new WdlResamplingSampleProvider(provider, sampleRate);

        var output = new MemoryStream();
        var buffer = new float[sampleRate * channels];
        var bytes = new byte[buffer.Length * 2];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var v = (int)Math.Round(Math.Clamp(buffer[i], -1f, 1f) * 32767f);
                bytes[i * 2] = (byte)v;
                bytes[i * 2 + 1] = (byte)(v >> 8);
            }
            output.Write(bytes, 0, read * 2);
        }
        return new DecodedAudio(output.ToArray(), sampleRate, channels);
    }

    private static ISampleProvider Remix(ISampleProvider source, int channels)
    {
        var inCh = source.WaveFormat.Channels;
        if (inCh == 1 && channels == 2)
            return new MonoToStereoSampleProvider(source);
        if (channels == 1)
            return new DownmixToMono(source);
        // Other layouts: take the first channels, padding with the last available one.
        var map = new MultiplexingSampleProvider([source], channels);
        for (var ch = 0; ch < channels; ch++)
            map.ConnectInputToOutput(Math.Min(ch, inCh - 1), ch);
        return map;
    }

    private static DecodedAudio TrimWithFade(DecodedAudio audio, TimeSpan length)
    {
        var frames = (int)Math.Min(audio.FrameCount, Math.Floor(length.TotalSeconds * audio.SampleRate));
        var pcm = audio.Pcm16[..(frames * audio.Channels * 2)];
        var fadeFrames = Math.Min(frames, audio.SampleRate / 20); // 50 ms
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm.AsSpan());
        for (var f = 0; f < fadeFrames; f++)
        {
            var gain = (float)f / fadeFrames;
            var frame = frames - 1 - f;
            for (var c = 0; c < audio.Channels; c++)
                samples[frame * audio.Channels + c] = (short)(samples[frame * audio.Channels + c] * gain);
        }
        return new DecodedAudio(pcm, audio.SampleRate, audio.Channels);
    }

    private static DecodedAudio TrimSilenceStart(DecodedAudio audio)
    {
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(audio.Pcm16.AsSpan());
        const int threshold = 328; // about -40 dBFS
        var first = 0;
        while (first < samples.Length && Math.Abs((int)samples[first]) < threshold)
            first++;
        var frame = Math.Max(0, first / audio.Channels - audio.SampleRate / 100); // keep 10 ms before the sound
        return frame == 0 ? audio : new DecodedAudio(audio.Pcm16[(frame * audio.Channels * 2)..], audio.SampleRate, audio.Channels);
    }

    /// <summary>Averages all input channels into one.</summary>
    private sealed class DownmixToMono(ISampleProvider source) : ISampleProvider
    {
        private float[] _buffer = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var ch = source.WaveFormat.Channels;
            if (_buffer.Length < count * ch)
                _buffer = new float[count * ch];
            var read = source.Read(_buffer, 0, count * ch) / ch;
            for (var i = 0; i < read; i++)
            {
                float sum = 0;
                for (var c = 0; c < ch; c++)
                    sum += _buffer[i * ch + c];
                buffer[offset + i] = sum / ch;
            }
            return read;
        }
    }
}
