namespace SoundStudio.Core.Audio;

/// <summary>Decoded FLAC: interleaved 16-bit samples.</summary>
public sealed record FlacAudio(short[] Samples, int SampleRate, int Channels, int BitsPerSample, long DeclaredTotalSamples)
{
    public int FrameCount => Samples.Length / Channels;
}

/// <summary>
/// Self-contained FLAC decoder (per the FLAC format spec). Used instead of Windows' codec because the
/// Windows FLAC source stops early on files re-encoded by other tools, which many modded sounds are.
/// Supports fixed/LPC/verbatim/constant subframes, all stereo decorrelation modes, 4–32 bit input.
/// </summary>
public static class FlacDecoder
{
    public static FlacAudio Decode(byte[] data)
    {
        var r = new BitReader(data);
        if (data.Length < 8 || r.ReadBits(32) != 0x664C6143) // "fLaC"
            throw new InvalidDataException("Not a FLAC stream.");

        int sampleRate = 0, channels = 0, bps = 0;
        long totalSamples = 0;
        bool last;
        do
        {
            last = r.ReadBits(1) == 1;
            var type = (int)r.ReadBits(7);
            var length = (int)r.ReadBits(24);
            if (type == 0) // STREAMINFO
            {
                var start = r.BytePosition;
                r.ReadBits(16); r.ReadBits(16); // min/max block size
                r.ReadBits(24); r.ReadBits(24); // min/max frame size
                sampleRate = (int)r.ReadBits(20);
                channels = (int)r.ReadBits(3) + 1;
                bps = (int)r.ReadBits(5) + 1;
                totalSamples = ((long)r.ReadBits(4) << 32) | r.ReadBits(32);
                r.BytePosition = start + length;
            }
            else
            {
                r.BytePosition += length;
            }
        } while (!last && r.BytePosition < data.Length);

        if (channels == 0)
            throw new InvalidDataException("FLAC stream has no STREAMINFO block.");

        var output = new List<short>(totalSamples > 0 ? (int)Math.Min(totalSamples * channels, int.MaxValue / 2) : 1 << 16);
        var channelBuffers = new int[8][];
        long[] scratch = [];

        while (r.BytePosition + 2 <= data.Length)
        {
            if (!SyncToFrame(r))
                break;
            var frameStart = r.BytePosition;
            try
            {
                DecodeFrame(r, sampleRate, bps, channelBuffers, ref scratch, output, channels);
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                // Corrupt frame: resume searching one byte later.
                r.BytePosition = frameStart + 1;
            }
            if (totalSamples > 0 && output.Count >= totalSamples * channels)
                break;
        }

        var samples = output.ToArray();
        if (totalSamples > 0 && samples.Length > totalSamples * channels)
            samples = samples[..(int)(totalSamples * channels)];
        return new FlacAudio(samples, sampleRate, channels, bps, totalSamples);
    }

    /// <summary>Advances to the next frame sync code (0xFFF8 / 0xFFF9) on a byte boundary.</summary>
    private static bool SyncToFrame(BitReader r)
    {
        r.AlignToByte();
        var data = r.Data;
        for (var i = r.BytePosition; i + 1 < data.Length; i++)
        {
            if (data[i] == 0xFF && (data[i + 1] & 0xFE) == 0xF8)
            {
                r.BytePosition = i;
                return true;
            }
        }
        r.BytePosition = data.Length;
        return false;
    }

    private static readonly int[] SampleRates = [0, 88200, 176400, 192000, 8000, 16000, 22050, 24000, 32000, 44100, 48000, 96000];
    private static readonly int[] SampleSizes = [0, 8, 12, 0, 16, 20, 24, 32];

