using System.Security.Cryptography;

namespace SoundStudio.Core.Tests;

/// <summary>Write-only stream that hashes everything written, so multi-hundred-MB banks need no buffer.</summary>
internal sealed class HashingStream : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _length;

    public byte[] Finish() => _hash.GetHashAndReset();
    public override void Write(byte[] buffer, int offset, int count) { _hash.AppendData(buffer, offset, count); _length += count; }
    public override void Write(ReadOnlySpan<byte> buffer) { _hash.AppendData(buffer); _length += buffer.Length; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
}
