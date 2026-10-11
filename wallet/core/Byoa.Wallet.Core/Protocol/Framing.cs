using System.Buffers.Binary;

namespace Byoa.Wallet.Protocol;

/// <summary>
/// The framing of Chrome native messaging, also used on the wallet's named pipe: a 4-byte little-endian length,
/// then that many bytes of UTF-8 JSON.
/// </summary>
public static class Framing
{
    /// <summary>The largest message the host and the wallet accept. A 15 s clip is about 0.7 MB of JSON.</summary>
    public const int MaxIncoming = 8 * 1024 * 1024;

    /// <summary>Chrome refuses messages from a native host above 1 MB.</summary>
    public const int MaxToBrowser = 1024 * 1024;

    /// <summary>Reads one message. Returns null at a clean end of the stream (no bytes of a new message).</summary>
    /// <exception cref="InvalidDataException">The length is above <paramref name="max"/>.</exception>
    /// <exception cref="EndOfStreamException">The stream ended inside a message.</exception>
    public static async Task<byte[]?> ReadAsync(Stream stream, int max = MaxIncoming, CancellationToken ct = default)
    {
        var header = new byte[4];
        var got = 0;
        while (got < 4)
        {
            var n = await stream.ReadAsync(header.AsMemory(got, 4 - got), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (got == 0) return null;
                throw new EndOfStreamException("the stream ended inside a message length");
            }
            got += n;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > (uint)max) throw new InvalidDataException($"message of {length} bytes is above the limit of {max}");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return body;
    }

    /// <summary>Writes one message (length and body in one write, then a flush).</summary>
    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var buffer = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)payload.Length);
        payload.CopyTo(buffer.AsMemory(4));
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
