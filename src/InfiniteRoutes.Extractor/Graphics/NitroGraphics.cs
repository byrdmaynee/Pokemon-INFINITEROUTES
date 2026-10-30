using System.Buffers.Binary;

namespace InfiniteRoutes.Extractor.Graphics;

/// <summary>An RGBA8888 image.</summary>
public sealed class Rgba(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = new byte[width * height * 4];

    public void Set(int x, int y, uint rgba)
    {
        var i = (y * Width + x) * 4;
        Pixels[i] = (byte)(rgba >> 24);
        Pixels[i + 1] = (byte)(rgba >> 16);
        Pixels[i + 2] = (byte)(rgba >> 8);
        Pixels[i + 3] = (byte)rgba;
    }

    /// <summary>Copies <paramref name="src"/> into this image with its top-left corner at (x, y).</summary>
    public void Blit(Rgba src, int x, int y)
    {
        for (var row = 0; row < src.Height && y + row < Height; row++)
        {
            var w = Math.Min(src.Width, Width - x);
            if (w > 0 && y + row >= 0)
                Array.Copy(src.Pixels, row * src.Width * 4, Pixels, ((y + row) * Width + x) * 4, w * 4);
        }
    }
}

/// <summary>Palette from an NCLR ("RLCN") file: BGR555 colours, 16 per palette for 4bpp.</summary>
public sealed class Nclr
{
    public uint[] Colors { get; }

    Nclr(uint[] colors) => Colors = colors;

    public static Nclr Parse(ReadOnlySpan<byte> file)
    {
        var pltt = NitroFile.FindSection(file, "RLCN", "TTLP");
        var dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(pltt[8..]);
        var dataOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(pltt[12..]);
        var data = pltt[dataOffset..];
        dataSize = Math.Min(dataSize, data.Length);
        var colors = new uint[dataSize / 2];
        for (var i = 0; i < colors.Length; i++)
            colors[i] = Bgr555ToRgba(BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]));
        return new Nclr(colors);
    }

    public static uint Bgr555ToRgba(ushort c)
    {
        uint r = (uint)(c & 0x1F), g = (uint)((c >> 5) & 0x1F), b = (uint)((c >> 10) & 0x1F);
        r = (r << 3) | (r >> 2);
        g = (g << 3) | (g >> 2);
        b = (b << 3) | (b >> 2);
        return (r << 24) | (g << 16) | (b << 8) | 0xFF;
    }
}

/// <summary>Tile/pixel data from an NCGR ("RGCN") file.</summary>
public sealed class Ncgr
{
    /// <summary>One palette index per pixel, in storage order.</summary>
    public byte[] Indices { get; }
    public int BitsPerPixel { get; }
    public int TilesWide { get; }
    public int TilesHigh { get; }
    /// <summary>True when pixels are stored as 8×8 tiles; false for a plain left-to-right bitmap.</summary>
    public bool Tiled { get; }

    Ncgr(byte[] indices, int bpp, int tilesWide, int tilesHigh, bool tiled)
    {
        Indices = indices;
        BitsPerPixel = bpp;
        TilesWide = tilesWide;
        TilesHigh = tilesHigh;
        Tiled = tiled;
    }

    public static Ncgr Parse(ReadOnlySpan<byte> file)
    {
        var chr = NitroFile.FindSection(file, "RGCN", "RAHC");
        int tilesHigh = BinaryPrimitives.ReadUInt16LittleEndian(chr);
        int tilesWide = BinaryPrimitives.ReadUInt16LittleEndian(chr[2..]);
        var depth = BinaryPrimitives.ReadUInt32LittleEndian(chr[4..]);
        // chr[8..12] is the VRAM mapping mode, unused here.
        var linearFlag = chr[12];
        var dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(chr[16..]);
        var dataOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(chr[20..]);
        var data = chr.Slice(dataOffset, Math.Min(dataSize, chr.Length - dataOffset));

        var bpp = depth == 4 ? 8 : 4;
        byte[] indices;
        if (bpp == 8)
            indices = data.ToArray();
        else
        {
            indices = new byte[data.Length * 2];
            for (var i = 0; i < data.Length; i++)
            {
                indices[i * 2] = (byte)(data[i] & 0x0F);
                indices[i * 2 + 1] = (byte)(data[i] >> 4);
            }
        }

        if (tilesWide == 0xFFFF || tilesHigh == 0xFFFF)
            tilesWide = tilesHigh = 0;
        return new Ncgr(indices, bpp, tilesWide, tilesHigh, tiled: (linearFlag & 1) == 0);
    }

