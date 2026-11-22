using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using SoundStudio.Core;

namespace SoundStudio.App.Services;

/// <summary>
/// Finding the user's audio editor (Audacity unless they chose another) and handing sounds to it.
/// </summary>
public static class ExternalEditor
{
    /// <summary>The editor to use: the one chosen in settings, else Audacity if it's installed.</summary>
    public static string? Resolve(AppSettings settings) =>
        settings.ExternalEditorPath is { Length: > 0 } chosen && File.Exists(chosen) ? chosen : FindAudacity();

    /// <summary>"Audacity", or the chosen program's own name.</summary>
    public static string DisplayName(string? editorPath)
    {
        if (editorPath == null)
            return "Audacity";
        var stem = Path.GetFileNameWithoutExtension(editorPath);
        if (stem.Equals("audacity", StringComparison.OrdinalIgnoreCase))
            return "Audacity";
        try
        {
            var description = FileVersionInfo.GetVersionInfo(editorPath).FileDescription?.Trim();
            if (description is { Length: > 0 and <= 30 })
                return description;
        }
        catch (IOException) { }
        return stem;
    }

    public static string? FindAudacity()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\audacity.exe");
                if (key?.GetValue(null) is string path && File.Exists(path.Trim('"')))
                    return path.Trim('"');
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var path = Path.Combine(Environment.GetFolderPath(folder), "Audacity", "Audacity.exe");
            if (File.Exists(path))
                return path;
        }
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Audacity", "Audacity.exe");
        return File.Exists(local) ? local : null;
    }

    /// <summary>
    /// The editor to use, asking the user to pick one when Audacity isn't installed and none was chosen
    /// (or always, with <paramref name="choose"/>). Null if they cancel.
    /// </summary>
    public static string? Choose(AppSettings settings, IUserDialogs dialogs, bool choose = false)
    {
        if (!choose && Resolve(settings) is { } found)
            return found;
        var picked = dialogs.PickFile(choose
                ? "Choose the program to edit sounds with"
                : "Audacity isn't installed (get it free at audacityteam.org). Or choose another audio editor:",
            "Programs|*.exe");
        if (picked == null)
            return null;
        settings.ExternalEditorPath = picked;
        try { settings.Save(); } catch (IOException) { }
        return picked;
    }

    /// <summary>Folders where an export of a file opened from <paramref name="editFolder"/> is likely to land.</summary>
    public static IEnumerable<string> LikelyExportFolders(string editFolder)
    {
        var folders = new List<string> { editFolder };
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Audacity"));
        // Audacity remembers where it last exported; its settings file lists those folders.
        var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "audacity", "audacity.cfg");
        try
        {
            if (File.Exists(cfg))
            {
                var section = "";
                foreach (var raw in File.ReadLines(cfg))
                {
                    var line = raw.Trim();
                    if (line.StartsWith('['))
                    {
                        section = line;
                        continue;
                    }
                    var eq = line.IndexOf('=');
                    if (eq <= 0 || (section + line[..eq]).IndexOf("Export", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var value = line[(eq + 1)..].Replace(@"\\", @"\").Trim();
                    if (value.Length > 2 && Directory.Exists(value))
                        folders.Add(value);
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return folders.Where(Directory.Exists).Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// One sound open in the external editor: watches for it being exported (saved over, or exported under the same
/// name to a usual folder) and reports each new version.
/// </summary>
public sealed class ExternalEditSession : IDisposable
{
    private static readonly string[] AudioExtensions = [".wav", ".mp3", ".flac", ".ogg", ".m4a", ".opus"];

    private readonly string _stem;
    private readonly Action<string> _onEdited;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly DispatcherTimer _settle;
    private readonly Dictionary<string, DateTime> _handled = new(StringComparer.OrdinalIgnoreCase);
    private string? _candidate;
    private int _attempts;

    /// <param name="filePath">The audio handed to the editor.</param>
    /// <param name="onEdited">Called on the UI thread with the path of each new version.</param>
    public ExternalEditSession(string editorPath, string filePath, Action<string> onEdited)
    {
        EditorPath = editorPath;
        FilePath = filePath;
        EditorName = ExternalEditor.DisplayName(editorPath);
        _stem = Path.GetFileNameWithoutExtension(filePath);
        _onEdited = onEdited;
        _handled[filePath] = File.GetLastWriteTimeUtc(filePath);
        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _settle.Tick += (_, _) => TryPickUp();
    }

    public string EditorPath { get; }
    public string FilePath { get; }
    public string EditorName { get; }

    /// <summary>Opens the file in the editor and starts watching for exports.</summary>
    public void Start()
    {
        foreach (var folder in ExternalEditor.LikelyExportFolders(Path.GetDirectoryName(FilePath)!))
        {
            try
            {
                var w = new FileSystemWatcher(folder, _stem + ".*")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                w.Changed += OnFileEvent;
                w.Created += OnFileEvent;
                w.Renamed += (_, e) => OnFileEvent(null, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(e.FullPath)!, e.Name));
                _watchers.Add(w);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        }
        OpenEditor();
    }

    /// <summary>Brings the file up in the editor again (it may have been closed).</summary>
    public void OpenEditor() =>
        Process.Start(new ProcessStartInfo(EditorPath) { ArgumentList = { FilePath }, UseShellExecute = false });

    private void OnFileEvent(object? sender, FileSystemEventArgs e)
    {
        if (!AudioExtensions.Contains(Path.GetExtension(e.FullPath), StringComparer.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileNameWithoutExtension(e.FullPath), _stem, StringComparison.OrdinalIgnoreCase))
            return;
        _settle.Dispatcher.BeginInvoke(() =>
        {
            _candidate = e.FullPath;
            _attempts = 0;
            _settle.Stop();
            _settle.Start(); // wait until the editor has finished writing
        });
    }

    private void TryPickUp()
    {
        if (_candidate is not { } path)
        {
            _settle.Stop();
            return;
        }
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_handled.TryGetValue(path, out var seen) && seen == stamp)
            {
                _settle.Stop();
                return;
            }
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { } // still being written?
            if (new FileInfo(path).Length < 64)
                throw new IOException("not written yet");
            _settle.Stop();
            _handled[path] = stamp;
            _candidate = null;
            _onEdited(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (++_attempts > 30) // ~18 s: give up on this one
                _settle.Stop();
        }
    }

    public void Dispose()
    {
        _settle.Stop();
        foreach (var w in _watchers)
            w.Dispose();
        _watchers.Clear();
    }
}
