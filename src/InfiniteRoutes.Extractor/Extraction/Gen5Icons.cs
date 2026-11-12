using InfiniteRoutes.Extractor.Graphics;
using InfiniteRoutes.Extractor.Nds;

namespace InfiniteRoutes.Extractor.Extraction;

/// <summary>
/// The small icons Black/White show in menus. Written to <c>sprites/icons/{species:D3}.png</c> (a Pokémon's party
/// icon, its first 32×32 frame) and <c>sprites/items/{item}.png</c> (32×32).
/// </summary>
/// <remarks>
/// <para>
/// Party icons are <see cref="PartyIcons"/>: member 0 holds the three shared 16-colour palettes, members 1–6 the cell and
/// animation data, then two members per icon slot (the icon, and an empty one) in National Dex order from slot 0, the
/// Egg. Which of the three palettes an icon uses comes from a table in the game code we haven't pinned down, so each
/// icon takes the palette whose colours lie closest to its species' battle sprite palette.
/// </para>
/// <para>
/// Item icons are <see cref="ItemIcons"/>: icon graphics and palettes in no fixed order, matched to items by a table
/// in the ARM9 program — one (u16 graphic, u16 palette) member pair per item, item 0 first (the blank icon), item 1
/// (Master Ball) being (2, 3). The table is found by looking for the longest run of pairs naming a graphic and a
/// palette, starting with the Master Ball's.
/// </para>
/// </remarks>
public static class Gen5Icons
{
    public const string PartyIcons = "a/0/0/7";
    public const string ItemIcons = "a/0/2/5";
    const int FirstIcon = 7, Palettes = 3, Size = 32;

    /// <param name="battlePalette">Species id → its normal battle sprite palette, for picking each icon's palette.</param>
    public static void ExportPartyIcons(NitroFs fs, int lastSpecies, Func<int, uint[]?> battlePalette, string dir)
    {
        Directory.CreateDirectory(dir);
        var narc = Narc.Parse(fs.ReadFile(PartyIcons));
        var palette = Nclr.Parse(Lz.DecompressIfNeeded(narc.Read(0)));
        for (var species = 1; species <= lastSpecies; species++)
        {
            var index = FirstIcon + species * 2;
            if (index >= narc.Count || Lz.DecompressIfNeeded(narc.Read(index)) is not { Length: > 0 } file)
                continue;
            var icon = Ncgr.Parse(file);
            var target = battlePalette(species);
            var best = Enumerable.Range(0, Palettes).MinBy(bank => Distance(icon, palette, bank, target));
            Png.Save(Crop(icon.Render(palette, Size, best), Size, Size), Path.Combine(dir, $"{species:D3}.png"));
        }
    }

    /// <summary>How far the colours an icon uses in palette <paramref name="bank"/> lie from the battle sprite's colours.</summary>
    static double Distance(Ncgr icon, Nclr palette, int bank, uint[]? target)
    {
        if (target is null || target.Length == 0)
            return bank;
        var used = icon.Indices.Where(i => i != 0).Distinct().ToList();
        double total = 0;
        foreach (var i in used)
        {
            var c = bank * 16 + i < palette.Colors.Length ? palette.Colors[bank * 16 + i] : 0;
            total += target.Skip(1).Min(t => Sq(c, t, 24) + Sq(c, t, 16) + Sq(c, t, 8));
        }
        return total / Math.Max(1, used.Count);
    }

    static double Sq(uint a, uint b, int shift)
    {
        var d = (int)(a >> shift & 0xFF) - (int)(b >> shift & 0xFF);
        return d * d;
    }

    public static void ExportItemIcons(NitroFs fs, string dir)
    {
        Directory.CreateDirectory(dir);
        var narc = Narc.Parse(fs.ReadFile(ItemIcons));
        var magic = Enumerable.Range(0, narc.Count).Select(i => narc.Read(i) is { Length: >= 4 } m ? System.Text.Encoding.ASCII.GetString(m, 0, 4) : "").ToArray();
        var table = FindItemTable(fs.ReadArm9(), i => i < magic.Length && magic[i] == "RGCN", i => i < magic.Length && magic[i] == "RLCN");
        for (var item = 1; item < table.Count; item++)
        {
            var (graphic, pal) = table[item];
            try
            {
                var icon = Ncgr.Parse(Lz.DecompressIfNeeded(narc.Read(graphic)));
                var colors = Nclr.Parse(Lz.DecompressIfNeeded(narc.Read(pal)));
                Png.Save(Crop(icon.Render(colors, Size), Size, Size), Path.Combine(dir, $"{item}.png"));
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
            }
        }
    }

    /// <summary>The item → (graphic, palette) table: the longest run of such pairs whose item 1 is (2, 3).</summary>
    public static List<(int Graphic, int Palette)> FindItemTable(byte[] code, Func<int, bool> isGraphic, Func<int, bool> isPalette)
    {
        (int Start, int Length) best = (-1, 0);
        for (var align = 0; align < 4; align += 2)
        {
            var run = 0;
            for (var o = align; o + 4 <= code.Length; o += 4)
            {
                int g = code[o] | code[o + 1] << 8, p = code[o + 2] | code[o + 3] << 8;
                if (isGraphic(g) && isPalette(p))
                {
                    run++;
                    var start = o - (run - 1) * 4;
                    if (run > best.Length && start + 8 <= code.Length && code[start + 4] == 2 && code[start + 5] == 0 && code[start + 6] == 3 && code[start + 7] == 0)
                        best = (start, run);
                }
                else
                    run = 0;
            }
        }
        var table = new List<(int, int)>();
        for (var i = 0; i < best.Length; i++)
        {
            var o = best.Start + i * 4;
            table.Add((code[o] | code[o + 1] << 8, code[o + 2] | code[o + 3] << 8));
        }
        return table;
    }

    static Rgba Crop(Rgba image, int width, int height)
    {
        var result = new Rgba(width, height);
        for (var y = 0; y < Math.Min(height, image.Height); y++)
            Array.Copy(image.Pixels, y * image.Width * 4, result.Pixels, y * width * 4, Math.Min(width, image.Width) * 4);
        return result;
    }
}
