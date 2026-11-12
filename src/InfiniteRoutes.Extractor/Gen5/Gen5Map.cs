using System.Text;
using static InfiniteRoutes.Extractor.Nitro3D.NitroDict;

namespace InfiniteRoutes.Extractor.Gen5;

/// <summary>
/// One Black/White map chunk (a/0/0/8): a short container — 2-char magic, u16 section count,
/// u32 section offsets — holding the BMD0 model, the collision grid (usually 32×32, 64×64 on the larger map type)
/// and building placements. Rail maps' chunks ("NG") have no grid.
/// </summary>
public sealed class Gen5Map
{
    public const int Tiles = 32;

    /// <summary>Tiles per side of <see cref="Collision"/> (32 unless the chunk says otherwise).</summary>
    public int Size { get; init; } = Tiles;

    public required string Magic { get; init; }
    public required byte[] Model { get; init; }
    /// <summary>Raw 8-byte tile records, row-major, or null when the chunk has none.</summary>
    public byte[]? Collision { get; init; }
    public required List<Gen5Building> Buildings { get; init; }

    public static Gen5Map Parse(byte[] data)
    {
        var magic = Encoding.ASCII.GetString(data, 0, 2);
        int count = U16(data, 2);
        var offsets = new int[count + 1];
        for (var i = 0; i <= count; i++)
            offsets[i] = i < count || 4 + i * 4 + 4 <= data.Length ? (int)U32(data, 4 + i * 4) : data.Length;
        if (offsets[count] <= 0 || offsets[count] > data.Length)
            offsets[count] = data.Length;
        byte[] Section(int i) => data[offsets[i]..offsets[i + 1]];

        var model = Section(0);
        if (!model.AsSpan(0, 4).SequenceEqual("BMD0"u8))
            throw new InvalidDataException($"Map chunk ({magic}) doesn't start with a model.");

        byte[]? collision = null;
        var size = Tiles;
        var buildings = new List<Gen5Building>();
        for (var i = 1; i < count; i++)
        {
            var s = Section(i);
            int n = s.Length >= 4 ? U16(s, 0) : 0;
            if (n is 32 or 64 or 128 && U16(s, 2) == n && s.Length >= 4 + n * n * 8 && (collision is null || n == size))
            {
                var layer = s[4..(4 + n * n * 8)];
                collision = collision is null ? layer : Merge(collision, layer, n);
                size = n;
            }
            else if (i == count - 1 && s.Length >= 4)
                buildings = Gen5Building.ParseList(s);
        }

        return new Gen5Map { Magic = magic, Model = model, Collision = collision, Size = size, Buildings = buildings };
    }

    /// <summary>
    /// "GC" chunks have a second collision layer: the upper level where a bridge or raised path crosses the ground
    /// layer (Pinwheel Forest's log bridges over the river). A tile is walkable when either level is; where only the
    /// upper one is, its record wins.
    /// </summary>
    static byte[] Merge(byte[] ground, byte[] upper, int size)
    {
        var merged = (byte[])ground.Clone();
        for (var t = 0; t < size * size; t++)
        {
            var o = t * 8;
            if (Walkable(U16(upper, o + 6)) && !Walkable(U16(ground, o + 6)))
                Array.Copy(upper, o, merged, o, 8);
        }
        return merged;
    }

    static bool Walkable(int flags) => (flags & 1) == 0 && !IsWaterFlags(flags);

    /// <summary>Water: 0x16 (sea, ponds) and 0x56 (rivers).</summary>
    static bool IsWaterFlags(int flags) => flags is 0x16 or 0x56;

    /// <summary>
    /// Tile record: four u16 words. The last one carries movement flags: bit 0 = solid. Values seen
    /// on Route 1: 0x80 open ground, 0x81 solid, 0x24 tall grass, 0x16 water (verified by laying
    /// the grid over the rendered maps). The other words aren't mapped out yet.
    /// </summary>
    public (ushort Terrain, ushort Behaviour, ushort Collision, ushort Flags) Tile(int x, int y)
    {
        var o = (y * Size + x) * 8;
        return (U16(Collision!, o), U16(Collision!, o + 2), U16(Collision!, o + 4), U16(Collision!, o + 6));
    }

    public bool IsBlocked(int x, int y) => Collision is null || (Tile(x, y).Flags & 1) != 0 || IsWater(x, y);

    public bool IsTallGrass(int x, int y) => Collision is not null && Tile(x, y).Flags == 0x24;

    /// <summary>Blocked for now; becomes surfable once Surf exists.</summary>
    public bool IsWater(int x, int y) => Collision is not null && IsWaterFlags(Tile(x, y).Flags);

    /// <summary>Packed tile byte for the client: bit 0 blocked, bit 1 tall grass, bit 2 water.</summary>
    public byte TileFlags(int x, int y) =>
        (byte)((IsBlocked(x, y) ? 1 : 0) | (IsTallGrass(x, y) ? 2 : 0) | (IsWater(x, y) ? 4 : 0));
}

/// <param name="Rotation">Around Y, 0x10000 = full turn.</param>
/// <param name="Id">Building ID, matched against the area's building pack.</param>
public sealed record Gen5Building(float X, float Y, float Z, int Rotation, int Id)
{
    public static List<Gen5Building> ParseList(byte[] s)
    {
        var count = (int)U32(s, 0);
        var list = new List<Gen5Building>();
        for (var i = 0; i < count && 4 + i * 16 + 16 <= s.Length; i++)
        {
            var o = 4 + i * 16;
            // The ID is stored big-endian, unlike everything else in the file.
            list.Add(new Gen5Building(Fx32(s, o), Fx32(s, o + 4), Fx32(s, o + 8), U16(s, o + 12), (s[o + 14] << 8) | s[o + 15]));
        }
        return list;
    }
}
