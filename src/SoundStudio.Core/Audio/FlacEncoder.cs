namespace SoundStudio.Core.Audio;

/// <summary>
/// Small, spec-conformant FLAC encoder for 16-bit audio, producing streams shaped like Black Ops II's own:
/// a single STREAMINFO block, fixed 1024-sample blocks, independent channels, fixed predictors (order 0–4)
/// with partitioned Rice residuals. Residuals are computed in 64-bit, so — unlike files written by the old
/// Sound Studio — nothing ever overflows.
/// </summary>
public static class FlacEncoder
{
    public const int BlockSize = 1024;

    /// <param name="samples">Interleaved signed 16-bit samples.</param>
    public static byte[] Encode(ReadOnlySpan<short> samples, int channels, int sampleRate)
    {
        if (channels is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate is <= 0 or >= 1 << 20)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (samples.Length % channels != 0)
            throw new ArgumentException("Sample count must be a multiple of the channel count.", nameof(samples));

        var frames = samples.Length / channels;
        var w = new BitWriter(samples.Length * 2 / 2 + 1024);

        // "fLaC" + STREAMINFO (last metadata block).
        w.WriteBits(0x664C6143, 32);
        w.WriteBits(1, 1);          // last block
        w.WriteBits(0, 7);          // STREAMINFO
        w.WriteBits(34, 24);        // length
        w.WriteBits(BlockSize, 16); // min block size
        w.WriteBits(BlockSize, 16); // max block size
        w.WriteBits(0, 24);         // min frame size (unknown)
        w.WriteBits(0, 24);         // max frame size (unknown)
        w.WriteBits((uint)sampleRate, 20);
        w.WriteBits((uint)(channels - 1), 3);
        w.WriteBits(15, 5);         // 16 bits per sample
        w.WriteBits((uint)((long)frames >> 32), 4);
        w.WriteBits((uint)frames, 32);
        for (var i = 0; i < 4; i++)
            w.WriteBits(0, 32);     // MD5 (unset, like the game's files)

        var block = new int[BlockSize];
        var residual = new long[BlockSize];
        var frameNumber = 0u;
        for (var start = 0; start < frames; start += BlockSize, frameNumber++)
        {
            var size = Math.Min(BlockSize, frames - start);
            var frameStart = w.ByteLength;

            // Frame header.
            w.WriteBits(0xFFF8, 16);                         // sync + reserved + fixed blocking
            w.WriteBits(size == BlockSize ? 0b1010u : 0b0111u, 4); // 1024, or 16-bit explicit size
            w.WriteBits(SampleRateCode(sampleRate), 4);
            w.WriteBits((uint)(channels - 1), 4);            // independent channels
            w.WriteBits(0b100, 3);                           // 16 bits per sample
            w.WriteBits(0, 1);
            WriteUtf8(w, frameNumber);
            if (size != BlockSize)
                w.WriteBits((uint)(size - 1), 16);
            w.WriteBits(Crc8(w.Bytes, frameStart, w.ByteLength - frameStart), 8);

            for (var ch = 0; ch < channels; ch++)
            {
                for (var i = 0; i < size; i++)
                    block[i] = samples[(start + i) * channels + ch];
                WriteSubframe(w, block.AsSpan(0, size), residual);
            }

            w.AlignToByte();
            w.WriteBits(Crc16(w.Bytes, frameStart, w.ByteLength - frameStart), 16);
        }

        return w.ToArray();
    }

    private static uint SampleRateCode(int rate) => rate switch
    {
        88200 => 1, 176400 => 2, 192000 => 3, 8000 => 4, 16000 => 5, 22050 => 6, 24000 => 7,
        32000 => 8, 44100 => 9, 48000 => 10, 96000 => 11,
        _ => 0, // take it from STREAMINFO
    };

