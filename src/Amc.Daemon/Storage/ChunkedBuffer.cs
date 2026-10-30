using System.Buffers;

namespace Amc.Daemon.Storage;

/// <summary>
/// Write-only stream over 64 KB chunks rented from <see cref="ArrayPool{T}.Shared"/>. Large payloads (state snapshots,
/// /api/state) are built without one contiguous array: no large-object-heap allocation, no doubling copies, and the
/// chunks go back to the pool on <see cref="Stream.Dispose()"/>.
/// </summary>
public sealed class ChunkedBuffer : Stream
{
    /// <summary>Below the 85 000-byte large-object threshold, so chunks live on the normal (compacting) heap.</summary>
    public const int ChunkSize = 64 * 1024;

    private readonly List<byte[]> _chunks = [];
    private int _used = ChunkSize; // bytes used in the last chunk (ChunkSize = "needs a new chunk")
    private long _length;
    private bool _disposed;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => _length;
    public override long Position { get => _length; set => throw new NotSupportedException(); }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (buffer.Length > 0)
        {
            if (_used == ChunkSize)
            {
                _chunks.Add(ArrayPool<byte>.Shared.Rent(ChunkSize));
                _used = 0;
            }
            var n = Math.Min(buffer.Length, ChunkSize - _used);
            buffer[..n].CopyTo(_chunks[^1].AsSpan(_used));
            _used += n;
            _length += n;
            buffer = buffer[n..];
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void WriteByte(byte value) => Write([value]);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The written bytes, in order.</summary>
    public IEnumerable<ReadOnlyMemory<byte>> Segments()
    {
        for (var i = 0; i < _chunks.Count; i++)
            yield return _chunks[i].AsMemory(0, i == _chunks.Count - 1 ? _used : ChunkSize);
    }

    /// <summary>Copies the content to <paramref name="destination"/> (this stream itself cannot be read).</summary>
    public void WriteTo(Stream destination)
    {
        foreach (var s in Segments()) destination.Write(s.Span);
    }

    public async Task WriteToAsync(Stream destination, CancellationToken ct = default)
    {
        foreach (var s in Segments()) await destination.WriteAsync(s, ct);
    }

    /// <summary>A read-only stream over the content (for decoding without copying it into one array).</summary>
    public Stream OpenRead() => new SegmentReader(this);

    public byte[] ToArray()
    {
        var a = new byte[_length];
        var at = 0;
        foreach (var s in Segments()) { s.Span.CopyTo(a.AsSpan(at)); at += s.Length; }
        return a;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            foreach (var c in _chunks) ArrayPool<byte>.Shared.Return(c);
            _chunks.Clear();
            _length = 0;
        }
        base.Dispose(disposing);
    }

    private sealed class SegmentReader(ChunkedBuffer owner) : Stream
    {
        private readonly IEnumerator<ReadOnlyMemory<byte>> _segments = owner.Segments().GetEnumerator();
        private ReadOnlyMemory<byte> _current = ReadOnlyMemory<byte>.Empty;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => owner.Length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(Span<byte> buffer)
        {
            while (_current.IsEmpty)
                if (!_segments.MoveNext()) return 0;
                else _current = _segments.Current;
            var n = Math.Min(buffer.Length, _current.Length);
            _current.Span[..n].CopyTo(buffer);
            _current = _current[n..];
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
