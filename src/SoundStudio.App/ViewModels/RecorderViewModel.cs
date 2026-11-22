using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundStudio.App.Services;
using SoundStudio.Audio;
using SoundStudio.Core;
using SoundStudio.Core.Bo2;

namespace SoundStudio.App.ViewModels;

public enum RecorderState { Ready, Countdown, Recording, Recorded }

/// <summary>Something to play while recording (the game audio, the mix so far), so you can record in time with it.</summary>
public sealed record BackingTrack(string Name, Func<Task<DecodedAudio?>>? Load)
{
    public static readonly BackingTrack None = new("Nothing", null);
    public override string ToString() => Name;
}

/// <summary>Record a new take for one sound, trim it, check it against the original, and stage it.</summary>
public sealed partial class RecorderViewModel : ObservableObject, IDisposable
{
    private const int WaveformColumns = 700;

    private readonly AppSettings _settings;
    private readonly Func<string, Task> _stage;
    private readonly bool _demo;
    private readonly DispatcherTimer _meterTimer;
    private readonly AudioPlayer _player = new();
    private readonly AudioPlayer _backingPlayer = new();
    private bool _takeHadBacking;

    /// <summary>
    /// Roughly how long playback takes to reach the speakers; a take recorded along with a backing track is moved
    /// earlier by this much so it lines up.
    /// </summary>
    private const double OutputLatencySeconds = 0.12;
    private MicRecorder? _recorder;
    private DecodedAudio? _take;
    private DecodedAudio? _originalAudio;
    private CancellationTokenSource? _countdownCts;
    private bool _playingOriginal;
    private double _previewStartFraction;
    private double _previewLengthFraction;

