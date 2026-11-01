using InfiniteRoutes.Extractor.Graphics;
using InfiniteRoutes.Extractor.Nitro3D;
using static InfiniteRoutes.Extractor.Nitro3D.NitroDict;

namespace InfiniteRoutes.Extractor.Gen5;

/// <summary>One frame of a <see cref="TexturePattern"/>: shown from <paramref name="Start"/> (in 1/30 s) until the next.</summary>
public sealed record PatternFrame(int Start, string Image);

/// <summary>
/// A frame-swap map texture animation (swaying flowers, sea foam, river banks): every material using
/// <paramref name="Texture"/> (or one of its numbered frames, "hana01.1") cycles through the frames every
/// <paramref name="Length"/> 1/30 s ticks.
/// </summary>
public sealed record TexturePattern(string Texture, int Length, IReadOnlyList<PatternFrame> Frames);

/// <summary>
/// Map texture frame animations (<see cref="Gen5Layout.MapPatternAnimations"/>, indexed by the area table's
/// u16 @4 high byte). A file is u32 n, then 2n + 1 offsets: n pairs of (frame table, BTX0) and the end. A frame
/// table is u32 frame count k, k u16 start ticks, k u8 texture indices, k u8 palette indices, u32 track count t,
/// t u8 first-frame indices (each list padded to 4 bytes), u32 length in ticks. Each track plays its run of frames
/// on its own; indices refer to the BTX0's texture and palette dictionaries in order.
/// </summary>
public static class Gen5TexturePatterns
{
    /// <summary>Decodes the file's animations, saving each frame as a PNG in <paramref name="imageDir"/>.</summary>
    public static List<TexturePattern> Parse(byte[] file, string imageDir)
    {
        if (file.Length < 4)
            throw new InvalidDataException("Texture pattern file is empty.");
        var count = (int)U32(file, 0);
        if (count <= 0 || 4 + (2 * count + 1) * 4 > file.Length)
            throw new InvalidDataException("Bad texture pattern header.");
        Directory.CreateDirectory(imageDir);

        var result = new List<TexturePattern>();
        for (var i = 0; i < count; i++)
        {
            var table = (int)U32(file, 4 + i * 8);
            var btx = (int)U32(file, 8 + i * 8);
            var end = (int)U32(file, 12 + i * 8);
            if (table >= btx || btx >= end || end > file.Length)
                throw new InvalidDataException($"Bad texture pattern entry {i}.");
            var tex = Tex0.FromFile(file[btx..end]) ?? throw new InvalidDataException($"Texture pattern {i} has no TEX0.");
            var textures = tex.Textures.Values.ToList();
            var palettes = tex.PaletteNames.ToList();
            if (textures.Count == 0)
                continue;

            var k = (int)U32(file, table);
            var starts = table + 4;
            var texIndices = starts + Pad(k * 2);
            var palIndices = texIndices + Pad(k);
            var tracks = palIndices + Pad(k);
            var trackCount = Math.Max(1, (int)U32(file, tracks));
            var length = (int)U32(file, tracks + 4 + Pad(trackCount));
            for (var track = 0; track < trackCount; track++)
            {
                var first = file[tracks + 4 + track];
                var last = track + 1 < trackCount ? file[tracks + 5 + track] : k;
                if (first < last && last <= k)
                    result.Add(Track(first, last));
            }

            TexturePattern Track(int first, int last)
            {
                var frames = new List<PatternFrame>();
                for (var f = first; f < last; f++)
                {
                    var t = textures[Math.Min(file[texIndices + f], textures.Count - 1)];
                    var p = palettes.Count > 0 ? palettes[Math.Min(file[palIndices + f], palettes.Count - 1)] : null;
                    var image = $"{t.Name}__{p}.png";
                    var path = Path.Combine(imageDir, image);
                    if (!File.Exists(path))
                        Png.Save(tex.Decode(t, p), path);
                    frames.Add(new PatternFrame(U16(file, starts + f * 2), image));
                }
                return new TexturePattern(BaseName(textures[0].Name), Math.Max(1, length), frames);
            }
        }
        return result;
    }

    /// <summary>"hana01.1" → "hana01": the name shared by an animated texture's frames.</summary>
    public static string BaseName(string texture)
    {
        var dot = texture.LastIndexOf('.');
        return dot > 0 && texture[(dot + 1)..].All(char.IsAsciiDigit) && dot + 1 < texture.Length ? texture[..dot] : texture;
    }

    static int Pad(int bytes) => (bytes + 3) & ~3;
}
