using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundStudio.App.Services;
using SoundStudio.Audio;
using SoundStudio.Core;
using SoundStudio.Core.Bo2;
using SoundStudio.Core.Editing;

namespace SoundStudio.App.ViewModels;

public enum LayerKind { File, Recording, GameAudio, Original }

/// <summary>What a layer editor had when its mix was staged, so reopening it picks up where it left off.</summary>
public sealed record LayerSession(IReadOnlyList<LayerState> Layers, double MasterGainDb, bool PreventClipping, bool CutToOriginal, string MixPath);

public sealed record LayerState(string Name, LayerKind Kind, DecodedAudio Audio, double GainDb, double OffsetSeconds, bool Muted);

/// <summary>
/// Layer several sounds into one: files, recordings, the game's current audio and the saved original, each with
/// its own volume and start time, plus an overall volume. "Use as replacement" stages the mix like any file.
/// </summary>
public sealed partial class LayersViewModel : ObservableObject, IDisposable
{
    private const int WaveformColumns = 600;

    private readonly AppSettings _settings;
    private readonly OriginalsStore _originals;
    private readonly Func<string, LayerSession, Task> _stage;
    private readonly AudioPlayer _player = new();
    private readonly DispatcherTimer _remixTimer;
    private readonly DispatcherTimer _progressTimer;
    private DecodedAudio? _mix;
    private DecodedAudio? _gameAudio;
    private DecodedAudio? _originalAudio;
    private LayerViewModel? _soloLayer;
    private int _remixVersion;
    private int _recordings;