    private static void WriteSubframe(BitWriter w, ReadOnlySpan<int> s, long[] residualBuffer)
    {
        var n = s.Length;

        // Constant block (e.g. silence): 8 + 16 bits.
        var constant = true;
        for (var i = 1; i < n && constant; i++)
            constant = s[i] == s[0];
        if (constant)
        {
            w.WriteBits(0, 1);
            w.WriteBits(0, 6);
            w.WriteBits(0, 1);
            w.WriteSigned(s[0], 16);
            return;
        }

        // Pick the fixed predictor order with the cheapest residual.
        var bestOrder = -1;
        var bestBits = (long)n * 16; // verbatim cost
        RiceLayout? bestLayout = null;
        for (var order = 0; order <= 4 && order < n; order++)
        {
            var residual = residualBuffer.AsSpan(0, n - order);
            ComputeFixedResidual(s, order, residual);
            var layout = RiceLayout.Choose(residual, n, order);
            var bits = 16L * order + layout.TotalBits;
            if (bits < bestBits)
            {
                bestBits = bits;
                bestOrder = order;
                bestLayout = layout;
            }
        }

        if (bestOrder < 0)
        {
            // Verbatim.
            w.WriteBits(0, 1);
            w.WriteBits(1, 6);
            w.WriteBits(0, 1);
            foreach (var v in s)
                w.WriteSigned(v, 16);
            return;
        }

        w.WriteBits(0, 1);
        w.WriteBits((uint)(8 + bestOrder), 6);
        w.WriteBits(0, 1);
        for (var i = 0; i < bestOrder; i++)
            w.WriteSigned(s[i], 16);
        var res = residualBuffer.AsSpan(0, n - bestOrder);
        ComputeFixedResidual(s, bestOrder, res);
        bestLayout!.Write(w, res, n, bestOrder);
    }

    private static void ComputeFixedResidual(ReadOnlySpan<int> s, int order, Span<long> residual)
    {
        for (var i = order; i < s.Length; i++)
        {
            long x = s[i];
            residual[i - order] = order switch
            {
                0 => x,
                1 => x - s[i - 1],
                2 => x - 2L * s[i - 1] + s[i - 2],
                3 => x - 3L * s[i - 1] + 3L * s[i - 2] - s[i - 3],
                _ => x - 4L * s[i - 1] + 6L * s[i - 2] - 4L * s[i - 3] + s[i - 4],
            };
        }
    }

    /// <summary>Partition order and Rice parameters for one residual.</summary>
    private sealed class RiceLayout
    {
        private int _partitionOrder;
        private int[] _params = [];
        public long TotalBits { get; private set; }
        private bool UseFiveBitParams => _params.Any(p => p > 14);

        public static RiceLayout Choose(ReadOnlySpan<long> residual, int blockSize, int predictorOrder)
        {
            RiceLayout? best = null;
            for (var po = 0; po <= 6; po++)
            {
                var partitions = 1 << po;
                if (blockSize % partitions != 0 || (blockSize >> po) <= predictorOrder)
                    break;
                var layout = new RiceLayout { _partitionOrder = po, _params = new int[partitions] };
                long bits = 2 + 4;
                var offset = 0;
                for (var p = 0; p < partitions; p++)
                {
                    var count = (blockSize >> po) - (p == 0 ? predictorOrder : 0);
                    var part = residual.Slice(offset, count);
                    offset += count;
                    var (k, cost) = BestParameter(part);
                    layout._params[p] = k;
                    bits += cost;
                }
                bits += partitions * (layout.UseFiveBitParams ? 5 : 4);
                layout.TotalBits = bits;
                if (best == null || bits < best.TotalBits)
                    best = layout;
            }
            return best!;
        }

        private static (int K, long Bits) BestParameter(ReadOnlySpan<long> part)
        {
            if (part.Length == 0)
                return (0, 0);
            ulong sum = 0;
            foreach (var r in part)
                sum += ZigZag(r);
            var mean = sum / (ulong)part.Length;
            var guess = mean == 0 ? 0 : Math.Min(30, 63 - System.Numerics.BitOperations.LeadingZeroCount(mean));
            var bestK = 0;
            var bestBits = long.MaxValue;
            for (var k = Math.Max(0, guess - 2); k <= Math.Min(30, guess + 2); k++)
            {
                long bits = 0;
                foreach (var r in part)
                    bits += (long)(ZigZag(r) >> k) + 1 + k;
                if (bits < bestBits)
                {
                    bestBits = bits;
                    bestK = k;
                }
            }
            return (bestK, bestBits);
        }

