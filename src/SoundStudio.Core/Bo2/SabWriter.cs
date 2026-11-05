using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SoundStudio.Core.Bo2;

public enum ChecksumMode
{
    /// <summary>
    /// Keep each sound's original MD5 in the checksum table even when its audio changes. The game does not
    /// verify it, and keeping it lets us detect modified sounds later (data MD5 != stored MD5).
    /// </summary>
    KeepOriginal,

    /// <summary>Recompute checksums for replaced sounds (hides the modification from detection).</summary>
    Recompute,
}

/// <summary>
/// Writes a T6 bank using the same layout the game's own banks use: header, audio data packed in table
/// order from 0x800, then the entry table and the checksum table, each padded to 0x800.
/// Writing is strictly sequential, so any writable stream works.
/// </summary>
public static class SabWriter
{
    private const int A = SabBank.SectionAlignment;

    public static void Write(SabBank bank, Stream output, ChecksumMode checksums = ChecksumMode.KeepOriginal)
    {
        var entries = bank.Entries;

        // 1. Compute the layout up front.
        var offsets = new long[entries.Count];
        var sizes = new int[entries.Count];
        long position = SabBank.HeaderSize;
        for (var i = 0; i < entries.Count; i++)
        {
            offsets[i] = position;
            sizes[i] = entries[i].PendingReplacement?.Length ?? entries[i].Size;
            position += sizes[i];
        }
        if (position > uint.MaxValue)
            throw new InvalidOperationException("The bank would exceed 4 GB, which Black Ops II cannot address.");

        var entryTableOffset = Align(position);
        var checksumTableOffset = Align(entryTableOffset + (long)entries.Count * SabBank.EntrySize);
        var totalLength = Align(checksumTableOffset + (long)entries.Count * SabBank.ChecksumSize);

        // 2. Header: verbatim copy with the layout fields patched.
        var header = (byte[])bank.RawHeader.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x14), entries.Count);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x20), totalLength);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x28), entryTableOffset);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0x30), checksumTableOffset);
        output.Write(header);

        // 3. Audio data.
        var newChecksums = new byte[entries.Count][];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            newChecksums[i] = entry.Checksum;
            if (entry.PendingReplacement is { } data)
            {
                output.Write(data);
                if (checksums == ChecksumMode.Recompute)
                    newChecksums[i] = MD5.HashData(data);
            }
            else
            {
                bank.CopyRange(entry.Offset, entry.Size, output);
            }
        }
        Pad(output, position);

        // 4. Entry table.
        var record = new byte[SabBank.EntrySize];
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var r = record.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(r, e.Id);
            BinaryPrimitives.WriteInt32LittleEndian(r[4..], sizes[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(r[8..], (uint)offsets[i]);
            BinaryPrimitives.WriteInt32LittleEndian(r[12..], e.SampleCount);
            r[16] = e.SampleRateFlag;
            r[17] = e.ChannelCount;
            r[18] = e.LoopRaw;
            r[19] = (byte)e.Format;
            output.Write(record);
        }
        Pad(output, entryTableOffset + (long)entries.Count * SabBank.EntrySize);

        // 5. Checksum table.
        foreach (var checksum in newChecksums)
            output.Write(checksum);
        Pad(output, checksumTableOffset + (long)entries.Count * SabBank.ChecksumSize);
    }

    /// <summary>
    /// Saves pending changes back to the bank's own file. Writes to a temporary file in the same folder first,
    /// then swaps it in, so a crash or full disk never leaves a half-written bank. The passed bank is disposed;
    /// use the returned, freshly opened bank afterwards.
    /// </summary>
    public static SabBank SaveInPlace(SabBank bank, ChecksumMode checksums = ChecksumMode.KeepOriginal)
    {
        var path = bank.FilePath ?? throw new InvalidOperationException("Bank was not opened from a file.");
        var temp = path + ".saving";
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                Write(bank, fs, checksums);
                fs.Flush(flushToDisk: true);
            }

            // Sanity check the result before it replaces anything.
            using (SabBank.Open(temp)) { }

            bank.Dispose();
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        return SabBank.Open(path);
    }

    private static long Align(long value) => (value + A - 1) / A * A;

    private static void Pad(Stream output, long position)
    {
        var padding = Align(position) - position;
        if (padding > 0)
            output.Write(new byte[padding]);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
