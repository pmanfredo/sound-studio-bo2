using System.Buffers.Binary;
using System.Text;

namespace SoundStudio.Core.Bo2;

/// <summary>
/// A Black Ops II (T6) sound bank: <c>.sabs</c> (streamed) or <c>.sabl</c> (loaded).
/// </summary>
/// <remarks>
/// Layout (little-endian):
/// <code>
/// 0x000  int   magic "2UX#" (0x23585532)
/// 0x004  int   version (0xE for T6)
/// 0x008  int   entry size (0x14)
/// 0x00C  int   checksum size (0x10)
/// 0x010  int   dependency string size (0x40)
/// 0x014  int   entry count
/// 0x018  int   dependency count
/// 0x01C  int   padding
/// 0x020  long  file length
/// 0x028  long  entry table offset
/// 0x030  long  checksum table offset
/// 0x038  byte[16] asset link id
/// 0x048  char[30][0x40] dependency names
/// 0x800  audio data ... entry table ... checksum table (each section 0x800 aligned)
/// </code>
/// The whole 0x800-byte header is kept verbatim so saving never loses unknown bytes.
/// </remarks>
public sealed class SabBank : IDisposable
{
    public const int Magic = 0x23585532;
    public const int VersionT6 = 0xE;
    public const int EntrySize = 0x14;
    public const int ChecksumSize = 0x10;
    public const int DependencySize = 0x40;
    public const int MaxDependencies = 30;
    public const int HeaderSize = 0x800;
    public const int SectionAlignment = 0x800;

    private readonly Stream _stream;
    private readonly object _lock = new();

    private SabBank(string? path, Stream stream)
    {
        FilePath = path;
        _stream = stream;
    }

    /// <summary>Path the bank was opened from, if it came from disk.</summary>
    public string? FilePath { get; }

    /// <summary>Verbatim copy of the 0x800-byte header.</summary>
    public byte[] RawHeader { get; private set; } = [];

    public long DeclaredLength { get; private set; }
    public long EntryTableOffset { get; private set; }
    public long ChecksumTableOffset { get; private set; }
    public byte[] AssetLinkId { get; private set; } = [];
    public IReadOnlyList<string> Dependencies { get; private set; } = [];
    public IReadOnlyList<SabEntry> Entries { get; private set; } = [];

    /// <summary>Length of the underlying file or stream.</summary>
    public long FileLength => _stream.Length;

    public bool HasPendingChanges => Entries.Any(e => e.HasPendingReplacement);

    /// <summary>Opens a bank for reading. The file stays open (shared read) until disposed.</summary>
    public static SabBank Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        try
        {
            var bank = new SabBank(Path.GetFullPath(path), stream);
            bank.Parse();
            return bank;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Parses a bank from a seekable stream. The bank takes ownership of the stream.</summary>
    public static SabBank Open(Stream stream)
    {
        if (!stream.CanSeek || !stream.CanRead)
            throw new ArgumentException("Stream must be readable and seekable.", nameof(stream));
        var bank = new SabBank(null, stream);
        bank.Parse();
        return bank;
    }

    private void Parse()
    {
        if (_stream.Length < HeaderSize)
            throw new InvalidDataException("File is smaller than a sound bank header; it may be truncated.");

        var header = ReadRange(0, HeaderSize);
        var h = header.AsSpan();

        int ReadInt(int offset) => BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset));
        long ReadLong(int offset) => BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(offset));

        if (ReadInt(0x00) != Magic)
            throw new InvalidDataException("Not a sound bank (bad magic value).");

        var version = ReadInt(0x04);
        if (version != VersionT6)
            throw new InvalidDataException(version switch
            {
                0xF => "This is a Black Ops III bank. Only Black Ops II banks are supported.",
                0x4 => "This is an Infinite Warfare bank. Only Black Ops II banks are supported.",
                _ => $"Unsupported sound bank version 0x{version:X}.",
            });

        if (ReadInt(0x08) != EntrySize || ReadInt(0x0C) != ChecksumSize || ReadInt(0x10) != DependencySize)
            throw new InvalidDataException("Unexpected table sizes in the bank header; the file may be corrupt.");

        var entryCount = ReadInt(0x14);
        var dependencyCount = ReadInt(0x18);
        DeclaredLength = ReadLong(0x20);
        EntryTableOffset = ReadLong(0x28);
        ChecksumTableOffset = ReadLong(0x30);

        var fileLength = _stream.Length;
        if (entryCount < 0 || dependencyCount < 0 || dependencyCount > MaxDependencies)
            throw new InvalidDataException("Invalid counts in the bank header; the file may be corrupt.");
        if (DeclaredLength > fileLength)
            throw new InvalidDataException("Bank is shorter than its header says; it may be partially downloaded.");
        if (EntryTableOffset + (long)entryCount * EntrySize > fileLength ||
            ChecksumTableOffset + (long)entryCount * ChecksumSize > fileLength)
            throw new InvalidDataException("Bank tables point past the end of the file; it may be corrupt.");

        RawHeader = header;
        AssetLinkId = h.Slice(0x38, 16).ToArray();

        var deps = new List<string>(dependencyCount);
        for (var i = 0; i < dependencyCount; i++)
        {
            var raw = h.Slice(0x48 + i * DependencySize, DependencySize);
            var end = raw.IndexOf((byte)0);
            deps.Add(Encoding.ASCII.GetString(end < 0 ? raw : raw[..end]));
        }
        Dependencies = deps;

        var table = ReadRange(EntryTableOffset, entryCount * EntrySize);
        var checksums = ReadRange(ChecksumTableOffset, entryCount * ChecksumSize);
        var entries = new List<SabEntry>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var r = table.AsSpan(i * EntrySize, EntrySize);
            var entry = new SabEntry(this, i)
            {
                Id = BinaryPrimitives.ReadUInt32LittleEndian(r),
                Size = BinaryPrimitives.ReadInt32LittleEndian(r[4..]),
                Offset = BinaryPrimitives.ReadUInt32LittleEndian(r[8..]),
                SampleCount = BinaryPrimitives.ReadInt32LittleEndian(r[12..]),
                SampleRateFlag = r[16],
                ChannelCount = r[17],
                LoopRaw = r[18],
                Format = (AudioFormat)r[19],
                Checksum = checksums.AsSpan(i * ChecksumSize, ChecksumSize).ToArray(),
            };
            if (entry.Size < 0 || entry.Offset + entry.Size > fileLength)
                throw new InvalidDataException($"Sound #{i + 1} points past the end of the file; the bank may be corrupt.");
            entries.Add(entry);
        }
        Entries = entries;
    }

    internal byte[] ReadRange(long offset, int count)
    {
        var buffer = new byte[count];
        lock (_lock)
        {
            _stream.Position = offset;
            _stream.ReadExactly(buffer);
        }
        return buffer;
    }

    /// <summary>Copies a byte range of the underlying file to <paramref name="destination"/>.</summary>
    internal void CopyRange(long offset, long count, Stream destination)
    {
        var buffer = new byte[1 << 20];
        while (count > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, count);
            lock (_lock)
            {
                _stream.Position = offset;
                _stream.ReadExactly(buffer, 0, chunk);
            }
            destination.Write(buffer, 0, chunk);
            offset += chunk;
            count -= chunk;
        }
    }

    public void Dispose() => _stream.Dispose();
}
