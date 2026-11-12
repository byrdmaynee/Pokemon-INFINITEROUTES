namespace InfiniteRoutes.Extractor.Nds;

/// <summary>
/// "Backward LZ", how the DS compresses its ARM9 binary and overlays: the file's last 8 bytes give the compressed
/// span's length (low 24 bits) and footer length (high 8), then how much bigger it gets; the span is decoded from its
/// end towards its start, each flag byte's bits (high first) choosing a literal or a (length, distance) copy.
/// </summary>
public static class Blz
{
    public static byte[] Decompress(byte[] data)
    {
        if (data.Length < 8)
            return data;
        var grow = BitConverter.ToInt32(data, data.Length - 4);
        if (grow <= 0)
            return data;
        var top = BitConverter.ToUInt32(data, data.Length - 8);
        int compressed = (int)(top & 0xFFFFFF), footer = (int)(top >> 24);
        if (compressed > data.Length || footer > compressed)
            return data;
        var output = new byte[data.Length + grow];
        Array.Copy(data, output, data.Length);
        int src = data.Length - footer, end = data.Length - compressed, dst = output.Length;
        while (src > end && dst > 0)
        {
            int flags = data[--src];
            for (var bit = 0; bit < 8 && src > end && dst > 0; bit++, flags <<= 1)
            {
                if ((flags & 0x80) == 0)
                {
                    output[--dst] = data[--src];
                    continue;
                }
                src -= 2;
                var pair = data[src + 1] << 8 | data[src];
                int length = (pair >> 12) + 3, distance = (pair & 0xFFF) + 3;
                for (var k = 0; k < length && dst > 0; k++)
                {
                    dst--;
                    output[dst] = output[dst + distance];
                }
            }
        }
        return output;
    }
}
