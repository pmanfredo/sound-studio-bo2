using System.IO;
namespace SoundStudio.App.Services;

public enum NoticeKind { Success, Info, Warning, Error }

/// <summary>What the view model needs from the window: confirmations, quiet notifications, folder picking.</summary>
public interface IUserDialogs
{
    /// <summary>Asks a yes/no question. Returns true if the user chose <paramref name="confirmText"/>.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    /// <summary>Shows a non-blocking notification that disappears by itself.</summary>
    void Notify(string title, string message, NoticeKind kind = NoticeKind.Success);

    string? PickFolder(string title, string? initialFolder);

    /// <summary>Asks where to save a file. <paramref name="filter"/> uses the "Label|*.ext" format.</summary>
    string? PickSaveFile(string title, string fileName, string filter);

    string? PickFile(string title, string filter);

    /// <summary>Opens the recorder (modal). It stages the take itself via the view model's callback.</summary>
    void ShowRecorder(ViewModels.RecorderViewModel recorder);

    /// <summary>Opens (or focuses) the Share window.</summary>
    void ShowSync(ViewModels.SyncViewModel sync);

    /// <summary>Opens the layer editor (modal). It stages the mix itself via the view model's callback.</summary>
    void ShowLayers(ViewModels.LayersViewModel layers, ViewModels.LayerSession? session);

    /// <summary>Shows matched files for bulk replace. Null = cancelled.</summary>
    Task<ViewModels.ReviewResult?> ReviewReplacementsAsync(IReadOnlyList<ViewModels.MatchItemViewModel> items, string scopeName);
}

/// <summary>Used before the window exists and in screenshot mode: never blocks.</summary>
public sealed class SilentDialogs : IUserDialogs
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(false);
    public void Notify(string title, string message, NoticeKind kind = NoticeKind.Success) { }
    public string? PickFolder(string title, string? initialFolder) => null;
    public string? PickSaveFile(string title, string fileName, string filter) => null;
    public string? PickFile(string title, string filter) => null;
    public void ShowRecorder(ViewModels.RecorderViewModel recorder) { }
    public void ShowSync(ViewModels.SyncViewModel sync) { }
    public void ShowLayers(ViewModels.LayersViewModel layers, ViewModels.LayerSession? session) { }
    public Task<ViewModels.ReviewResult?> ReviewReplacementsAsync(IReadOnlyList<ViewModels.MatchItemViewModel> items, string scopeName) =>
        Task.FromResult<ViewModels.ReviewResult?>(null);
}

/// <summary>Screenshot/automation mode: confirms everything, returns a preset folder, logs notifications to a file.</summary>
public sealed class ScriptedDialogs(string? folder, string logPath) : IUserDialogs
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        File.AppendAllText(logPath, $"CONFIRM {title} [{confirmText}]{Environment.NewLine}");
        return Task.FromResult(true);
    }

    public void Notify(string title, string message, NoticeKind kind = NoticeKind.Success) =>
        File.AppendAllText(logPath, $"{kind.ToString().ToUpperInvariant()} {title}: {message}{Environment.NewLine}");

    public string? PickFolder(string title, string? initialFolder) => folder;
    public string? PickSaveFile(string title, string fileName, string filter) =>
        folder == null ? null : Path.Combine(folder, fileName);

    public void ShowRecorder(ViewModels.RecorderViewModel recorder) =>
        File.AppendAllText(logPath, $"RECORDER for {recorder.Target.Name}{Environment.NewLine}");
    public void ShowSync(ViewModels.SyncViewModel sync) { }
    public void ShowLayers(ViewModels.LayersViewModel layers, ViewModels.LayerSession? session) =>
        File.AppendAllText(logPath, $"LAYERS for {layers.Target.Name}{Environment.NewLine}");

    /// <summary>"--pick" may name a file directly.</summary>
    public string? PickFile(string title, string filter) => folder != null && File.Exists(folder) ? folder : null;

    public Task<ViewModels.ReviewResult?> ReviewReplacementsAsync(IReadOnlyList<ViewModels.MatchItemViewModel> items, string scopeName)
    {
        File.AppendAllText(logPath, $"REVIEW scope={scopeName} matched={items.Count(i => i.IsMatched)}/{items.Count}{Environment.NewLine}");
        return Task.FromResult<ViewModels.ReviewResult?>(new ViewModels.ReviewResult(TrimLong: false));
    }
}
