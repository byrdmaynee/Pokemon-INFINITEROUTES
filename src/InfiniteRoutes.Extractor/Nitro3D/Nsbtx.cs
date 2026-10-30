using InfiniteRoutes.Extractor.Graphics;
using static InfiniteRoutes.Extractor.Nitro3D.NitroDict;

namespace InfiniteRoutes.Extractor.Nitro3D;

public sealed record NitroTexture(string Name, int Width, int Height, int Format, bool Color0Transparent, int DataOffset)
{
    /// <summary>Formats with an alpha channel or a transparent colour need alpha testing/blending.</summary>
    public bool HasAlpha => Format is 1 or 6 || Color0Transparent || Format == 5;
}

/// <summary>
/// A TEX0 block (inside a BTX0 or BMD0 file): texture and palette dictionaries plus raw data.
/// Decodes all seven DS texture formats to RGBA.
/// </summary>
public sealed class Tex0
{
    readonly byte[] _file;
    readonly int _texData, _cmpData, _cmpInfo, _plttData;
    readonly Dictionary<string, int> _palettes = new(StringComparer.Ordinal);

    public Dictionary<string, NitroTexture> Textures { get; } = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> PaletteNames => _palettes.Keys;

    Tex0(byte[] file, int tex0)
    {
        _file = file;
        var b = file;
        var texDict = tex0 + U16(b, tex0 + 0x0E);
        _texData = tex0 + (int)U32(b, tex0 + 0x14);
        _cmpData = tex0 + (int)U32(b, tex0 + 0x24);
        _cmpInfo = tex0 + (int)U32(b, tex0 + 0x28);
        var plttDict = tex0 + (int)U32(b, tex0 + 0x34);
        _plttData = tex0 + (int)U32(b, tex0 + 0x38);

        foreach (var e in Read(b, texDict))
        {
            var p = U32(b, e.EntryOffset);
            Textures[e.Name] = new NitroTexture(e.Name,
                Width: 8 << (int)((p >> 20) & 7),
                Height: 8 << (int)((p >> 23) & 7),
                Format: (int)((p >> 26) & 7),
                Color0Transparent: ((p >> 29) & 1) != 0,
                DataOffset: (int)(p & 0xFFFF) << 3);
        }
        foreach (var e in Read(b, plttDict))
            _palettes[e.Name] = U16(b, e.EntryOffset) << 3;
    }

    /// <summary>Finds the TEX0 block in a BTX0 or BMD0 file; null when the file has none.</summary>
    public static Tex0? FromFile(byte[] file)
    {
        var off = Section(file, "TEX0");
        return off < 0 ? null : new Tex0(file, off);
    }

    public Rgba Decode(NitroTexture t, string? paletteName)
    {
        var b = _file;
        var img = new Rgba(t.Width, t.Height);
        var pal = paletteName is not null && _palettes.TryGetValue(paletteName, out var po) ? _plttData + po : _plttData;
        uint Color(int index) => pal + index * 2 + 1 < _file.Length ? Nclr.Bgr555ToRgba(U16(b, pal + index * 2)) : 0xFF00FFFF;
        var data = _texData + t.DataOffset;
        var n = t.Width * t.Height;

        switch (t.Format)
        {
            case 1: // A3I5
                for (var i = 0; i < n; i++)
                {
                    var v = b[data + i];
                    var a = (v >> 5) * 255 / 7;
                    img.Set(i % t.Width, i / t.Width, (Color(v & 0x1F) & 0xFFFFFF00) | (uint)a);
                }
                break;
            case 2: // 4 colours, 2 bpp
            case 3: // 16 colours, 4 bpp
            case 4: // 256 colours, 8 bpp
            {
                var bpp = t.Format == 2 ? 2 : t.Format == 3 ? 4 : 8;
                var mask = (1 << bpp) - 1;
                for (var i = 0; i < n; i++)
                {
                    var bit = i * bpp;
                    var index = (b[data + bit / 8] >> (bit % 8)) & mask;
                    if (index == 0 && t.Color0Transparent)
                        continue;
                    img.Set(i % t.Width, i / t.Width, Color(index));
                }
                break;
            }
            case 5: // 4×4 compressed
                DecodeCompressed(b, t, img, Color);
                break;
            case 6: // A5I3
                for (var i = 0; i < n; i++)
                {
                    var v = b[data + i];
                    var a = (v >> 3) * 255 / 31;
                    img.Set(i % t.Width, i / t.Width, (Color(v & 7) & 0xFFFFFF00) | (uint)a);
                }
                break;
            case 7: // direct colour
                for (var i = 0; i < n; i++)
                {
                    var c = U16(b, data + i * 2);
                    if ((c & 0x8000) != 0)
                        img.Set(i % t.Width, i / t.Width, Nclr.Bgr555ToRgba(c));
                }
                break;
        }
        return img;
    }

    void DecodeCompressed(byte[] b, NitroTexture t, Rgba img, Func<int, uint> color)
    {
        var blocksWide = t.Width / 4;
        var blocks = blocksWide * (t.Height / 4);
        var texels = _cmpData + t.DataOffset;
        var info = _cmpInfo + t.DataOffset / 2;
        Span<uint> c = stackalloc uint[4];

        for (var blk = 0; blk < blocks; blk++)
        {
            var bits = U32(b, texels + blk * 4);
            var extra = U16(b, info + blk * 2);
            var palIndex = (extra & 0x3FFF) * 2;
            var mode = extra >> 14;

            c[0] = color(palIndex);
            c[1] = color(palIndex + 1);
            switch (mode)
            {
                case 0: c[2] = color(palIndex + 2); c[3] = 0; break;
                case 1: c[2] = Mix(c[0], c[1], 1, 1); c[3] = 0; break;
                case 2: c[2] = color(palIndex + 2); c[3] = color(palIndex + 3); break;
                default: c[2] = Mix(c[0], c[1], 5, 3); c[3] = Mix(c[0], c[1], 3, 5); break;
            }

            var bx = blk % blocksWide * 4;
            var by = blk / blocksWide * 4;
            for (var p = 0; p < 16; p++)
            {
                var v = c[(int)((bits >> (p * 2)) & 3)];
                if (v != 0)
                    img.Set(bx + p % 4, by + p / 4, v);
            }
        }
    }

    static uint Mix(uint a, uint b, int wa, int wb)
    {
        uint Ch(int shift) => (uint)((((a >> shift) & 0xFF) * wa + ((b >> shift) & 0xFF) * wb) / (wa + wb));
        return (Ch(24) << 24) | (Ch(16) << 16) | (Ch(8) << 8) | 0xFF;
    }
}