    /// <summary>
    /// Renders tiles placed as sprite blocks (x, y, width, height in pixels), consuming tiles in
    /// order, row-major within each block — how OBJ graphics are laid out in 1D mapping mode.
    /// </summary>
    public Rgba RenderBlocks(Nclr palette, int width, int height, IEnumerable<(int X, int Y, int W, int H)> blocks)
    {
        var image = new Rgba(width, height);
        var tile = 0;
        foreach (var (bx, by, bw, bh) in blocks)
        {
            for (var ty = 0; ty < bh / 8; ty++)
            for (var tx = 0; tx < bw / 8; tx++, tile++)
            for (var p = 0; p < 64; p++)
            {
                var i = tile * 64 + p;
                if (i >= Indices.Length || Indices[i] == 0)
                    continue;
                image.Set(bx + tx * 8 + p % 8, by + ty * 8 + p / 8,
                    Indices[i] < palette.Colors.Length ? palette.Colors[Indices[i]] : 0xFF00FFFF);
            }
        }
        return image;
    }

    /// <summary>Gen 5 96×96 battle sprite: 64×64, 32×64, 64×32, 32×32 OBJ blocks.</summary>
    public Rgba RenderGen5Battler(Nclr palette) =>
        RenderBlocks(palette, 96, 96, [(0, 0, 64, 64), (64, 0, 32, 64), (0, 64, 64, 32), (64, 64, 32, 32)]);

    /// <summary>
    /// Renders as a <paramref name="width"/>-pixel-wide image. Palette index 0 is transparent.
    /// </summary>
    public Rgba Render(Nclr palette, int width, int paletteBank = 0)
    {
        var height = Indices.Length / width;
        var image = new Rgba(width, height);
        var tilesPerRow = width / 8;
        var bankOffset = paletteBank * (BitsPerPixel == 4 ? 16 : 256);

        for (var i = 0; i < width * height; i++)
        {
            int x, y;
            if (Tiled)
            {
                var tile = i / 64;
                var inTile = i % 64;
                x = tile % tilesPerRow * 8 + inTile % 8;
                y = tile / tilesPerRow * 8 + inTile / 8;
            }
            else
            {
                x = i % width;
                y = i / width;
            }
            var index = Indices[i];
            if (index == 0)
                continue;
            var colorIndex = bankOffset + index;
            image.Set(x, y, colorIndex < palette.Colors.Length ? palette.Colors[colorIndex] : 0xFF00FFFF);
        }
        return image;
    }
}

public static class NitroFile
{
    /// <summary>
    /// Returns the body of a section (after its 8-byte magic+size header) inside a Nitro
    /// graphics file. Magics are stored reversed on disk (e.g. "RGCN", "RAHC").
    /// </summary>
    public static ReadOnlySpan<byte> FindSection(ReadOnlySpan<byte> file, string fileMagic, string sectionMagic)
    {
        if (file.Length < 16 || !file[..4].SequenceEqual(System.Text.Encoding.ASCII.GetBytes(fileMagic)))
            throw new InvalidDataException($"Not a {fileMagic} file.");
        int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(file[12..]);
        var want = System.Text.Encoding.ASCII.GetBytes(sectionMagic);
        var pos = headerSize;
        while (pos + 8 <= file.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file[(pos + 4)..]);
            if (size < 8)
                break;
            // Some Gen 5 sprites declare a section larger than the file; trust the file length.
            size = Math.Min(size, file.Length - pos);
            if (file.Slice(pos, 4).SequenceEqual(want))
                return file.Slice(pos + 8, size - 8);
            pos += size;
        }
        throw new InvalidDataException($"{fileMagic} file has no {sectionMagic} section.");
    }
}
