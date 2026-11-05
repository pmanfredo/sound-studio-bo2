using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundStudio.Audio;

public sealed record MicDevice(string Id, string Name, bool IsDefault)
{
    public override string ToString() => Name;
}

/// <summary>
/// Captures a microphone: while open it measures the input level (for the meter), and between
/// <see cref="BeginRecording"/> and <see cref="EndRecording"/> it keeps the audio, downmixed to mono.
/// </summary>
public sealed class MicRecorder : IDisposable
{
    /// <summary>Longest take we keep (protects memory if someone forgets to stop).</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(10);

    private readonly IWaveIn _capture;
    private readonly object _lock = new();
    private readonly List<float> _samples = [];
    private bool _recording;
    private float _peak;

    /// <summary>Raised (on a capture thread) when <see cref="MaxLength"/> is reached.</summary>
    public event EventHandler? MaxLengthReached;

    /// <summary>Raised (on a capture thread) if the device stops, e.g. it was unplugged.</summary>
    public event EventHandler<Exception?>? DeviceStopped;

    public MicRecorder(MicDevice device)
        : this(CreateCapture(device))
    {
    }

    /// <summary>For tests: any <see cref="IWaveIn"/> source.</summary>
    public MicRecorder(IWaveIn capture)
    {
        _capture = capture;
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => DeviceStopped?.Invoke(this, e.Exception);
    }

    public WaveFormat Format => _capture.WaveFormat;
    public bool IsRecording { get { lock (_lock) return _recording; } }

    public TimeSpan Recorded
    {
        get { lock (_lock) return TimeSpan.FromSeconds((double)_samples.Count / Format.SampleRate); }
    }

    public static IReadOnlyList<MicDevice> ListDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
                defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).ID;
            else if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
                defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID;
        }
        catch (System.Runtime.InteropServices.COMException) { }

        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new MicDevice(d.ID, d.FriendlyName, d.ID == defaultId))
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.Name)
            .ToList();
    }

    private static IWaveIn CreateCapture(MicDevice device)
    {
        using var enumerator = new MMDeviceEnumerator();
        var endpoint = enumerator.GetDevice(device.Id);
        return new WasapiCapture(endpoint) { ShareMode = AudioClientShareMode.Shared };
    }

    /// <summary>Starts listening (level meter only, nothing is kept).</summary>
    public void Start() => _capture.StartRecording();

    public void BeginRecording()
    {
        lock (_lock)
        {
            _samples.Clear();
            _recording = true;
        }
    }

    /// <summary>Stops keeping audio and returns the take as mono 16-bit PCM at the device's rate.</summary>
    public DecodedAudio EndRecording()
    {
        float[] samples;
        lock (_lock)
        {
            _recording = false;
            samples = [.. _samples];
            _samples.Clear();
        }
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var v = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 32767f);
            pcm[i * 2] = (byte)v;
            pcm[i * 2 + 1] = (byte)(v >> 8);
        }
        return new DecodedAudio(pcm, Format.SampleRate, 1);
    }

    /// <summary>Peak input level (0..1) since the last call, for the meter.</summary>
    public float TakePeak()
    {
        lock (_lock)
        {
            var p = _peak;
            _peak = 0;
            return p;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var format = _capture.WaveFormat;
        var frames = ToMonoFloat(e.Buffer, e.BytesRecorded, format);
        var reachedMax = false;
        lock (_lock)
        {
            foreach (var s in frames)
                _peak = Math.Max(_peak, Math.Abs(s));
            if (_recording)
            {
                _samples.AddRange(frames);
                if (_samples.Count >= MaxLength.TotalSeconds * format.SampleRate)
                {
                    _recording = false;
                    reachedMax = true;
                }
            }
        }
        if (reachedMax)
            MaxLengthReached?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Converts a capture buffer (float/16/24/32-bit, any channel count) to mono floats.</summary>
    public static float[] ToMonoFloat(byte[] buffer, int bytes, WaveFormat format)
    {
        var channels = format.Channels;
        var bytesPerSample = format.BitsPerSample / 8;
        var frames = bytes / (bytesPerSample * channels);
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                      (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        var result = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var o = (f * channels + c) * bytesPerSample;
                sum += (isFloat, bytesPerSample) switch
                {
                    (true, 4) => BitConverter.ToSingle(buffer, o),
                    (_, 2) => BitConverter.ToInt16(buffer, o) / 32768f,
                    (_, 3) => ((buffer[o] << 8 | buffer[o + 1] << 16 | buffer[o + 2] << 24) >> 8) / 8388608f,
                    (_, 4) => BitConverter.ToInt32(buffer, o) / 2147483648f,
                    _ => 0f,
                };
            }
            result[f] = sum / channels;
        }
        return result;
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        _capture.Dispose();
    }
}
