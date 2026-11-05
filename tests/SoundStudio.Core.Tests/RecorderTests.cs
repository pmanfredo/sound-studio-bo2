using NAudio.Wave;
using SoundStudio.Audio;

namespace SoundStudio.Core.Tests;

/// <summary>Recorder and edit tests use a fake input device: the real microphone is never opened.</summary>
public class RecorderTests
{
    private sealed class FakeMic(WaveFormat format) : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = format;
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public bool Started { get; private set; }
        public void StartRecording() => Started = true;
        public void StopRecording() => RecordingStopped?.Invoke(this, new StoppedEventArgs());
        public void Dispose() { }
        public void Feed(byte[] buffer) => DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, buffer.Length));
    }

    private static byte[] StereoFloat(int frames, Func<int, float> left, Func<int, float> right)
    {
        var b = new byte[frames * 8];
        for (var i = 0; i < frames; i++)
        {
            BitConverter.GetBytes(left(i)).CopyTo(b, i * 8);
            BitConverter.GetBytes(right(i)).CopyTo(b, i * 8 + 4);
        }
        return b;
    }

    [Fact]
    public void Keeps_only_audio_between_begin_and_end_as_mono()
    {
        var mic = new FakeMic(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        using var recorder = new MicRecorder(mic);
        recorder.Start();
        Assert.True(mic.Started);

        mic.Feed(StereoFloat(4800, _ => 0.9f, _ => 0.9f)); // before recording: meter only
        Assert.InRange(recorder.TakePeak(), 0.89f, 0.91f);
        Assert.Equal(0f, recorder.TakePeak());               // peak resets after reading

        recorder.BeginRecording();
        mic.Feed(StereoFloat(48000, _ => 0.5f, _ => 0.1f));  // 1 s
        mic.Feed(StereoFloat(24000, _ => -0.2f, _ => -0.2f)); // 0.5 s
        Assert.Equal(1.5, recorder.Recorded.TotalSeconds, 3);
        var take = recorder.EndRecording();
        mic.Feed(StereoFloat(4800, _ => 0.7f, _ => 0.7f));   // after: ignored

        Assert.Equal(1, take.Channels);
        Assert.Equal(48000, take.SampleRate);
        Assert.Equal(72000, take.FrameCount);
        var samples = new short[take.FrameCount];
        Buffer.BlockCopy(take.Pcm16, 0, samples, 0, take.Pcm16.Length);
        Assert.Equal((short)Math.Round(0.3 * 32767), samples[0]);      // (0.5 + 0.1) / 2
        Assert.Equal((short)Math.Round(-0.2 * 32767), samples[^1]);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void Converts_integer_capture_formats(int bits)
    {
        var format = new WaveFormat(44100, bits, 1);
        var bytesPer = bits / 8;
        var buffer = new byte[bytesPer * 2];
        // +half scale, -quarter scale
        long half = 1L << (bits - 2), quarter = -(1L << (bits - 3));
        for (var b = 0; b < bytesPer; b++)
        {
            buffer[b] = (byte)(half >> (8 * b));
            buffer[bytesPer + b] = (byte)(quarter >> (8 * b));
        }
        var mono = MicRecorder.ToMonoFloat(buffer, buffer.Length, format);
        Assert.Equal(0.5f, mono[0], 3);
        Assert.Equal(-0.25f, mono[1], 3);
    }

    [Fact]
    public void Finds_the_voice_and_trims_with_padding()
    {
        // 1 s silence, 0.5 s "voice", 1 s silence (with a little noise floor) at 48 kHz.
        const int rate = 48000;
        var s = new short[rate * 5 / 2];
        var rng = new Random(3);
        for (var i = 0; i < s.Length; i++)
            s[i] = (short)(i is >= rate and < rate * 3 / 2 ? 10000 * Math.Sin(i * 0.05) : rng.Next(-40, 40));
        var audio = new DecodedAudio(ToBytes(s), rate, 1);

        var (start, end) = AudioEdit.FindSoundBounds(audio);
        Assert.InRange(start, rate - (int)(0.08 * rate) - 50, rate - (int)(0.08 * rate) + 50);
        Assert.InRange(end, rate * 3 / 2 + (int)(0.15 * rate) - 50, rate * 3 / 2 + (int)(0.15 * rate) + 50);

        var trimmed = AudioEdit.Trim(audio, start, end);
        Assert.Equal(end - start, trimmed.FrameCount);
        var t = new short[trimmed.FrameCount];
        Buffer.BlockCopy(trimmed.Pcm16, 0, t, 0, trimmed.Pcm16.Length);
        Assert.Equal(0, t[0]); // faded in
    }

    [Fact]
    public void Normalizes_to_minus_one_dB_and_leaves_silence_alone()
    {
        var quiet = new DecodedAudio(ToBytes([0, 1000, -2000, 500]), 48000, 1);
        var loud = AudioEdit.Normalize(quiet);
        Assert.Equal(-1.0, AudioEdit.PeakDb(loud), 1);

        var silence = new DecodedAudio(new byte[100], 48000, 1);
        Assert.Same(silence, AudioEdit.Normalize(silence));
        Assert.Equal((0, 50), AudioEdit.FindSoundBounds(silence));
    }

    private static byte[] ToBytes(short[] s) { var b = new byte[s.Length * 2]; Buffer.BlockCopy(s, 0, b, 0, b.Length); return b; }
}
