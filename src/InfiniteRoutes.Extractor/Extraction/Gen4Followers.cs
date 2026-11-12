using System.Text.Json;
using InfiniteRoutes.Extractor.Graphics;
using InfiniteRoutes.Extractor.Nds;
using InfiniteRoutes.Extractor.Nitro3D;
using InfiniteRoutes.Extractor.Roms;

namespace InfiniteRoutes.Extractor.Extraction;

/// <summary>
/// The walking Pokémon of HeartGold and SoulSilver (the only DS games that have them, #1–493), for Active Buddies.
/// </summary>
/// <remarks>
/// <para>
/// They are the <c>tsure_poke</c> BTX0s in <c>a/0/8/1</c>, entries 297–862: one per species in National Dex order,
/// each followed by its other forms or its female look where the game draws one (<see cref="Extras"/> — worked out
/// by matching every sprite against the species' battle sprites and checked by eye, with Unown's 28 letters and
/// Rotom's six forms as fixed points). Each has eight frames in storage order: two walking up, two down, and four
/// walking right (two pairs alike); walking left is walking right mirrored, as in the games. Palette 0 is the normal
/// colouring, palette 1 the shiny one. Frames are 32 px square, or 64 px for big Pokémon.
/// </para>
/// <para>
/// Output, in <c>sprites/followers/</c>: <c>{species}.png</c> and <c>{species}s.png</c> (shiny), and for the extra
/// sprites <c>{species}_{n}.png</c> / <c>{species}_{n}s.png</c> (n from 1). Each sheet is two columns (the walking
/// frames) by four rows in <see cref="Facing"/> order (down, up, left, right). <c>followers.json</c> lists the frame
/// size per species and which extras are female looks.
/// </para>
/// </remarks>
public static class Gen4Followers
{
    public const string Archive = "a/0/8/1";
    public const int FirstSprite = 297, LastSpecies = 493;

    /// <summary>How many extra sprites follow each species' own (forms, or a female look).</summary>
    public static readonly IReadOnlyDictionary<int, int> Extras = new Dictionary<int, int>
    {
        [3] = 1, [25] = 1, [154] = 1, [172] = 1, [201] = 27, [202] = 1, [208] = 1, [214] = 1, [386] = 3, [412] = 2,
        [413] = 2, [415] = 1, [422] = 1, [423] = 1, [443] = 1, [444] = 1, [445] = 1, [449] = 1, [450] = 1, [479] = 5,
        [487] = 1, [492] = 1, [493] = 17,
    };

    /// <summary>Species whose one extra sprite is the female look (the rest are forms: Pichu's spiky ear, Unown's letters …).</summary>
    public static readonly IReadOnlySet<int> FemaleLooks = new HashSet<int> { 3, 25, 154, 202, 208, 214, 415, 443, 444, 445, 449, 450 };

    public static bool Supports(GameDefinition game) => game.Profile == "gen4-hgss";

    /// <summary>The archive entry of species <paramref name="species"/>'s own sprite.</summary>
    public static int SpriteOf(int species) =>
        FirstSprite + species - 1 + Extras.Where(e => e.Key < species).Sum(e => e.Value);

    /// <summary>What <c>followers.json</c> holds.</summary>
    public sealed record Index(Dictionary<int, int> FrameSize, Dictionary<int, int> Extras, List<int> FemaleLooks);

    public static string Extract(string romPath, GameDefinition game, string gameCode, string sha1,
        IProgress<ExtractionProgress>? progress = null, CancellationToken ct = default)
    {
        if (!Supports(game))
            throw new NotSupportedException($"{game.Name} has no walking Pokémon.");
        var target = ExtractionCache.DirectoryFor(gameCode, sha1);
        using var guard = ExtractionCache.Lock(target, out var waited, ct);
        if (waited && ExtractionCache.IsComplete(target))
            return target;
        var temp = target + ".partial";
        if (Directory.Exists(temp))
            Directory.Delete(temp, recursive: true);
        var outDir = Directory.CreateDirectory(Path.Combine(temp, "sprites", "followers")).FullName;

        using (var fs = NitroFs.Open(RomSource.OpenSeekable(romPath)))
        {
            var narc = Narc.Parse(fs.ReadFile(Archive));
            var sizes = new Dictionary<int, int>();
            for (var species = 1; species <= LastSpecies; species++)
            {
                ct.ThrowIfCancellationRequested();
                if (species % 25 == 0)
                    progress?.Report(new($"Walking Pokémon ({species}/{LastSpecies})", (double)species / LastSpecies));
                var first = SpriteOf(species);
                for (var n = 0; n <= Extras.GetValueOrDefault(species); n++)
                {
                    if (first + n >= narc.Count || Tex0.FromFile(Lz.DecompressIfNeeded(narc.Read(first + n))) is not { } tex)
                        continue;
                    var palettes = tex.PaletteNames.Order(StringComparer.Ordinal).ToList();
                    var name = n == 0 ? $"{species}" : $"{species}_{n}";
                    var normal = Sheet(tex, palettes.FirstOrDefault());
                    Png.Save(normal, Path.Combine(outDir, name + ".png"));
                    if (palettes.Count > 1)
                        Png.Save(Sheet(tex, palettes[1]), Path.Combine(outDir, name + "s.png"));
                    if (n == 0)
                        sizes[species] = normal.Width / 2;
                }
            }
            File.WriteAllText(Path.Combine(outDir, "followers.json"), JsonSerializer.Serialize(
                new Index(sizes, Extras.ToDictionary(), [.. FemaleLooks.Order()]), Json.Indented));
        }

        ExtractionCache.WriteStamp(temp, new ExtractionStamp(ExtractionCache.ExtractorVersion, game.Id, gameCode, sha1, DateTime.UtcNow));
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.Move(temp, target);
        progress?.Report(new("Done", 1));
        return target;
    }

    /// <summary>Two walking frames per row: down, up, left, right (left mirrored).</summary>
    public static Rgba Sheet(Tex0 tex, string? palette)
    {
        var frames = tex.Textures.Values.Select(t => tex.Decode(t, palette)).ToList();
        var size = frames.Count > 0 ? frames[0].Width : 32;
        var sheet = new Rgba(size * 2, size * 4);
        // Storage order: up ×2, down ×2, then the side view ×4 (two pairs alike), drawn facing left.
        (int Row, int Frame, bool Flip)[] layout =
        [
            ((int)Facing.Down, 2, false), ((int)Facing.Down, 3, false),
            ((int)Facing.Up, 0, false), ((int)Facing.Up, 1, false),
            ((int)Facing.Left, 4, false), ((int)Facing.Left, 5, false),
            ((int)Facing.Right, 4, true), ((int)Facing.Right, 5, true),
        ];
        for (var k = 0; k < layout.Length; k++)
        {
            var (row, frame, flip) = layout[k];
            if (frame >= frames.Count)
                continue;
            var image = flip ? Mirror(frames[frame]) : frames[frame];
            sheet.Blit(image, k % 2 * size, row * size);
        }
        return sheet;
    }

    static Rgba Mirror(Rgba image)
    {
        var m = new Rgba(image.Width, image.Height);
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                Array.Copy(image.Pixels, (y * image.Width + x) * 4, m.Pixels, (y * image.Width + image.Width - 1 - x) * 4, 4);
        return m;
    }
}
