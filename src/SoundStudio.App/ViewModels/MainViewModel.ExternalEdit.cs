using System.IO;
using CommunityToolkit.Mvvm.Input;
using SoundStudio.App.Services;
using SoundStudio.Audio;
using SoundStudio.Core;
using SoundStudio.Core.Bo2;

namespace SoundStudio.App.ViewModels;

/// <summary>"Edit in Audacity": hand a sound to an external editor and stage whatever it exports.</summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<SoundRowViewModel, ExternalEditSession> _externalEdits = [];

    /// <summary>"Edit in Audacity…", or the name of the editor the user chose.</summary>
    public string EditExternallyLabel => $"Edit in {ExternalEditor.DisplayName(ExternalEditor.Resolve(_settings))}…";

    /// <summary>
    /// Writes what the selected sound sounds like now (the staged version if there is one) to a WAV, opens it in
    /// the editor, and stages each export of it.
    /// </summary>
    [RelayCommand]
    private async Task EditExternallyAsync()
    {
        if (SelectedRow is not { CanReplace: true, IsMasked: false } row)
            return;
        if (_externalEdits.TryGetValue(row, out var open))
        {
            open.OpenEditor();
            return;
        }
        var editor = ExternalEditor.Choose(_settings, Dialogs);
        if (editor == null)
            return;
        OnPropertyChanged(nameof(EditExternallyLabel));
        Player.StopCommand.Execute(null);

        string path;
        try
        {
            var info = row.Info;
            var audio = (row.Pending is { IsHidden: false } pending ? pending.Prepared.Audio : null)
                        ?? await Task.Run(() => AudioDecoder.Decode(SoundReader.Read(info.Bank.FilePath, info.Index, info.Id)));
            var folder = Path.Combine(AppPaths.EditsFolder, $"{Path.GetFileNameWithoutExtension(info.Bank.FileName)}-{info.Index}");
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, SafeFileName(row.Name) + ".wav");
            audio.SaveWav(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            Dialogs.Notify("Couldn't prepare the sound", ex.Message, NoticeKind.Error);
            return;
        }

        var session = new ExternalEditSession(editor, path, async edited => await StageExternalEditAsync(row, edited));
        try
        {
            session.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            session.Dispose();
            Dialogs.Notify($"Couldn't open {session.EditorName}", ex.Message, NoticeKind.Error);
            return;
        }
        _externalEdits[row] = session;
        row.EditingIn = session.EditorName;
    }

    private async Task StageExternalEditAsync(SoundRowViewModel row, string path)
    {
        if (!_externalEdits.ContainsKey(row))
            return;
        await StageAsync([(row, path)], options: null);
    }

    /// <summary>The export went somewhere we don't watch: let the user point at it.</summary>
    [RelayCommand]
    private async Task PickExternalEditAsync()
    {
        if (SelectedRow is not { } row)
            return;
        var file = Dialogs.PickFile($"Choose the file you exported for {row.Name}", ReplacementEncoder.FileFilter);
        if (file != null)
            await StageAsync([(row, file)], options: null);
    }

    [RelayCommand]
    private void StopExternalEdit()
    {
        if (SelectedRow is { } row)
            StopExternalEdit(row);
    }

    private void StopExternalEdit(SoundRowViewModel row)
    {
        if (_externalEdits.Remove(row, out var session))
            session.Dispose();
        row.EditingIn = null;
    }

    /// <summary>Lets the user switch to a different editor than Audacity.</summary>
    [RelayCommand]
    private void ChooseExternalEditor()
    {
        if (ExternalEditor.Choose(_settings, Dialogs, choose: true) != null)
            OnPropertyChanged(nameof(EditExternallyLabel));
    }

    public void StopAllExternalEdits()
    {
        foreach (var row in _externalEdits.Keys.ToList())
            StopExternalEdit(row);
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 ? "sound" : clean;
    }
}
