using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace SoundStudio.App.Services;

/// <summary>
/// Checks for new versions and installs them (Velopack). The update location is a setting:
/// a GitHub repository ("https://github.com/you/sound-studio", using its Releases) or any web folder
/// that the release script's output was uploaded to ("https://example.com/soundstudio").
/// Does nothing when running from a dev build (not installed) or when no location is set.
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateManager? _manager;
    private UpdateInfo? _ready;

    public UpdateService(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return;
        try
        {
            IUpdateSource feed = source.Contains("github.com", StringComparison.OrdinalIgnoreCase)
                ? new GithubSource(source.TrimEnd('/'), accessToken: null, prerelease: false)
                : new SimpleWebSource(source.TrimEnd('/'));
            var manager = new UpdateManager(feed);
            if (manager.IsInstalled)
                _manager = manager;
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or InvalidOperationException)
        {
            _manager = null;
        }
    }

    /// <summary>Update location baked in at build time by the release script.</summary>
    public static string? BuiltInSource =>
        Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateSource")?.Value is { Length: > 0 } s ? s : null;

    /// <summary>Version of the running app, e.g. "1.0.0".</summary>
    public static string CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "dev";

    public bool IsEnabled => _manager != null;
    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    /// <summary>Checks and downloads in the background. Returns the new version when one is ready to install.</summary>
    public async Task<string?> CheckAndDownloadAsync()
    {
        if (_manager == null)
            return null;
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update == null)
                return null;
            await _manager.DownloadUpdatesAsync(update);
            _ready = update;
            return ReadyVersion;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // offline, feed missing, etc.: try again next launch
        }
    }

    /// <summary>Closes the app, installs the downloaded version and starts it again.</summary>
    public void RestartToUpdate()
    {
        if (_manager != null && _ready != null)
            _manager.ApplyUpdatesAndRestart(_ready);
    }
}
