using System.IO.Compression;
using System.Text.Json;
using Amc.Core.Events;
using Amc.Core.State;

namespace Amc.Daemon.Storage;

/// <summary>
/// Binary snapshot format: the 4-byte header "AMS" + codec id, then the compressed UTF-8 JSON of <see cref="WorldState"/>.
/// The JSON is streamed straight into the compressor, so the uncompressed state never exists as one string or array.
/// Snapshots written before this format are TEXT values holding plain JSON; <see cref="EventStore"/> still reads them.
/// </summary>
public static class SnapshotCodec
{
    public const byte Brotli = 1;
    public const byte GZip = 2;
    public const int HeaderLength = 4;

    private static ReadOnlySpan<byte> Magic => "AMS"u8;

    /// <summary>Encodes <paramref name="state"/> into pooled chunks. The caller disposes the result.</summary>
    public static ChunkedBuffer Encode(WorldState state, byte codec = GZip)
    {
        var buffer = new ChunkedBuffer();
        try
        {
            buffer.Write(Magic);
            buffer.WriteByte(codec);
            using (var z = Compress(buffer, codec))
                JsonSerializer.Serialize(z, state, Json.Options);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>True if <paramref name="header"/> starts a snapshot in this format.</summary>
    public static bool IsEncoded(ReadOnlySpan<byte> header) =>
        header.Length >= HeaderLength && header[..3].SequenceEqual(Magic) && header[3] is Brotli or GZip;

    /// <summary>Decodes a snapshot stream (header included). Throws <see cref="InvalidDataException"/> or <see cref="JsonException"/> if corrupt.</summary>
    public static WorldState? Decode(Stream source)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        source.ReadExactly(header);
        if (!IsEncoded(header)) throw new InvalidDataException("not an Overhive state snapshot");
        using Stream z = header[3] == Brotli
            ? new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true)
            : new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
        try { return JsonSerializer.Deserialize<WorldState>(z, Json.Options); }
        catch (InvalidOperationException ex) { throw new InvalidDataException("corrupt state snapshot", ex); } // Brotli decoder
    }

    private static Stream Compress(Stream into, byte codec) => codec switch
    {
        // Fastest: snapshots are encoded under the state read lock. On a 33 MB state, gzip (zlib-ng) level 1 took
        // 122 ms for 4.38 MB and Brotli quality 1 168-259 ms for 4.43 MB, so gzip is the default; both decode.
        Brotli => new BrotliStream(into, CompressionLevel.Fastest, leaveOpen: true),
        GZip => new GZipStream(into, CompressionLevel.Fastest, leaveOpen: true),
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };
}
