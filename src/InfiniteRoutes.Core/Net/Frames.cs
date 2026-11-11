using System.Buffers.Binary;
using MessagePack;

namespace InfiniteRoutes.Core.Net;

/// <summary>
/// Frames on a stream: a 4-byte little-endian length, then that many bytes of MessagePack (one <see cref="Message"/>).
/// Frames over <see cref="Protocol.MaxFrame"/> are refused, so a broken or hostile peer can't make the other side
/// allocate without limit.
/// </summary>
public static class Frames
{
    static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard
        .WithSecurity(MessagePackSecurity.UntrustedData);

    public static byte[] Encode(Message message)
    {
        var body = MessagePackSerializer.Serialize(message, Options);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    public static Message Decode(ReadOnlyMemory<byte> body) =>
        MessagePackSerializer.Deserialize<Message>(body, Options) ?? throw new InvalidDataException("Empty message.");

    public static async Task WriteAsync(Stream stream, Message message, CancellationToken ct = default)
    {
        await stream.WriteAsync(Encode(message), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>The next message, or null when the stream ended cleanly between frames.</summary>
    /// <exception cref="InvalidDataException">A frame is too big, cut short or not a message.</exception>
    public static async Task<Message?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await Fill(stream, header, ct, allowEnd: true))
            return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > Protocol.MaxFrame)
            throw new InvalidDataException($"Bad frame length {length}.");
        var body = new byte[length];
        if (!await Fill(stream, body, ct, allowEnd: false))
            throw new InvalidDataException("The connection closed mid-frame.");
        try
        {
            return Decode(body);
        }
        catch (MessagePackSerializationException e)
        {
            throw new InvalidDataException("Not a valid message.", e);
        }
    }

    static async Task<bool> Fill(Stream stream, byte[] buffer, CancellationToken ct, bool allowEnd)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                if (read == 0 && allowEnd)
                    return false;
                throw new InvalidDataException("The connection closed mid-frame.");
            }
            read += n;
        }
        return true;
    }
}