    public LayersViewModel(SoundRowViewModel target, AppSettings settings, OriginalsStore originals,
        Func<string, LayerSession, Task> stage)
    {
        Target = target;
        _settings = settings;
        _originals = originals;
        _stage = stage;
        _player.Volume = settings.Volume;
        _remixTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _remixTimer.Tick += async (_, _) => { _remixTimer.Stop(); await RemixAsync(); };
        _progressTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        _player.PlaybackEnded += (_, _) => Application.Current?.Dispatcher.BeginInvoke(StopPlayback);
        Layers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasLayers));
            OnPropertyChanged(nameof(LayerCountText));
            UpdateTimeline();
            ScheduleRemix();
        };
    }

    public IUserDialogs Dialogs { get; set; } = new SilentDialogs();

    /// <summary>Set by the window: opens the recorder on top of it.</summary>
    public Action<RecorderViewModel>? ShowRecorder { get; set; }

    /// <summary>Set by the window: multi-select file picker.</summary>
    public Func<string[]?>? PickFiles { get; set; }

    public event EventHandler? RequestClose;

    public SoundRowViewModel Target { get; }
    public string TargetDurationText => SoundRowViewModel.FormatDuration(Target.Info.Duration);
    public ObservableCollection<LayerViewModel> Layers { get; } = [];
    public bool HasLayers => Layers.Count > 0;
    public string LayerCountText => Layers.Count == 1 ? "1 layer" : $"{Layers.Count} layers";

    [ObservableProperty] private bool _hasOriginal;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _isBusy;

    // ---- overall ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterGainText))]
    private double _masterGainDb;
    public string MasterGainText => Math.Abs(MasterGainDb) < 0.05 ? "0 dB" : $"{MasterGainDb:+0.0;-0.0} dB";

    [ObservableProperty] private bool _preventClipping = true;
    [ObservableProperty] private bool _cutToOriginal;

    partial void OnMasterGainDbChanged(double value) => ScheduleRemix();
    partial void OnPreventClippingChanged(bool value) => ScheduleRemix();
    partial void OnCutToOriginalChanged(bool value) => ScheduleRemix();

    [ObservableProperty] private string _mixDurationText = "0.00 s";
    [ObservableProperty] private string? _mixNote;
    [ObservableProperty] private string? _lengthWarning;
    [ObservableProperty] private bool _isPlayingMix;
    public bool CanUse => _mix is { FrameCount: > 0 };

    // ---- timeline ----

    /// <summary>Seconds shown across the timeline: the whole mix plus room to drag clips later.</summary>
    [ObservableProperty] private double _timelineSeconds = 2;

    /// <summary>The part of the timeline in view (zoom): first second shown and how many seconds fit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZoomed), nameof(ScrollMaximum))]
    private double _viewStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZoomed), nameof(ScrollMaximum))]
    private double _viewSeconds = 2;

    public bool IsZoomed => ViewSeconds < TimelineSeconds - 0.001;
    public double ScrollMaximum => Math.Max(0, TimelineSeconds - ViewSeconds);

    /// <summary>Zooms by <paramref name="factor"/> (&lt;1 = in) keeping <paramref name="anchor"/> under the mouse.</summary>
    public void Zoom(double factor, double anchor)
    {
        var seconds = Math.Clamp(ViewSeconds * factor, 0.1, TimelineSeconds);
        var start = anchor - (anchor - ViewStart) * (seconds / ViewSeconds);
        ViewSeconds = seconds;
        ViewStart = Math.Clamp(start, 0, Math.Max(0, TimelineSeconds - seconds));
    }

    /// <summary>Scrolls by a fraction of what's in view.</summary>
    public void Pan(double fraction) =>
        ViewStart = Math.Clamp(ViewStart + fraction * ViewSeconds, 0, Math.Max(0, TimelineSeconds - ViewSeconds));

    [RelayCommand] private void ZoomIn() => Zoom(0.5, PlayheadSeconds >= 0 ? PlayheadSeconds : ViewStart + ViewSeconds / 2);
    [RelayCommand] private void ZoomOut() => Zoom(2, ViewStart + ViewSeconds / 2);

    /// <summary>Shows the whole mix.</summary>
    [RelayCommand]
    private void ZoomFit()
    {
        ViewStart = 0;
        ViewSeconds = TimelineSeconds;
    }

    /// <summary>Zooms so the original's length fills most of the view.</summary>
    [RelayCommand]
    private void ZoomToOriginal()
    {
        ViewStart = 0;
        ViewSeconds = Math.Min(TimelineSeconds, Math.Max(0.1, OriginalSeconds * 1.3));
    }

    partial void OnTimelineSecondsChanged(double oldValue, double newValue)
    {
        // Showing everything: keep showing everything. Zoomed in: keep the zoom, but stay inside the timeline.
        if (ViewSeconds >= oldValue - 0.001)
            ViewSeconds = newValue;
        else
            ViewSeconds = Math.Min(ViewSeconds, newValue);
        ViewStart = Math.Clamp(ViewStart, 0, Math.Max(0, newValue - ViewSeconds));
        OnPropertyChanged(nameof(IsZoomed));
        OnPropertyChanged(nameof(ScrollMaximum));
    }

    /// <summary>Where the playhead is (seconds into the mix), or -1 when nothing plays.</summary>
    [ObservableProperty] private double _playheadSeconds = -1;

    public double OriginalSeconds => Target.Info.Duration.TotalSeconds;

    private bool _dragging;

    /// <summary>A clip is being dragged: keep the timeline scale still so it doesn't shift under the mouse.</summary>
    public void BeginDrag()
    {
        _dragging = true;
        StopPlayback();
    }

    public void EndDrag()
    {
        _dragging = false;
        UpdateTimeline();
    }

    private void UpdateTimeline()
    {
        if (_dragging)
            return;
        var end = Layers.Select(l => l.OffsetSeconds + l.DurationSeconds).DefaultIfEmpty(0).Max();
        var longest = Math.Max(Math.Max(end, OriginalSeconds), 0.5);
        TimelineSeconds = NiceCeiling(longest * 1.25);
    }

    private static double NiceCeiling(double seconds)
    {
        foreach (var step in new[] { 0.25, 0.5, 1, 2, 5, 10, 30 })
            if (seconds <= step * 8)
                return Math.Ceiling(seconds / step) * step;
        return Math.Ceiling(seconds / 60) * 60;
    }

    // ---- setup -------------------------------------------------------------------------------------------

    /// <summary>Loads the game audio (and saved original), then restores <paramref name="session"/> or starts with the game audio.</summary>
    public async Task InitializeAsync(LayerSession? session)
    {
        IsBusy = true;
        try
        {
            var info = Target.Info;
            (_gameAudio, _originalAudio) = await Task.Run(() =>
            {
                var sound = SoundReader.Read(info.Bank.FilePath, info.Index, info.Id);
                var game = AudioDecoder.Decode(sound);
                DecodedAudio? original = null;
                if (Target.IsModified && _originals.Load(info.Bank.FileName, info.Index, info.Id, sound.Checksum) is { } saved)
                    original = AudioDecoder.Decode(saved.Data, sound.Format, sound.Channels, sound.SampleRate);
                return (game, original);
            });
            HasOriginal = _originalAudio != null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            ErrorText = "Couldn't read the game audio: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        if (session != null)
        {
            MasterGainDb = session.MasterGainDb;
            PreventClipping = session.PreventClipping;
            CutToOriginal = session.CutToOriginal;
            foreach (var l in session.Layers)
                Add(l.Name, l.Kind, l.Audio, l.GainDb, l.OffsetSeconds, l.Muted);
        }
        else if (_gameAudio != null)
        {
            Add(Target.IsModified ? "Game audio (your current version)" : "Game audio", LayerKind.GameAudio, _gameAudio);
        }
        await RemixAsync();
    }

    private LayerViewModel Add(string name, LayerKind kind, DecodedAudio audio, double gainDb = 0, double offset = 0, bool muted = false)
    {
        var layer = new LayerViewModel(this, name, kind, audio, audio.ComputePeaks(Math.Clamp((int)(audio.Duration.TotalSeconds * 400), 200, 8000)))
        {
            GainDb = gainDb,
            OffsetSeconds = offset,
            Muted = muted,
        };
        layer.Changed += (_, _) => { UpdateTimeline(); ScheduleRemix(); };
        Layers.Add(layer);
        return layer;
    }

    // ---- adding layers -----------------------------------------------------------------------------------

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var files = PickFiles?.Invoke();
        if (files is { Length: > 0 })
            await AddFilesAsync(files);
    }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var files = MainViewModel.ExpandAudioFiles(paths);
        if (files.Count == 0)
            return;
        IsBusy = true;
        ErrorText = null;
        var errors = new List<string>();
        try
        {
            foreach (var file in files)
            {
                try
                {
                    var audio = await Task.Run(() => ReplacementEncoder.Load(file));
                    if (audio.FrameCount == 0)
                        throw new InvalidDataException("it has no audio");
                    Add(Path.GetFileName(file), LayerKind.File, audio);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
                                               or InvalidOperationException or UnauthorizedAccessException
                                               or System.Runtime.InteropServices.COMException)
                {
                    errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
        if (errors.Count > 0)
            ErrorText = errors.Count == 1 ? errors[0] : $"{errors.Count} files couldn't be added. {errors[0]}";
    }

    [RelayCommand]
    private void AddGameAudio()
    {
        if (_gameAudio != null)
            Add(Target.IsModified ? "Game audio (your current version)" : "Game audio", LayerKind.GameAudio, _gameAudio);
    }

    [RelayCommand]
    private void AddOriginal()
    {
        if (_originalAudio != null)
            Add("Original game audio", LayerKind.Original, _originalAudio);
    }

    [RelayCommand]
    private void Record()
    {
        StopPlayback();
        var recorder = new RecorderViewModel(Target, _settings, path =>
        {
            try
            {
                var take = ReplacementEncoder.Load(path);
                Add($"Recording {++_recordings}", LayerKind.Recording, take);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                ErrorText = "Couldn't add the recording: " + ex.Message;
            }
            return Task.CompletedTask;
        })
        {
            Dialogs = Dialogs,
            UseLabel = "Add as layer",
            UseToolTip = "Add this take as a new layer in the mix",
        };
        ShowRecorder?.Invoke(recorder);
    }

    // ---- layer actions -----------------------------------------------------------------------------------

    public void Remove(LayerViewModel layer)
    {
        if (_soloLayer == layer)
            StopPlayback();
        Layers.Remove(layer);
    }

    public void MoveUp(LayerViewModel layer)
    {
        var i = Layers.IndexOf(layer);
        if (i > 0)
            Layers.Move(i, i - 1);
    }

    public void MoveDown(LayerViewModel layer)
    {
        var i = Layers.IndexOf(layer);
        if (i >= 0 && i < Layers.Count - 1)
            Layers.Move(i, i + 1);
    }

    public void Duplicate(LayerViewModel layer)
    {
        var copy = Add(layer.Name + " (copy)", layer.Kind, layer.Audio, layer.GainDb, layer.OffsetSeconds, layer.Muted);
        Layers.Move(Layers.IndexOf(copy), Layers.IndexOf(layer) + 1);
    }

    /// <summary>Plays one layer on its own (with its volume, without the overall volume).</summary>
    public void PlayLayer(LayerViewModel layer)
    {
        var wasThis = _soloLayer == layer && _player.IsPlaying;
        StopPlayback();
        if (wasThis)
            return;
        var audio = AudioEdit.ApplyGain(layer.Audio, layer.GainDb, preventClipping: false).Audio;
        _player.Load(audio);
        _player.Play();
        _soloLayer = layer;
        layer.IsPlaying = true;
        PlayheadSeconds = layer.OffsetSeconds;
        _progressTimer.Start();
    }

    // ---- the mix -----------------------------------------------------------------------------------------

    private void ScheduleRemix()
    {
        _remixTimer.Stop();
        _remixTimer.Start();
    }

    private async Task RemixAsync()
    {
        var version = ++_remixVersion;
        var info = Target.Info;
        var layers = Layers.Select(l => new MixLayer(l.Audio)
        {
            GainDb = l.GainDb,
            Offset = TimeSpan.FromSeconds(l.OffsetSeconds),
            Muted = l.Muted,
        }).ToList();
        var master = MasterGainDb;
        var prevent = PreventClipping;
        TimeSpan? length = CutToOriginal ? info.Duration : null;

        MixResult result;
        try
        {
            result = await Task.Run(() => LayerMixer.Mix(layers, info.SampleRate, info.Channels, master, prevent, length));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ErrorText = "Couldn't mix: " + ex.Message;
            return;
        }
        if (version != _remixVersion)
            return; // a newer change is already being mixed

        var audio = result.Audio;
        if (CutToOriginal && audio.FrameCount > 0)
            audio = ReplacementEncoder.FadeOutEnd(audio, TimeSpan.FromMilliseconds(50));
        var wasPlaying = IsPlayingMix;
        _resumeAt = wasPlaying ? _player.Position.TotalSeconds : 0;
        if (wasPlaying)
            StopPlayback();
        _mix = audio;
        MixDurationText = SoundRowViewModel.FormatDuration(audio.Duration);
        MixNote = result.WasLimited
            ? $"Overall volume lowered to {result.AppliedMasterDb:+0.0;-0.0} dB so the layers together don't distort."
            : !prevent && result.PeakDb > 0 ? $"Too loud by {result.PeakDb:0.0} dB: the loudest parts will distort." : null;
        LengthWarning = audio.Duration > info.Duration + TimeSpan.FromSeconds(0.25)
            ? $"The mix is {SoundRowViewModel.FormatDuration(audio.Duration)}, longer than the original ({TargetDurationText}). Longer sounds can make some levels load forever."
            : null;
        OnPropertyChanged(nameof(CanUse));

        if (wasPlaying)
            PlayFrom(_resumeAt);
    }

    private double _resumeAt;

    [RelayCommand]
    private void PlayMix()
    {
        if (IsPlayingMix)
            StopPlayback();
        else
            PlayFrom(0);
    }

    /// <summary>Plays the mix from <paramref name="seconds"/> (clicking the ruler).</summary>
    public void PlayFrom(double seconds)
    {
        if (_mix is not { FrameCount: > 0 })
            return;
        if (!IsPlayingMix)
        {
            StopPlayback();
            _player.Load(_mix);
        }
        if (seconds >= _mix.Duration.TotalSeconds)
            seconds = 0;
        _player.Position = TimeSpan.FromSeconds(seconds);
        _player.Play();
        IsPlayingMix = true;
        PlayheadSeconds = seconds;
        _progressTimer.Start();
    }

    [RelayCommand]
    private void Stop() => StopPlayback();

    private void StopPlayback()
    {
        _player.Stop();
        _player.Unload();
        _progressTimer.Stop();
        IsPlayingMix = false;
        PlayheadSeconds = -1;
        if (_soloLayer != null)
        {
            _soloLayer.IsPlaying = false;
            _soloLayer = null;
        }
    }

    private void UpdateProgress()
    {
        var position = _player.Position.TotalSeconds;
        if (IsPlayingMix)
            PlayheadSeconds = position;
        else if (_soloLayer != null)
            PlayheadSeconds = _soloLayer.OffsetSeconds + position;
    }

    // ---- output ------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task UseAsReplacementAsync()
    {
        _remixTimer.Stop();
        await RemixAsync(); // make sure the very last change is in
        if (_mix is not { FrameCount: > 0 } mix)
            return;
        StopPlayback();
        var folder = Path.Combine(AppPaths.DataFolder, "layers");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{Target.Name} (mix of {LayerCountText}, {DateTime.Now:yyyy-MM-dd HH-mm-ss}).wav");
        mix.SaveWav(path);
        var session = new LayerSession(
            Layers.Select(l => new LayerState(l.Name, l.Kind, l.Audio, l.GainDb, l.OffsetSeconds, l.Muted)).ToList(),
            MasterGainDb, PreventClipping, CutToOriginal, path);
        RequestClose?.Invoke(this, EventArgs.Empty);
        await _stage(path, session);
    }

    [RelayCommand]
    private void SaveWav()
    {
        if (_mix is not { FrameCount: > 0 } mix)
            return;
        var path = Dialogs.PickSaveFile("Save mix as WAV", $"{Target.Name} (mix).wav", "WAV audio|*.wav");
        if (path == null)
            return;
        mix.SaveWav(path);
        Dialogs.Notify("Saved", Path.GetFileName(path));
    }

    public void Dispose()
    {
        _remixTimer.Stop();
        _progressTimer.Stop();
        _player.Dispose();
    }
}

/// <summary>One layer in the layer editor.</summary>
public sealed partial class LayerViewModel(LayersViewModel owner, string name, LayerKind kind, DecodedAudio audio, float[] peaks)
    : ObservableObject
{
    public LayersViewModel Owner { get; } = owner;

    public event EventHandler? Changed;

    public string Name { get; } = name;
    public LayerKind Kind { get; } = kind;
    public DecodedAudio Audio { get; } = audio;
    public float[] Peaks { get; } = peaks;
    public double DurationSeconds => Audio.Duration.TotalSeconds;
    public string DurationText => SoundRowViewModel.FormatDuration(Audio.Duration);

    public Wpf.Ui.Controls.SymbolRegular Icon => Kind switch
    {
        LayerKind.Recording => Wpf.Ui.Controls.SymbolRegular.Mic24,
        LayerKind.GameAudio => Wpf.Ui.Controls.SymbolRegular.Games24,
        LayerKind.Original => Wpf.Ui.Controls.SymbolRegular.History24,
        _ => Wpf.Ui.Controls.SymbolRegular.MusicNote224,
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText))]
    private double _gainDb;
    public string GainText => Math.Abs(GainDb) < 0.05 ? "0 dB" : $"{GainDb:+0.0;-0.0} dB";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffsetText), nameof(InfoText))]
    private double _offsetSeconds;
    public string OffsetText => OffsetSeconds < 0.0005 ? "starts at 0" : $"starts at {OffsetSeconds:0.00} s";
    public string InfoText => $"{DurationText} · {OffsetText}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WaveOpacity))]
    private bool _muted;
    public double WaveOpacity => Muted ? 0.3 : 1;
    [ObservableProperty] private bool _isPlaying;

    partial void OnGainDbChanged(double value) => Changed?.Invoke(this, EventArgs.Empty);
    partial void OnOffsetSecondsChanged(double value) => Changed?.Invoke(this, EventArgs.Empty);
    partial void OnMutedChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    [RelayCommand] private void Play() => Owner.PlayLayer(this);
    [RelayCommand] private void Remove() => Owner.Remove(this);
    [RelayCommand] private void MoveUp() => Owner.MoveUp(this);
    [RelayCommand] private void MoveDown() => Owner.MoveDown(this);
    [RelayCommand] private void Duplicate() => Owner.Duplicate(this);
    [RelayCommand] private void ResetGain() => GainDb = 0;
    [RelayCommand] private void ResetOffset() => OffsetSeconds = 0;
}