    private static void DecodeFrame(BitReader r, int streamRate, int streamBps, int[][] buffers, ref long[] scratch,
        List<short> output, int streamChannels)
    {
        r.ReadBits(15); // sync (14) + reserved (1)
        r.ReadBits(1);  // blocking strategy
        var blockSizeCode = (int)r.ReadBits(4);
        var rateCode = (int)r.ReadBits(4);
        var channelAssignment = (int)r.ReadBits(4);
        var sizeCode = (int)r.ReadBits(3);
        if (r.ReadBits(1) != 0)
            throw new InvalidDataException("Reserved bit set.");

        // Frame/sample number, UTF-8 style: skip it.
        var first = (int)r.ReadBits(8);
        var extra = first < 0x80 ? 0 : first < 0xE0 ? 1 : first < 0xF0 ? 2 : first < 0xF8 ? 3 : first < 0xFC ? 4 : first < 0xFE ? 5 : 6;
        for (var i = 0; i < extra; i++)
            r.ReadBits(8);

        var blockSize = blockSizeCode switch
        {
            0 => throw new InvalidDataException("Reserved block size."),
            1 => 192,
            >= 2 and <= 5 => 576 << (blockSizeCode - 2),
            6 => (int)r.ReadBits(8) + 1,
            7 => (int)r.ReadBits(16) + 1,
            _ => 256 << (blockSizeCode - 8),
        };
        switch (rateCode)
        {
            case 12: r.ReadBits(8); break;
            case 13: case 14: r.ReadBits(16); break;
            case 15: throw new InvalidDataException("Invalid sample rate.");
        }
        r.ReadBits(8); // CRC-8 of header

        var bps = sizeCode == 0 ? streamBps : SampleSizes[sizeCode];
        if (bps == 0)
            throw new InvalidDataException("Reserved sample size.");

        var channels = channelAssignment < 8 ? channelAssignment + 1 : 2;
        if (channelAssignment > 10)
            throw new InvalidDataException("Reserved channel assignment.");
        if (channels != streamChannels)
            throw new InvalidDataException("Channel count changed mid-stream.");

        if (scratch.Length < blockSize)
            scratch = new long[blockSize];
        for (var ch = 0; ch < channels; ch++)
        {
            if (buffers[ch] == null || buffers[ch].Length < blockSize)
                buffers[ch] = new int[Math.Max(blockSize, 4608)];
            // The side channel carries one extra bit.
            var subBps = bps + ((channelAssignment == 8 && ch == 1) || (channelAssignment == 9 && ch == 0) || (channelAssignment == 10 && ch == 1) ? 1 : 0);
            DecodeSubframe(r, subBps, blockSize, buffers[ch], scratch);
        }
        r.AlignToByte();
        r.ReadBits(16); // CRC-16

        // Stereo decorrelation.
        if (channelAssignment >= 8)
        {
            var a = buffers[0];
            var b = buffers[1];
            for (var i = 0; i < blockSize; i++)
            {
                switch (channelAssignment)
                {
                    case 8: b[i] = a[i] - b[i]; break;           // left/side -> right = left - side
                    case 9: a[i] = a[i] + b[i]; break;           // side/right -> left = side + right
                    case 10:
                        long mid = ((long)a[i] << 1) | (b[i] & 1L);
                        long side = b[i];
                        a[i] = (int)((mid + side) >> 1);
                        b[i] = (int)((mid - side) >> 1);
                        break;
                }
            }
        }

        // Interleave and convert to 16-bit.
        var shift = bps - 16;
        for (var i = 0; i < blockSize; i++)
        {
            for (var ch = 0; ch < channels; ch++)
            {
                var v = buffers[ch][i];
                v = shift > 0 ? v >> shift : v << -shift;
                // Wrap (not clamp): files written by the old Sound Studio contain residuals that overflow
                // 16 bits; decoders like ffmpeg/libFLAC truncate, which yields the intended audio.
                output.Add(unchecked((short)v));
            }
        }
    }

    private static void DecodeSubframe(BitReader r, int bps, int blockSize, int[] samples, long[] scratch)
    {
        if (r.ReadBits(1) != 0)
            throw new InvalidDataException("Subframe padding bit set.");
        var type = (int)r.ReadBits(6);
        var wasted = 0;
        if (r.ReadBits(1) == 1)
        {
            wasted = 1;
            while (r.ReadBits(1) == 0)
                wasted++;
            bps -= wasted;
        }

        if (type == 0) // constant
        {
            var v = r.ReadSigned(bps);
            Array.Fill(samples, v, 0, blockSize);
        }
        else if (type == 1) // verbatim
        {
            for (var i = 0; i < blockSize; i++)
                samples[i] = r.ReadSigned(bps);
        }
        else if (type is >= 8 and <= 12) // fixed
        {
            var order = type - 8;
            for (var i = 0; i < order; i++)
                samples[i] = r.ReadSigned(bps);
            ReadResidual(r, blockSize, order, samples);
            for (var i = order; i < blockSize; i++)
            {
                samples[i] += order switch
                {
                    0 => 0,
                    1 => samples[i - 1],
                    2 => 2 * samples[i - 1] - samples[i - 2],
                    3 => 3 * samples[i - 1] - 3 * samples[i - 2] + samples[i - 3],
                    _ => 4 * samples[i - 1] - 6 * samples[i - 2] + 4 * samples[i - 3] - samples[i - 4],
                };
            }
        }
        else if (type >= 32) // LPC
        {
            var order = type - 31;
            for (var i = 0; i < order; i++)
                samples[i] = r.ReadSigned(bps);
            var precision = (int)r.ReadBits(4) + 1;
            if (precision == 16)
                throw new InvalidDataException("Invalid LPC precision.");
            var shift = r.ReadSigned(5);
            var coefs = new int[order];
            for (var i = 0; i < order; i++)
                coefs[i] = r.ReadSigned(precision);
            ReadResidual(r, blockSize, order, samples);
            for (var i = order; i < blockSize; i++)
            {
                long sum = 0;
                for (var j = 0; j < order; j++)
                    sum += (long)coefs[j] * samples[i - 1 - j];
                samples[i] += (int)(sum >> shift);
            }
        }
        else
        {
            throw new InvalidDataException($"Reserved subframe type {type}.");
        }

        if (wasted > 0)
            for (var i = 0; i < blockSize; i++)
                samples[i] <<= wasted;
    }

