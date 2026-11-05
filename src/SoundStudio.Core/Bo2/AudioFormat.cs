namespace SoundStudio.Core.Bo2;

/// <summary>Audio encodings found in Black Ops II (T6) sound banks.</summary>
public enum AudioFormat : byte
{
    /// <summary>Signed 16-bit little-endian PCM with no RIFF header.</summary>
    Pcm16 = 0x0,
    /// <summary>Xbox 360 XMA. Playable after adding a header; cannot be re-encoded.</summary>
    Xma4 = 0x4,
    Mp3 = 0x5,
    /// <summary>Complete FLAC stream (PC banks).</summary>
    Flac = 0x8,
}

public static class AudioFormatExtensions
{
    public static string DisplayName(this AudioFormat format) => format switch
    {
        AudioFormat.Pcm16 => "PCM",
        AudioFormat.Xma4 => "XMA",
        AudioFormat.Mp3 => "MP3",
        AudioFormat.Flac => "FLAC",
        _ => $"Unknown (0x{(byte)format:X2})",
    };

    public static string FileExtension(this AudioFormat format) => format switch
    {
        AudioFormat.Pcm16 => ".wav",
        AudioFormat.Xma4 => ".xma",
        AudioFormat.Mp3 => ".mp3",
        AudioFormat.Flac => ".flac",
        _ => ".bin",
    };

    /// <summary>Whether a replacement can be encoded into this format.</summary>
    public static bool CanReplace(this AudioFormat format) =>
        format is AudioFormat.Pcm16 or AudioFormat.Mp3 or AudioFormat.Flac;
}
