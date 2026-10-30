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
/// Zone event files ("zone entities", <see cref="Gen5Layout.ZoneEvents"/>): u32 size, then counts of
/// furniture (20 bytes each), NPCs (36), warps (20) and triggers (22), each list following the last.
/// </summary>
public static class Gen5Events
{
    public const int FurnitureSize = 20;
    public const int NpcSize = 36;
    public const int WarpSize = 20;

    public static List<Gen5Warp> ParseWarps(byte[] file)
    {
        if (file.Length < 8)
            return [];
        int furniture = file[4], npcs = file[5], warps = file[6];
        var o = 8 + furniture * FurnitureSize + npcs * NpcSize;
        if (o + warps * WarpSize > file.Length)
            throw new InvalidDataException("Zone event file is shorter than its warp list.");

        var result = new List<Gen5Warp>(warps);
        for (var i = 0; i < warps; i++, o += WarpSize)
            result.Add(new Gen5Warp(U16(file, o), U16(file, o + 2), file[o + 4], file[o + 5], U16(file, o + 6),
                U16(file, o + 8), S16(file, o + 10), U16(file, o + 12), U16(file, o + 14), U16(file, o + 16)));
        return result;
    }
}
