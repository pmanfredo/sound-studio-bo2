using System.Diagnostics;

namespace SoundStudio.Audio;

/// <summary>
/// Optional ffmpeg, used only for input formats Windows can't decode itself (ogg, opus, webm, video files...).
/// </summary>
public static class Ffmpeg
{
    private static readonly Lazy<string?> Located = new(Locate);

    /// <summary>Explicit path from settings; overrides auto-detection when it exists.</summary>
    public static string? ConfiguredPath { get; set; }

    public static string? Path =>
        ConfiguredPath is { Length: > 0 } p && File.Exists(p) ? p : Located.Value;

    public static bool IsAvailable => Path != null;

    private static string? Locate()
    {
        var candidates = new List<string>
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Downloads\v1.6.1-windows\ffmpeg\bin\ffmpeg.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(System.IO.Path.Combine(dir.Trim(), "ffmpeg.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Decodes any file ffmpeg understands to 16-bit PCM at the given rate/channels.</summary>
    public static DecodedAudio Decode(string file, int sampleRate, int channels, CancellationToken ct = default)
    {
        var exe = Path ?? throw new InvalidOperationException("ffmpeg is not available.");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-i", file, "-vn", "-ac", channels.ToString(),
                     "-ar", sampleRate.ToString(), "-f", "s16le", "-" })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start ffmpeg.");
        using var registration = ct.Register(() => { try { process.Kill(); } catch { } });
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0 || output.Length == 0)
            throw new InvalidDataException("ffmpeg couldn't read the file: " + errorTask.Result.Trim());
        return new DecodedAudio(output.ToArray(), sampleRate, channels);
    }
}
