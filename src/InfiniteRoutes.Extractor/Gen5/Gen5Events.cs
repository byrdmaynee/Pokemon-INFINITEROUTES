using static InfiniteRoutes.Extractor.Nitro3D.NitroDict;

namespace InfiniteRoutes.Extractor.Gen5;

/// <summary>
/// One warp from a zone's event file. Positions are in 1/16-tile units (tile = value >> 4);
/// <see cref="Direction"/> is the way the player faces on arriving here (1 up, 2 down, 3 left, 4 right, 0 none).
/// </summary>
public sealed record Gen5Warp(int TargetZone, int TargetWarp, int Direction, int Transition, int CoordType,
    int X, int Height, int Z, int Width, int Depth)
{
    /// <summary>Grid warps; coordinate type 1 is placed on a rail and isn't supported yet.</summary>
    public bool OnGrid => CoordType == 0;
    public int TileX => X >> 4;
    public int TileY => Z >> 4;
}

/// <summary>
/// One NPC (or overworld object such as an item ball) from a zone's event file. X/Y are whole tiles;
/// <see cref="Height"/> is fx32 in model units (16 per tile). <see cref="Direction"/>: 0 up, 1 down, 2 left, 3 right.
/// <see cref="Flag"/> is the event flag that hides it (0 = always present); <see cref="RangeX"/>/<see cref="RangeY"/>
/// bound how far a wandering NPC strays from home.
/// </summary>
public sealed record Gen5Npc(int Id, int Sprite, int Movement, int Type, int Flag, int Script, int Direction,
    int Sight, int RangeX, int RangeY, int CoordType, int X, int Y, int Height)
{
    public bool OnGrid => CoordType == 0;
    public float HeightTiles => Height / 65536f / 16;
}

/// <summary>
/// One piece of furniture from a zone's event file: an interactable tile (PCs, signs, bookshelves) running
/// <see cref="Script"/> when the player faces it and presses A. X/Y are whole tiles.
/// </summary>
public sealed record Gen5Furniture(int Script, int Condition, int Interaction, int CoordType, int X, int Y, int Height)
{
    public bool OnGrid => CoordType == 0;
}

/// <summary>
/// Zone event files ("zone entities", <see cref="Gen5Layout.ZoneEvents"/>): u32 size, then counts of
/// furniture (20 bytes each), NPCs (36), warps (20) and triggers (22), each list following the last.
/// </summary>
public static class Gen5Events
{
    public const int FurnitureSize = 20;
    public const int NpcSize = 36;
    public const int WarpSize = 20;

    public static List<Gen5Furniture> ParseFurniture(byte[] file)
    {
        if (file.Length < 8)
            return [];
        int count = file[4];
        var o = Need(file, 8, count * FurnitureSize, "furniture");
        var result = new List<Gen5Furniture>(count);
        for (var i = 0; i < count; i++, o += FurnitureSize)
            result.Add(new Gen5Furniture(U16(file, o), U16(file, o + 2), U16(file, o + 4), U16(file, o + 6),
                U16(file, o + 8), U16(file, o + 12), (int)U32(file, o + 16)));
        return result;
    }

    public static List<Gen5Npc> ParseNpcs(byte[] file)
    {
        if (file.Length < 8)
            return [];
        int npcs = file[5];
        var o = Need(file, 8 + file[4] * FurnitureSize, npcs * NpcSize, "NPC");
        var result = new List<Gen5Npc>(npcs);
        for (var i = 0; i < npcs; i++, o += NpcSize)
            result.Add(new Gen5Npc(U16(file, o), U16(file, o + 2), U16(file, o + 4), U16(file, o + 6), U16(file, o + 8),
                U16(file, o + 10), U16(file, o + 12), U16(file, o + 14), U16(file, o + 20), U16(file, o + 22),
                U16(file, o + 24), U16(file, o + 28), U16(file, o + 30), (int)U32(file, o + 32)));
        return result;
    }

    public static List<Gen5Warp> ParseWarps(byte[] file)
    {
        if (file.Length < 8)
            return [];
        int warps = file[6];
        var o = Need(file, 8 + file[4] * FurnitureSize + file[5] * NpcSize, warps * WarpSize, "warp");
        var result = new List<Gen5Warp>(warps);
        for (var i = 0; i < warps; i++, o += WarpSize)
            result.Add(new Gen5Warp(U16(file, o), U16(file, o + 2), file[o + 4], file[o + 5], U16(file, o + 6),
                U16(file, o + 8), S16(file, o + 10), U16(file, o + 12), U16(file, o + 14), U16(file, o + 16)));
        return result;
    }

    static int Need(byte[] file, int offset, int length, string what) =>
        offset + length <= file.Length ? offset : throw new InvalidDataException($"Zone event file is shorter than its {what} list.");
}

/// <summary>
/// Overworld sprite table (<see cref="Gen5Layout.OverworldTable"/>, first member): u32 count, then 28-byte
/// entries keyed by sprite id, each naming the member of <see cref="Gen5Layout.OverworldSprites"/> to draw.
/// </summary>
public static class Gen5OverworldTable
{
    public const int EntrySize = 28;
    const int TextureOffset = 16;

    /// <summary>Sprite id → member index in the overworld sprite archive.</summary>
    public static Dictionary<int, int> Parse(byte[] table)
    {
        var count = table.Length >= 4 ? (int)U32(table, 0) : 0;
        var result = new Dictionary<int, int>(count);
        for (var i = 0; i < count && 4 + (i + 1) * EntrySize <= table.Length; i++)
        {
            var o = 4 + i * EntrySize;
            result.TryAdd(U16(table, o), U16(table, o + TextureOffset));
        }
        return result;
    }
}
