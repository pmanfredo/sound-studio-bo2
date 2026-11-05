using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundStudio.Audio;

/// <summary>Plays one <see cref="DecodedAudio"/> at a time with seek, volume and loop.</summary>
public sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private RawSourceWaveStream? _stream;
    private VolumeSampleProvider? _volumeProvider;
    private float _volume = 0.8f;
    private bool _stopRequested;

    /// <summary>Raised (on a background thread) when playback stops by itself at the end.</summary>
    public event EventHandler? PlaybackEnded;

    public DecodedAudio? Audio { get; private set; }
    public bool Loop { get; set; }
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public TimeSpan Duration => Audio?.Duration ?? TimeSpan.Zero;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_volumeProvider != null)
                _volumeProvider.Volume = _volume;
        }
    }

    public TimeSpan Position
    {
        get => _stream?.CurrentTime ?? TimeSpan.Zero;
        set
        {
            if (_stream == null)
                return;
            var clamped = value < TimeSpan.Zero ? TimeSpan.Zero : value > _stream.TotalTime ? _stream.TotalTime : value;
            // Keep the position on a frame boundary.
            var bytes = (long)(clamped.TotalSeconds * _stream.WaveFormat.AverageBytesPerSecond);
            _stream.Position = bytes - bytes % _stream.WaveFormat.BlockAlign;
        }
    }

    public void Load(DecodedAudio audio)
    {
        Unload();
        Audio = audio;
        _stream = new RawSourceWaveStream(new MemoryStream(audio.Pcm16, writable: false), audio.WaveFormat);
        _volumeProvider = new VolumeSampleProvider(new LoopingSampleProvider(this, _stream.ToSampleProvider())) { Volume = _volume };
        _output = new WaveOutEvent { DesiredLatency = 120 };
        _output.Init(_volumeProvider);
        _output.PlaybackStopped += OnPlaybackStopped;
    }

    public void Play()
    {
        if (_output == null || _stream == null)
            return;
        if (_stream.Position >= _stream.Length)
            _stream.Position = 0;
        _output.Play();
    }

    public void Pause() => _output?.Pause();

    public void Stop()
    {
        if (_output == null)
            return;
        _stopRequested = true;
        _output.Stop();
        Position = TimeSpan.Zero;
    }

    public void Unload()
    {
        if (_output != null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Dispose();
            _output = null;
        }
        _stream?.Dispose();
        _stream = null;
        _volumeProvider = null;
        Audio = null;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopRequested)
        {
            _stopRequested = false;
            return;
        }
        if (_stream != null)
            _stream.Position = 0;
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Unload();

    /// <summary>Restarts the source at the end while <see cref="Loop"/> is on.</summary>
    private sealed class LoopingSampleProvider(AudioPlayer owner, ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = source.Read(buffer, offset + total, count - total);
                if (read == 0)
                {
                    if (!owner.Loop || owner._stream == null || owner._stream.Length == 0)
                        break;
                    owner._stream.Position = 0;
                    continue;
                }
                total += read;
            }
            return total;
        }
    }
}
