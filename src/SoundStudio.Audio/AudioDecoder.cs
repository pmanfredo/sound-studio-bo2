using NAudio.MediaFoundation;
using NAudio.Wave;
using SoundStudio.Core.Bo2;

namespace SoundStudio.Audio;

/// <summary>
/// Turns game audio (headerless PCM, FLAC, MP3) and ordinary audio files into <see cref="DecodedAudio"/>.
/// FLAC/MP3 use Windows' built-in Media Foundation codecs, so no extra DLLs are needed.
/// </summary>
public static class AudioDecoder
{
    private static readonly Lazy<bool> MediaFoundationReady = new(() =>
    {
        MediaFoundationApi.Startup();
        return true;
    });

    public static DecodedAudio Decode(SoundData sound) =>
        Decode(sound.Data, sound.Format, sound.Channels, sound.SampleRate);

    public static DecodedAudio Decode(byte[] data, AudioFormat format, int channels, int sampleRate)
    {
        switch (format)
        {
            case AudioFormat.Pcm16:
                // Headerless 16-bit PCM: trim a stray odd byte so frames stay aligned.
                var usable = data.Length - data.Length % (2 * Math.Max(1, channels));
                return new DecodedAudio(usable == data.Length ? data : data[..usable], sampleRate, channels);

            case AudioFormat.Flac:
                // Built-in decoder: Windows' FLAC codec stops early on many re-encoded (modded) sounds.
                var flac = SoundStudio.Core.Audio.FlacDecoder.Decode(data);
                var pcm = new byte[flac.Samples.Length * 2];
                Buffer.BlockCopy(flac.Samples, 0, pcm, 0, pcm.Length);
                return new DecodedAudio(pcm, flac.SampleRate, flac.Channels);

            case AudioFormat.Mp3:
                _ = MediaFoundationReady.Value;
                using (var reader = new StreamMediaFoundationReader(new MemoryStream(data)))
                    return ReadAll(reader);

            default:
                throw new NotSupportedException($"{format.DisplayName()} audio can't be played.");
        }
    }

    /// <summary>Decodes any audio file Windows understands (wav, mp3, flac, m4a, wma, ...).</summary>
    public static DecodedAudio DecodeFile(string path)
    {
        _ = MediaFoundationReady.Value;
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            using var wav = new AudioFileReader(path);
            return ReadAll(wav);
        }
        using var reader = new MediaFoundationReader(path);
        return ReadAll(reader);
    }

    /// <summary>Reads a whole stream as float samples and converts to 16-bit PCM.</summary>
    private static DecodedAudio ReadAll(WaveStream stream)
    {
        var provider = stream.ToSampleProvider();
        var format = provider.WaveFormat;
        var output = new MemoryStream(stream.Length > 0 ? (int)Math.Min(int.MaxValue, stream.Length) : 1 << 20);
        var buffer = new float[format.SampleRate * format.Channels]; // ~1 second
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
        return new DecodedAudio(output.ToArray(), format.SampleRate, format.Channels);
    }
}
