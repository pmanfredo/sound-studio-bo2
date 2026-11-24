using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundStudio.App.Services;
using SoundStudio.Audio;
using SoundStudio.Core;
using SoundStudio.Core.Bo2;
using SoundStudio.Core.Editing;

namespace SoundStudio.App.ViewModels;

public enum PlaybackSource { New, Current, Original }

/// <summary>The player bar: loads the selected sound, plays it, and A/B-compares it with its original.</summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private const int WaveformColumns = 900;

    private readonly AudioPlayer _player = new();
    private readonly AppSettings _settings;
    private readonly OriginalsStore _originals;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _loadCts;
    private DecodedAudio? _current;
    private DecodedAudio? _original;
    private DecodedAudio? _new;
    private readonly Dictionary<PlaybackSource, float[]> _peaksBySource = [];

    public PlayerViewModel(AppSettings settings, OriginalsStore originals)
    {
        _settings = settings;
        _originals = originals;
        _player.Volume = settings.Volume;
        _volume = settings.Volume;
        _autoPlay = settings.AutoPlay;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => UpdatePosition();
        _player.PlaybackEnded += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsPlaying = false;
            _timer.Stop();
            UpdatePosition();
        });
    }

    public IUserDialogs Dialogs { get; set; } = new SilentDialogs();

    /// <summary>False in screenshot/automation mode so nothing plays out loud.</summary>
    public bool AllowAutoPlay { get; set; } = true;

    [ObservableProperty] private SoundRowViewModel? _row;

    // A loaded sound can become someone's hidden change (or stop being one) while it's showing: reload so the
    // player never plays it under "Hidden sound from ...".
    partial void OnRowChanged(SoundRowViewModel? oldValue, SoundRowViewModel? newValue)
    {
        if (oldValue != null)
            oldValue.PropertyChanged -= OnRowPropertyChanged;
        if (newValue != null)
            newValue.PropertyChanged += OnRowPropertyChanged;
    }

    private bool _loadedMasked;

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoundRowViewModel.IsMasked) && sender is SoundRowViewModel row && ReferenceEquals(row, Row)
            && row.IsMasked != _loadedMasked)
            _ = LoadAsync(row, autoPlay: false);
    }
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _positionText = "0:00.0";
    [ObservableProperty] private string _durationText = "0:00.0";
    [ObservableProperty] private float[] _peaks = [];

    /// <summary>Staged (not yet applied) audio is available.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSourceSwitch))]
    private bool _hasNewSource;

    /// <summary>The saved original is available (sound is modified and its original was kept).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSourceSwitch))]
    private bool _hasOriginalSource;

    /// <summary>Show the A/B switch when there's more than one version to compare.</summary>
    public bool ShowSourceSwitch => HasNewSource || HasOriginalSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewSource), nameof(IsCurrentSource), nameof(IsOriginalSource))]
    private PlaybackSource _source = PlaybackSource.Current;

    public bool IsNewSource { get => Source == PlaybackSource.New; set { if (value) Source = PlaybackSource.New; } }
    public bool IsCurrentSource { get => Source == PlaybackSource.Current; set { if (value) Source = PlaybackSource.Current; } }
    public bool IsOriginalSource { get => Source == PlaybackSource.Original; set { if (value) Source = PlaybackSource.Original; } }

    [ObservableProperty] private bool _loop;
    [ObservableProperty] private bool _autoPlay;
    [ObservableProperty] private float _volume;

    partial void OnLoopChanged(bool value) => _player.Loop = value;

    partial void OnAutoPlayChanged(bool value)
    {
        _settings.AutoPlay = value;
        TrySaveSettings();
    }

    partial void OnVolumeChanged(float value)
    {
        _player.Volume = value;
        _settings.Volume = value;
    }

    partial void OnSourceChanged(PlaybackSource value) => SwitchSource();

    // ---- loading ---------------------------------------------------------------------------------------

    /// <summary>Loads (and maybe plays) the given sound. Fast clicks cancel earlier loads.</summary>
    public async Task LoadAsync(SoundRowViewModel? row, bool autoPlay)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();

        StopInternal();
        _player.Unload();
        _current = _original = _new = null;
        _peaksBySource.Clear();
        Row = row;
        _loadedMasked = row?.IsMasked == true;
        IsLoaded = false;
        ErrorText = null;
        Peaks = [];
        Progress = 0;
        PositionText = DurationText = FormatTime(TimeSpan.Zero);
        HasNewSource = HasOriginalSource = false;
        SetSourceSilently(PlaybackSource.Current);

        if (row == null)
            return;
        if (row.IsMasked)
            return; // nothing to play until it's applied
        if (row.Info.Format == AudioFormat.Xma4)
        {
            ErrorText = "XMA (Xbox) audio can't be played.";
            return;
        }

        IsLoading = true;
        try
        {
            var info = row.Info;
            var staged = row.Pending is { IsHidden: false } pending ? pending.Prepared.Audio : null; // hidden sounds stay unheard
            var (current, original) = await Task.Run(() =>
            {
                var sound = SoundReader.Read(info.Bank.FilePath, info.Index, info.Id);
                var decoded = AudioDecoder.Decode(sound);
                DecodedAudio? orig = null;
                if (row.IsModified)
                {
                    var saved = _originals.Load(info.Bank.FileName, info.Index, info.Id, sound.Checksum);
                    if (saved != null)
                        orig = AudioDecoder.Decode(saved.Data, sound.Format, sound.Channels, sound.SampleRate);
                }
                return (decoded, orig);
            }, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            _current = current;
            _original = original;
            _new = staged;
            _peaksBySource[PlaybackSource.Current] = current.ComputePeaks(WaveformColumns);
            if (original != null)
                _peaksBySource[PlaybackSource.Original] = original.ComputePeaks(WaveformColumns);
            if (staged != null)
                _peaksBySource[PlaybackSource.New] = staged.ComputePeaks(WaveformColumns);
            HasOriginalSource = original != null;
            HasNewSource = staged != null;

            // Staged audio is what the user wants to hear first.
            SetSourceSilently(staged != null ? PlaybackSource.New : PlaybackSource.Current);
            LoadIntoPlayer(AudioFor(Source)!, _peaksBySource[Source], keepFraction: 0);
            IsLoaded = true;

            if (autoPlay)
                Play();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            ErrorText = "Couldn't play this sound: " + ex.Message;
        }
        finally
        {
            if (!cts.IsCancellationRequested)
                IsLoading = false;
        }
    }

    private DecodedAudio? AudioFor(PlaybackSource source) => source switch
    {
        PlaybackSource.New => _new,
        PlaybackSource.Original => _original,
        _ => _current,
    };

    /// <summary>Changes the source without triggering a reload (used while loading).</summary>
    private void SetSourceSilently(PlaybackSource source)
    {
#pragma warning disable MVVMTK0034 // deliberately bypass OnSourceChanged
        _source = source;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(IsNewSource));
        OnPropertyChanged(nameof(IsCurrentSource));
        OnPropertyChanged(nameof(IsOriginalSource));
    }

    private void LoadIntoPlayer(DecodedAudio audio, float[] peaks, double keepFraction)
    {
        _player.Load(audio);
        _player.Loop = Loop;
        _player.Position = TimeSpan.FromSeconds(audio.Duration.TotalSeconds * keepFraction);
        Peaks = peaks;
        DurationText = FormatTime(audio.Duration);
        UpdatePosition();
    }

    /// <summary>A/B switch: swap audio but keep the playhead where it is, and keep playing.</summary>
    private void SwitchSource()
    {
        var audio = AudioFor(Source);
        if (audio == null || !_peaksBySource.TryGetValue(Source, out var peaks))
            return;
        var wasPlaying = _player.IsPlaying;
        var fraction = _player.Duration.TotalSeconds > 0 ? _player.Position.TotalSeconds / _player.Duration.TotalSeconds : 0;
        StopInternal();
        if (wasPlaying)
        {
            // Mid-listen: keep the playhead so you hear the same moment in the other version.
            LoadIntoPlayer(audio, peaks, Math.Clamp(fraction, 0, 1));
            Play();
        }
        else
        {
            // Same as picking a sound: start from the top, and play if auto-play is on.
            LoadIntoPlayer(audio, peaks, 0);
            if (AutoPlay && AllowAutoPlay)
                Play();
        }
    }

    // ---- transport -------------------------------------------------------------------------------------

    [RelayCommand]
    public void PlayPause()
    {
        if (!IsLoaded)
            return;
        if (_player.IsPlaying) // the real state, not a flag that could be stale
        {
            _player.Pause();
            IsPlaying = false;
            _timer.Stop();
        }
        else
        {
            Play();
        }
    }

    /// <summary>Plays from the start (double-click / Enter).</summary>
    [RelayCommand]
    public void PlayFromStart()
    {
        if (!IsLoaded)
            return;
        _player.Position = TimeSpan.Zero;
        Play();
    }

    [RelayCommand]
    private void Stop()
    {
        StopInternal();
        UpdatePosition();
    }

    /// <summary>Seek to a fraction (0..1) of the sound, e.g. from a click on the waveform.</summary>
    [RelayCommand]
    private void Seek(double fraction)
    {
        if (!IsLoaded)
            return;
        _player.Position = TimeSpan.FromSeconds(_player.Duration.TotalSeconds * Math.Clamp(fraction, 0, 1));
        UpdatePosition();
    }


    [RelayCommand]
    private void ExportWav()
    {
        var audio = AudioFor(Source);
        if (audio == null || Row == null)
            return;
        var suffix = Source switch { PlaybackSource.Original => "_original", PlaybackSource.New => "_new", _ => "" };
        var path = Dialogs.PickSaveFile("Export sound as WAV", $"{Row.Name}{suffix}.wav", "WAV audio|*.wav");
        if (path == null)
            return;
        try
        {
            audio.SaveWav(path);
            Dialogs.Notify("Exported", Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs.Notify("Couldn't export", ex.Message, NoticeKind.Error);
        }
    }

    private void Play()
    {
        _player.Play();
        IsPlaying = true;
        _timer.Start();
    }

    private void StopInternal()
    {
        _player.Stop();
        IsPlaying = false;
        _timer.Stop();
    }

    private void UpdatePosition()
    {
        // Safety net: if playback stopped without us hearing about it, don't keep showing "playing".
        if (IsPlaying && !_player.IsPlaying && _timer.IsEnabled)
        {
            IsPlaying = false;
            _timer.Stop();
        }
        var duration = _player.Duration.TotalSeconds;
        var position = _player.Position;
        Progress = duration > 0 ? Math.Clamp(position.TotalSeconds / duration, 0, 1) : 0;
        PositionText = FormatTime(position);
    }

    public static string FormatTime(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";

    private void TrySaveSettings()
    {
        try { _settings.Save(); }
        catch (IOException) { }
    }

    public void Dispose()
    {
        _timer.Stop();
        _player.Dispose();
        TrySaveSettings();
    }
}