        public void Write(BitWriter w, ReadOnlySpan<long> residual, int blockSize, int predictorOrder)
        {
            var fiveBit = UseFiveBitParams;
            w.WriteBits(fiveBit ? 1u : 0u, 2);
            w.WriteBits((uint)_partitionOrder, 4);
            var offset = 0;
            for (var p = 0; p < _params.Length; p++)
            {
                var count = (blockSize >> _partitionOrder) - (p == 0 ? predictorOrder : 0);
                var k = _params[p];
                w.WriteBits((uint)k, fiveBit ? 5 : 4);
                foreach (var r in residual.Slice(offset, count))
                {
                    var u = ZigZag(r);
                    w.WriteUnary(u >> k);
                    if (k > 0)
                        w.WriteBits((uint)(u & ((1UL << k) - 1)), k);
                }
                offset += count;
            }
        }

        private static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));
    }

    private static void WriteUtf8(BitWriter w, uint value)
    {
        if (value < 0x80)
        {
            w.WriteBits(value, 8);
            return;
        }
        var bytes = value < 0x800 ? 2 : value < 0x10000 ? 3 : value < 0x200000 ? 4 : value < 0x4000000 ? 5 : 6;
        var lead = (0xFF00u >> bytes) & 0xFF;
        w.WriteBits(lead | (value >> (6 * (bytes - 1))), 8);
        for (var i = bytes - 2; i >= 0; i--)
            w.WriteBits(0x80 | ((value >> (6 * i)) & 0x3F), 8);
    }

    private static uint Crc8(byte[] data, int start, int length)
    {
        byte crc = 0;
        for (var i = start; i < start + length; i++)
        {
            crc ^= data[i];
            for (var b = 0; b < 8; b++)
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
        }
        return crc;
    }

    private static uint Crc16(byte[] data, int start, int length)
    {
        ushort crc = 0;
        for (var i = start; i < start + length; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (var b = 0; b < 8; b++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1);
        }
        return crc;
    }

    /// <summary>MSB-first bit writer.</summary>
    private sealed class BitWriter(int capacity)
    {
        private byte[] _buffer = new byte[Math.Max(capacity, 64)];
        private int _length;   // whole bytes written
        private ulong _acc;    // pending bits
        private int _accBits;

        public byte[] Bytes { get { Flush(); return _buffer; } }
        public int ByteLength { get { Flush(); return _length; } }

        public void WriteBits(uint value, int count)
        {
            if (count == 0)
                return;
            _acc = (_acc << count) | (value & (count == 32 ? 0xFFFFFFFFu : (1u << count) - 1));
            _accBits += count;
            Flush();
        }

        public void WriteSigned(int value, int bits) => WriteBits((uint)value, bits);

        public void WriteUnary(ulong zeros)
        {
            while (zeros >= 32)
            {
                WriteBits(0, 32);
                zeros -= 32;
            }
            WriteBits(1, (int)zeros + 1);
        }

        public void AlignToByte()
        {
            if (_accBits % 8 != 0)
                WriteBits(0, 8 - _accBits % 8);
        }

        public byte[] ToArray()
        {
            AlignToByte();
            Flush();
            return _buffer[.._length];
        }

        private void Flush()
        {
            while (_accBits >= 8)
            {
                if (_length == _buffer.Length)
                    Array.Resize(ref _buffer, _buffer.Length * 2);
                _accBits -= 8;
                _buffer[_length++] = (byte)(_acc >> _accBits);
            }
            _acc &= _accBits == 0 ? 0 : (1UL << _accBits) - 1;
        }
    }
}
