namespace InfiniteRoutes.Extractor.Nds;

/// <summary>
/// Nintendo LZ77 variants used by DS games: LZ10 (type byte 0x10) and LZ11 (0x11).
/// Header: type byte + 24-bit decompressed size (or 0 followed by a 32-bit size).
/// </summary>
public static class Lz
{
    public static bool IsCompressed(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && (data[0] == 0x10 || data[0] == 0x11) && DecompressedSize(data, out _) > 0;

    /// <summary>Decompresses if the data looks LZ-compressed; otherwise returns it unchanged.</summary>
    public static byte[] DecompressIfNeeded(byte[] data)
    {
        if (!IsCompressed(data))
            return data;
        try
        {
            return Decompress(data);
        }
        catch (InvalidDataException)
        {
            return data;
        }
    }

    /// <exception cref="InvalidDataException">Unknown type or corrupt stream.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> src)
    {
        if (src.Length < 4)
            throw new InvalidDataException("LZ data is too short.");
        var size = DecompressedSize(src, out var pos);
        if (size <= 0 || size > 64 << 20)
            throw new InvalidDataException("LZ data has an invalid size.");

        var dst = new byte[size];
        var outPos = 0;
        var lz11 = src[0] == 0x11;
        if (src[0] != 0x10 && !lz11)
            throw new InvalidDataException($"Unknown compression type 0x{src[0]:X2}.");

        while (outPos < size)
        {
            if (pos >= src.Length)
                throw new InvalidDataException("LZ data ended early.");
            var flags = src[pos++];
            for (var bit = 7; bit >= 0 && outPos < size; bit--)
            {
                if ((flags & (1 << bit)) == 0)
                {
                    if (pos >= src.Length)
                        throw new InvalidDataException("LZ data ended early.");
                    dst[outPos++] = src[pos++];
                    continue;
                }

                int length, distance;
                if (!lz11)
                {
                    Need(src, pos, 2);
                    length = (src[pos] >> 4) + 3;
                    distance = (((src[pos] & 0x0F) << 8) | src[pos + 1]) + 1;
                    pos += 2;
                }
                else
                {
                    Need(src, pos, 2);
                    var indicator = src[pos] >> 4;
                    if (indicator == 0)
                    {
                        Need(src, pos, 3);
                        length = (((src[pos] & 0x0F) << 4) | (src[pos + 1] >> 4)) + 0x11;
                        distance = (((src[pos + 1] & 0x0F) << 8) | src[pos + 2]) + 1;
                        pos += 3;
                    }
                    else if (indicator == 1)
                    {
                        Need(src, pos, 4);
                        length = (((src[pos] & 0x0F) << 12) | (src[pos + 1] << 4) | (src[pos + 2] >> 4)) + 0x111;
                        distance = (((src[pos + 2] & 0x0F) << 8) | src[pos + 3]) + 1;
                        pos += 4;
                    }
                    else
                    {
                        length = indicator + 1;
                        distance = (((src[pos] & 0x0F) << 8) | src[pos + 1]) + 1;
                        pos += 2;
                    }
                }

                if (distance > outPos)
                    throw new InvalidDataException("LZ back-reference points before the start of the output.");
                for (var i = 0; i < length && outPos < size; i++, outPos++)
                    dst[outPos] = dst[outPos - distance];
            }
        }
        return dst;
    }

    static int DecompressedSize(ReadOnlySpan<byte> src, out int headerLength)
    {
        var size = src[1] | (src[2] << 8) | (src[3] << 16);
        headerLength = 4;
        if (size == 0 && src.Length >= 8)
        {
            size = (int)(src[4] | ((uint)src[5] << 8) | ((uint)src[6] << 16) | ((uint)src[7] << 24));
            headerLength = 8;
        }
        return size;
    }

    static void Need(ReadOnlySpan<byte> src, int pos, int count)
    {
        if (pos + count > src.Length)
            throw new InvalidDataException("LZ data ended early.");
    }
}
