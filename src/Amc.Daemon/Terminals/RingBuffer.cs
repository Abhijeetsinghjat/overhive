namespace Amc.Daemon.Terminals;

/// <summary>Fixed-capacity byte ring keeping the most recent output. Not thread-safe (the session locks).</summary>
public sealed class RingBuffer(int capacity)
{
    public const int DefaultCapacity = 2 * 1024 * 1024;

    private readonly byte[] _buf = new byte[capacity];
    private int _start;   // index of the oldest byte
    private int _count;

    public int Capacity => _buf.Length;
    public int Count => _count;
    public long TotalWritten { get; private set; }

    public void Append(ReadOnlySpan<byte> data)
    {
        TotalWritten += data.Length;
        if (data.Length >= _buf.Length)
        {
            data[^_buf.Length..].CopyTo(_buf);
            _start = 0;
            _count = _buf.Length;
            return;
        }
        var end = (_start + _count) % _buf.Length;
        var first = Math.Min(data.Length, _buf.Length - end);
        data[..first].CopyTo(_buf.AsSpan(end));
        data[first..].CopyTo(_buf);
        var overflow = _count + data.Length - _buf.Length;
        if (overflow > 0)
        {
            _start = (_start + overflow) % _buf.Length;
            _count = _buf.Length;
        }
        else _count += data.Length;
    }

    public byte[] ToArray()
    {
        var result = new byte[_count];
        var first = Math.Min(_count, _buf.Length - _start);
        _buf.AsSpan(_start, first).CopyTo(result);
        _buf.AsSpan(0, _count - first).CopyTo(result.AsSpan(first));
        return result;
    }
}
