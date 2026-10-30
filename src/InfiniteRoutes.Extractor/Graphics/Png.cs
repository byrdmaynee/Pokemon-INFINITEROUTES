using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace InfiniteRoutes.Extractor.Graphics;

/// <summary>Minimal RGBA PNG writer (no external image library needed).</summary>
public static class Png
{
    public static byte[] Encode(Rgba image)
    {
        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), image.Height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // RGBA
        WriteChunk(output, "IHDR", ihdr);

        var stride = image.Width * 4;
        var raw = new byte[(stride + 1) * image.Height];
        for (var y = 0; y < image.Height; y++)
            Buffer.BlockCopy(image.Pixels, y * stride, raw, y * (stride + 1) + 1, stride);   // filter 0 per row

        using (var compressed = new MemoryStream())
        {
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw);
            WriteChunk(output, "IDAT", compressed.ToArray());
        }
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    public static void Save(Rgba image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, Encode(image));
    }

    static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc.GetCurrentHashAsUInt32());
        s.Write(c);
    }
}
