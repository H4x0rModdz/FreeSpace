namespace FreeSpace.Desktop.Core.Transfers;

/// <summary>A read-only window [offset, offset+length) over a seekable stream, so chunks stream from disk without buffering.</summary>
public sealed class SliceStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private long _read;

    public SliceStream(Stream inner, long offset, long length)
    {
        _inner = inner;
        _length = length;
        _inner.Seek(offset, SeekOrigin.Begin);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var allowed = (int)Math.Min(buffer.Length, _length - _read);
        if (allowed <= 0) return 0;
        var n = _inner.Read(buffer[..allowed]);
        _read += n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var allowed = (int)Math.Min(buffer.Length, _length - _read);
        if (allowed <= 0) return 0;
        var n = await _inner.ReadAsync(buffer[..allowed], ct);
        _read += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
