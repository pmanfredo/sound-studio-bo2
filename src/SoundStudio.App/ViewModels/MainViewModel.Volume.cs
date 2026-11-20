using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundStudio.Audio;
using SoundStudio.Core;
using SoundStudio.Core.Bo2;

namespace SoundStudio.App.ViewModels;

/// <summary>
/// Volume for any sound. Adjusting stages a change like any replacement (so it's applied with Apply, shared
/// with friends, and revertible): either the staged replacement gets louder/quieter, or, for a sound with
/// nothing staged, the current game audio is re-encoded at the new volume.
/// </summary>
public sealed partial class MainViewModel
{
    private DispatcherTimer? _volumeDebounce;
    private bool _loadingVolume;

    /// <summary>Volume change for the selected sound in dB (slider: −24…+12).</summary>
    [ObservableProperty] private double _volumeDb;

    /// <summary>E.g. "Limited to +2.1 dB so it doesn't distort".</summary>
    [ObservableProperty] private string? _volumeNote;

    [ObservableProperty] private bool _isAdjustingVolume;

    public string VolumeText => Math.Abs(VolumeDb) < 0.05 ? "Original volume" : $"{VolumeDb:+0.0;-0.0} dB";

    /// <summary>Called when the selection changes: show that sound's current volume change.</summary>
    private void LoadVolumeForSelection()
    {
        _loadingVolume = true;
        VolumeDb = SelectedRow?.Pending?.Prepared.Options.GainDb ?? 0;
        VolumeNote = null;
        _loadingVolume = false;
        OnPropertyChanged(nameof(VolumeText));
    }

    partial void OnVolumeDbChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeText));
        if (_loadingVolume)
            return;
        _volumeDebounce ??= CreateVolumeDebounce();
        _volumeDebounce.Stop();
        _volumeDebounce.Start();
    }

    private DispatcherTimer CreateVolumeDebounce()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            if (SelectedRow is { } row)
                await SetVolumeAsync(row, Math.Round(VolumeDb * 2) / 2); // half-dB steps
        };
        return timer;
    }

    [RelayCommand]
    private void ResetVolume() => VolumeDb = 0;

    [RelayCommand]
    private void NudgeVolume(string? delta)
    {
        if (double.TryParse(delta, System.Globalization.CultureInfo.InvariantCulture, out var d))
            VolumeDb = Math.Clamp(Math.Round((VolumeDb + d) * 2) / 2, -24, 12);
    }

    /// <summary>Stages <paramref name="row"/> at <paramref name="gainDb"/> dB (relative to its source audio).</summary>
    public async Task SetVolumeAsync(SoundRowViewModel row, double gainDb)
    {
        if (!row.CanReplace || IsBusy)
            return;
        var pending = row.Pending;
        var volumeOnly = pending == null || pending.IsVolumeOnly;

        // Back to 0 dB on a volume-only change: nothing left to stage.
        if (volumeOnly && Math.Abs(gainDb) < 0.01)
        {
            if (pending != null)
            {
                SetPending(row, null);
                RefreshPendingState();
                await ReloadPlayerIfSelected(row);
            }
            VolumeNote = null;
            return;
        }

        IsAdjustingVolume = true;
        try
        {
            var info = row.Info;
            var source = pending?.Prepared.SourcePath ?? await Task.Run(() => SaveGameAudio(row));
            var options = (pending?.Prepared.Options ?? new ConvertOptions()) with { GainDb = gainDb };
            var prepared = await Task.Run(() => ReplacementEncoder.Prepare(source, info.Format, info.Channels, info.SampleRate, options));
            if (!ReferenceEquals(row.Pending, pending))
                return; // something else changed this sound meanwhile
            SetPending(row, new PendingChange(row, prepared) { IsVolumeOnly = volumeOnly });
            RefreshPendingState();
            VolumeNote = Math.Abs(prepared.AppliedGainDb - gainDb) > 0.05
                ? $"Limited to {prepared.AppliedGainDb:+0.0;-0.0} dB so it doesn't distort."
                : null;
            if (SelectedRow == row)
            {
                var note = VolumeNote;
                LoadVolumeForSelection(); // keep the slider in step with what's staged
                VolumeNote = note;
            }
            await ReloadPlayerIfSelected(row);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            VolumeNote = "Couldn't change the volume: " + ex.Message;
        }
        finally
        {
            IsAdjustingVolume = false;
        }
    }

    /// <summary>The sound as the game plays it now, as a WAV to adjust from (kept until applied).</summary>
    private static string SaveGameAudio(SoundRowViewModel row)
    {
        var folder = Path.Combine(AppPaths.DataFolder, "volume");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{row.Name} ({row.BankFileName} #{row.Info.Index}).wav");
        AudioDecoder.Decode(SoundReader.Read(row.Info.Bank.FilePath, row.Info.Index, row.Info.Id)).SaveWav(path);
        return path;
    }

    private Task ReloadPlayerIfSelected(SoundRowViewModel row) =>
        SelectedRow == row ? Player.LoadAsync(row, Player.AutoPlay && !SuppressAutoPlay) : Task.CompletedTask;
}