    /// <summary>Reads the Rice-coded residual into samples[order..blockSize).</summary>
    private static void ReadResidual(BitReader r, int blockSize, int order, int[] samples)
    {
        var method = (int)r.ReadBits(2);
        if (method > 1)
            throw new InvalidDataException("Reserved residual coding method.");
        var paramBits = method == 0 ? 4 : 5;
        var escape = method == 0 ? 15 : 31;
        var partitionOrder = (int)r.ReadBits(4);
        var partitions = 1 << partitionOrder;
        var perPartition = blockSize >> partitionOrder;
        var index = order;

        for (var p = 0; p < partitions; p++)
        {
            var count = p == 0 ? perPartition - order : perPartition;
            if (count < 0)
                throw new InvalidDataException("Bad residual partition.");
            var param = (int)r.ReadBits(paramBits);
            if (param == escape)
            {
                var bits = (int)r.ReadBits(5);
                for (var i = 0; i < count; i++)
                    samples[index++] = bits == 0 ? 0 : r.ReadSigned(bits);
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    var q = r.ReadUnary();
                    var v = (q << param) | (param > 0 ? r.ReadBits(param) : 0);
                    samples[index++] = (int)(v >> 1) ^ -(int)(v & 1);
                }
            }
        }
    }

    /// <summary>MSB-first bit reader.</summary>
    private sealed class BitReader(byte[] data)
    {
        private int _bitPos; // bits consumed in current byte (0..7)

        public byte[] Data { get; } = data;
        public int BytePosition { get; set; }

        public void AlignToByte()
        {
            if (_bitPos != 0)
            {
                _bitPos = 0;
                BytePosition++;
            }
        }

        public uint ReadBits(int count)
        {
            uint result = 0;
            while (count > 0)
            {
                if (BytePosition >= Data.Length)
                    throw new InvalidDataException("Unexpected end of FLAC data.");
                var available = 8 - _bitPos;
                var take = Math.Min(available, count);
                var bits = (Data[BytePosition] >> (available - take)) & ((1 << take) - 1);
                result = (result << take) | (uint)bits;
                count -= take;
                _bitPos += take;
                if (_bitPos == 8)
                {
                    _bitPos = 0;
                    BytePosition++;
                }
            }
            return result;
        }

        public int ReadSigned(int bits)
        {
            if (bits == 0)
                return 0;
            var v = ReadBits(bits);
            var signBit = 1u << (bits - 1);
            return (int)((v ^ signBit) - signBit);
        }

        /// <summary>Counts zero bits up to (and consuming) the next one bit.</summary>
        public uint ReadUnary()
        {
            uint count = 0;
            while (true)
            {
                if (BytePosition >= Data.Length)
                    throw new InvalidDataException("Unexpected end of FLAC data.");
                var remaining = (byte)(Data[BytePosition] << _bitPos);
                if (remaining == 0)
                {
                    count += (uint)(8 - _bitPos);
                    _bitPos = 0;
                    BytePosition++;
                    continue;
                }
                var leading = System.Numerics.BitOperations.LeadingZeroCount((uint)remaining) - 24;
                count += (uint)leading;
                _bitPos += leading + 1;
                if (_bitPos >= 8)
                {
                    _bitPos -= 8;
                    BytePosition++;
                }
                return count;
            }
        }
    }
}
