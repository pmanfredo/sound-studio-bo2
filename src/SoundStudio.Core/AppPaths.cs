using System.Text.Json;

namespace SoundStudio.Core;

/// <summary>Where the app keeps its own data: %LocalAppData%\SoundStudio.</summary>
public static class AppPaths
{
    /// <summary>App data folder. Override with SOUNDSTUDIO_DATA (used for sandboxes and tests).</summary>
    public static string DataFolder { get; } =
        Environment.GetEnvironmentVariable("SOUNDSTUDIO_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundStudio");

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");
    public static string ScanCacheFile => Path.Combine(DataFolder, "scan-cache.json");
    public static string OriginalsFolder => Path.Combine(DataFolder, "originals");
    public static string HistoryFile => Path.Combine(DataFolder, "history.jsonl");
    public static string RecordingsFolder => Path.Combine(DataFolder, "recordings");
    public static string SyncFolder => Path.Combine(DataFolder, "sync");

    /// <summary>Folders where a BO2 <c>sound</c> folder is commonly found, most likely first.</summary>
    public static IEnumerable<string> CandidateSoundFolders()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(docs, @"Plutonium\Black_Ops_II\pluto_t6_full_game\sound");
        yield return Path.Combine(local, @"Plutonium\storage\t6\sound");
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            yield return Path.Combine(drive.Name, @"Program Files (x86)\Steam\steamapps\common\Call of Duty Black Ops II\sound");
            yield return Path.Combine(drive.Name, @"SteamLibrary\steamapps\common\Call of Duty Black Ops II\sound");
        }
    }
}

public sealed class AppSettings
{
    public string? SoundFolder { get; set; }
    public bool DarkTheme { get; set; } = true;

    /// <summary>Name shown in the change history (and later, to the people you sync with).</summary>
    public string DisplayName { get; set; } = Environment.UserName;

    /// <summary>Play a sound as soon as it's selected.</summary>
    public bool AutoPlay { get; set; } = true;

    /// <summary>Player volume, 0..1.</summary>
    public float Volume { get; set; } = 0.8f;

    /// <summary>Last microphone used for recording (device id).</summary>
    public string? MicrophoneId { get; set; }

    /// <summary>Count 3-2-1 before recording starts.</summary>
    public bool RecordCountdown { get; set; } = true;

    /// <summary>Make recordings loud enough to sit next to the game's audio.</summary>
    public bool NormalizeRecordings { get; set; } = true;

    // ---- sync ----

    /// <summary>"Off", "Host" or "Join": restored at startup.</summary>
    public string SyncRole { get; set; } = "Off";

    /// <summary>Identifies this install to others (stable, random).</summary>
    public string ClientId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Host mode: stable id and secret, so existing invite codes keep working across restarts.</summary>
    public string? HostId { get; set; }
    public string? HostToken { get; set; }
    public int HostPort { get; set; } = 5150;
    public bool ShareOverInternet { get; set; }

    /// <summary>
    /// Optional stable internet address that forwards to this PC's sync port, e.g. a named Cloudflare tunnel
    /// route or a reverse proxy ("https://sounds.example.com"). Added to invite codes when hosting.
    /// </summary>
    public string? PublicAddress { get; set; }
    public string? CloudflaredPath { get; set; }

    /// <summary>Join mode: the host we joined (from its invite code).</summary>
    public string? JoinHostId { get; set; }
    public string? JoinHostName { get; set; }
    public string? JoinToken { get; set; }
    public List<string> JoinUrls { get; set; } = [];

    /// <summary>Overrides the update location built into the app (normally left empty).</summary>
    public string? UpdateSource { get; set; }

    /// <summary>Write incoming sounds straight to the game instead of staging them for review.</summary>
    public bool AutoApplyIncoming { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new AppSettings();
    }

    private static readonly object SaveLock = new();

    /// <summary>Writes to a temp file and swaps it in, so a crash mid-save can't corrupt settings.</summary>
    public void Save()
    {
        lock (SaveLock)
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
    }
}