    public RecorderViewModel(SoundRowViewModel target, AppSettings settings, Func<string, Task> stage, bool demo = false)
    {
        Target = target;
        _settings = settings;
        _stage = stage;
        _demo = demo;
        _useCountdown = settings.RecordCountdown;
        _normalize = settings.NormalizeRecordings;
        _player.Volume = settings.Volume;
        _backingPlayer.Volume = settings.Volume;
        _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _meterTimer.Tick += (_, _) => Tick();
        _player.PlaybackEnded += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsPreviewing = false;
            _playingOriginal = false;
            PreviewProgress = -1;
        });
    }

    /// <summary>Asks the window to close (after "Use as replacement").</summary>
    public event EventHandler? RequestClose;

    public IUserDialogs Dialogs { get; set; } = new SilentDialogs();

    /// <summary>Text of the main button ("Use as replacement", or "Add as layer" from the layer editor).</summary>
    public string UseLabel { get; init; } = "Use as replacement";
    public string UseToolTip { get; init; } = "Stage this recording; nothing is written to the game until you click Apply";
    public SoundRowViewModel Target { get; }
    public string TargetDurationText => SoundRowViewModel.FormatDuration(Target.Info.Duration);

    public ObservableCollection<MicDevice> Devices { get; } = [];
    [ObservableProperty] private MicDevice? _selectedDevice;
    [ObservableProperty] private string? _errorText;

    /// <summary>Input level for the meter, 0..1 on a dB scale (−60..0 dBFS).</summary>
    [ObservableProperty] private double _level;
    [ObservableProperty] private bool _isClipping;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsCountdown), nameof(IsRecording), nameof(IsRecorded), nameof(CanRecord))]
    private RecorderState _state = RecorderState.Ready;

    public bool IsReady => State == RecorderState.Ready;
    public bool IsCountdown => State == RecorderState.Countdown;
    public bool IsRecording => State == RecorderState.Recording;
    public bool IsRecorded => State == RecorderState.Recorded;
    public bool CanRecord => State is RecorderState.Ready or RecorderState.Recorded && (_recorder != null || _demo);

    /// <summary>What can be played while recording; the first is always "Nothing".</summary>
    public ObservableCollection<BackingTrack> BackingTracks { get; } = [BackingTrack.None];
    public bool HasBackingTracks => BackingTracks.Count > 1;
    [ObservableProperty]
    private BackingTrack _selectedBacking = BackingTrack.None;

    public void AddBackingTrack(string name, Func<Task<DecodedAudio?>> load)
    {
        var track = new BackingTrack(name, load);
        BackingTracks.Add(track);
        OnPropertyChanged(nameof(HasBackingTracks));
        if (name == _settings.RecordBacking)
            SelectedBacking = track;
    }

    /// <summary>
    /// For a take recorded along with a backing track: where its kept part belongs on the backing track's
    /// timeline (seconds), or null when nothing was playing.
    /// </summary>
    public double? KeptStartOnBacking => _takeHadBacking && _take != null
        ? Math.Max(0, TrimStart * _take.Duration.TotalSeconds - OutputLatencySeconds)
        : null;

    [ObservableProperty] private string _countdownText = "";
    [ObservableProperty] private string _elapsedText = "0:00.0";
    [ObservableProperty] private bool _useCountdown;

    [ObservableProperty] private float[] _peaks = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultDurationText), nameof(IsTooLong), nameof(LengthWarning))]
    private double _trimStart;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultDurationText), nameof(IsTooLong), nameof(LengthWarning))]
    private double _trimEnd = 1;
    [ObservableProperty] private bool _normalize;

    /// <summary>Microphone input level 0..100 (the Windows setting), or -1 when not adjustable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMicLevel))]
    private double _micLevel = -1;
    public bool HasMicLevel => MicLevel >= 0;

    /// <summary>Volume change for the take in dB, applied after normalizing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TakeGainText))]
    private double _takeGainDb;
    public string TakeGainText => Math.Abs(TakeGainDb) < 0.05 ? "0 dB" : $"{TakeGainDb:+0.0;-0.0} dB";
    [ObservableProperty] private bool _isPreviewing;
    [ObservableProperty] private double _previewProgress = -1;

    public TimeSpan ResultDuration => _take == null ? TimeSpan.Zero : TimeSpan.FromSeconds(_take.Duration.TotalSeconds * (TrimEnd - TrimStart));
    public string ResultDurationText => SoundRowViewModel.FormatDuration(ResultDuration);
    public bool IsTooLong => _take != null && ResultDuration > Target.Info.Duration + TimeSpan.FromSeconds(0.25);
    public string? LengthWarning => IsTooLong
        ? $"Longer than the original ({TargetDurationText}). Trim it, or it may make some levels load forever."
        : null;

    partial void OnUseCountdownChanged(bool value) => _settings.RecordCountdown = value;
    partial void OnSelectedBackingChanged(BackingTrack value) => _settings.RecordBacking = value?.Load == null ? null : value.Name;
    partial void OnNormalizeChanged(bool value) => _settings.NormalizeRecordings = value;

    partial void OnMicLevelChanged(double value)
    {
        if (_recorder != null && value >= 0 && Math.Abs((_recorder.DeviceLevel ?? -1) * 100 - value) > 0.5)
            _recorder.DeviceLevel = (float)(value / 100);
    }

    partial void OnSelectedDeviceChanged(MicDevice? value)
    {
        if (_demo || value == null)
            return;
        OpenDevice(value);
        _settings.MicrophoneId = value.Id;
    }

    // ---- setup -------------------------------------------------------------------------------------------

    public void Initialize()
    {
        if (_demo)
            return;
        try
        {
            foreach (var d in MicRecorder.ListDevices())
                Devices.Add(d);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            ErrorText = "Couldn't list microphones: " + ex.Message;
        }
        if (Devices.Count == 0)
        {
            ErrorText ??= "No microphone found. Plug one in, then reopen this window.";
            return;
        }
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == _settings.MicrophoneId) ?? Devices[0];
        _meterTimer.Start();
    }

    private void OpenDevice(MicDevice device)
    {
        _recorder?.Dispose();
        _recorder = null;
        ErrorText = null;
        try
        {
            var recorder = new MicRecorder(device);
            recorder.MaxLengthReached += (_, _) => System.Windows.Application.Current.Dispatcher.BeginInvoke(Stop);
            recorder.DeviceStopped += (_, ex) =>
            {
                if (ex != null)
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(() => ErrorText = "The microphone stopped: " + ex.Message);
            };
            recorder.Start();
            _recorder = recorder;
            MicLevel = recorder.DeviceLevel is { } level ? Math.Round(level * 100) : -1;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            ErrorText = $"Couldn't open {device.Name}. Check Windows privacy settings allow microphone access. ({ex.Message})";
        }
        OnPropertyChanged(nameof(CanRecord));
    }

    private void Tick()
    {
        if (_recorder == null)
            return;
        var peak = _recorder.TakePeak();
        var db = peak <= 0 ? -60 : 20 * Math.Log10(peak);
        var target = Math.Clamp((db + 60) / 60, 0, 1);
        Level = Math.Max(target, Level * 0.85); // fast attack, smooth release
        if (peak >= 0.99f)
            IsClipping = true;
        if (IsRecording)
            ElapsedText = PlayerViewModel.FormatTime(_recorder.Recorded);
    }

    // ---- recording ---------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task RecordAsync()
    {
        if (!CanRecord || _recorder == null)
            return;
        StopPreview();
        IsClipping = false;
        DecodedAudio? backing = null;
        if (SelectedBacking?.Load is { } load)
        {
            try
            {
                backing = await load();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            {
                ErrorText = $"Couldn't play {SelectedBacking?.Name.ToLowerInvariant()}: {ex.Message}";
            }
        }
        if (UseCountdown)
        {
            _countdownCts = new CancellationTokenSource();
            State = RecorderState.Countdown;
            try
            {
                for (var n = 3; n >= 1; n--)
                {
                    CountdownText = n.ToString();
                    await Task.Delay(700, _countdownCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                State = _take == null ? RecorderState.Ready : RecorderState.Recorded;
                return;
            }
        }
        _recorder.BeginRecording();
        _takeHadBacking = false;
        if (backing is { FrameCount: > 0 })
        {
            _backingPlayer.Load(backing);
            _backingPlayer.Play();
            _takeHadBacking = true;
        }
        ElapsedText = "0:00.0";
        State = RecorderState.Recording;
    }

    [RelayCommand]
    private void Stop()
    {
        if (State == RecorderState.Countdown)
        {
            _countdownCts?.Cancel();
            return;
        }
        if (State != RecorderState.Recording || _recorder == null)
            return;
        var take = _recorder.EndRecording();
        _backingPlayer.Stop();
        _backingPlayer.Unload();
        if (take.FrameCount == 0)
        {
            State = RecorderState.Ready;
            return;
        }
        LoadTake(take);
    }

    /// <summary>Shows a take for editing; auto-trims the silence around the voice.</summary>
    public void LoadTake(DecodedAudio take)
    {
        _take = take;
        Peaks = take.ComputePeaks(WaveformColumns);
        State = RecorderState.Recorded;
        AutoTrim();
    }

    [RelayCommand]
    private void AutoTrim()
    {
        if (_take == null)
            return;
        var (start, end) = AudioEdit.FindSoundBounds(_take);
        TrimStart = (double)start / _take.FrameCount;
        TrimEnd = Math.Max(TrimStart + 0.01, (double)end / _take.FrameCount);
    }

    [RelayCommand]
    private void ResetTrim()
    {
        TrimStart = 0;
        TrimEnd = 1;
    }

    /// <summary>Shortens the keep-region from the end so it fits the original's length.</summary>
    [RelayCommand]
    private void FitToOriginal()
    {
        if (_take == null)
            return;
        var fraction = Target.Info.Duration.TotalSeconds / _take.Duration.TotalSeconds;
        TrimEnd = Math.Min(TrimEnd, TrimStart + fraction);
    }

    [RelayCommand]
    private void RecordAgain()
    {
        StopPreview();
        _take = null;
        Peaks = [];
        TrimStart = 0;
        TrimEnd = 1;
        State = RecorderState.Ready;
    }

    private DecodedAudio? Processed()
    {
        if (_take == null)
            return null;
        var clip = AudioEdit.Trim(_take, (int)(TrimStart * _take.FrameCount), (int)(TrimEnd * _take.FrameCount));
        if (Normalize)
            clip = AudioEdit.Normalize(clip);
        return AudioEdit.ApplyGain(clip, TakeGainDb).Audio;
    }

    // ---- listening ---------------------------------------------------------------------------------------

    [RelayCommand]
    private void PlayPreview()
    {
        if (IsPreviewing && !_playingOriginal)
        {
            StopPreview();
            return;
        }
        var clip = Processed();
        if (clip == null)
            return;
        StopPreview();
        _previewStartFraction = TrimStart;
        _previewLengthFraction = TrimEnd - TrimStart;
        _player.Load(clip);
        _player.Play();
        IsPreviewing = true;
        _playingOriginal = false;
        _meterTimer.Tick += UpdatePreviewProgress;
    }

    [RelayCommand]
    private async Task HearOriginalAsync()
    {
        StopPreview();
        try
        {
            _originalAudio ??= await Task.Run(() =>
                AudioDecoder.Decode(SoundReader.Read(Target.Info.Bank.FilePath, Target.Info.Index, Target.Info.Id)));
            _player.Load(_originalAudio);
            _player.Play();
            IsPreviewing = true;
            _playingOriginal = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            ErrorText = "Couldn't play the original: " + ex.Message;
        }
    }

    private void UpdatePreviewProgress(object? sender, EventArgs e)
    {
        if (!IsPreviewing || _playingOriginal || _player.Duration.TotalSeconds <= 0)
        {
            _meterTimer.Tick -= UpdatePreviewProgress;
            return;
        }
        PreviewProgress = _previewStartFraction + _previewLengthFraction * (_player.Position.TotalSeconds / _player.Duration.TotalSeconds);
    }

    private void StopPreview()
    {
        _player.Stop();
        _player.Unload();
        IsPreviewing = false;
        _playingOriginal = false;
        PreviewProgress = -1;
    }

    // ---- output ------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task UseAsReplacementAsync()
    {
        var clip = Processed();
        if (clip == null)
            return;
        StopPreview();
        var path = SaveRecording(clip);
        RequestClose?.Invoke(this, EventArgs.Empty);
        await _stage(path);
    }

    [RelayCommand]
    private void SaveWav()
    {
        var clip = Processed();
        if (clip == null)
            return;
        var path = Dialogs.PickSaveFile("Save recording as WAV", $"{Target.Name}.wav", "WAV audio|*.wav");
        if (path == null)
            return;
        clip.SaveWav(path);
        Dialogs.Notify("Saved", Path.GetFileName(path));
    }

    /// <summary>Keeps every take in %LocalAppData%\SoundStudio\recordings, named after the sound.</summary>
    private string SaveRecording(DecodedAudio clip)
    {
        Directory.CreateDirectory(AppPaths.RecordingsFolder);
        var path = Path.Combine(AppPaths.RecordingsFolder, $"{Target.Name} (recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}).wav");
        clip.SaveWav(path);
        return path;
    }

    public void Dispose()
    {
        _countdownCts?.Cancel();
        _meterTimer.Stop();
        _player.Dispose();
        _backingPlayer.Dispose();
        _recorder?.Dispose();
        _recorder = null;
        if (!_demo)
        {
            try { _settings.Save(); } catch (IOException) { }
        }
    }
}
