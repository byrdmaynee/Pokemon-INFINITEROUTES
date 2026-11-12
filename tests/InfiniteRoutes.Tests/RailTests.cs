using System.Buffers.Binary;
using System.Numerics;
using InfiniteRoutes.Extractor.Gen5;

namespace InfiniteRoutes.Tests;

public class RailTests
{
    /// <summary>
    /// A rail file: points (x, z, half-width in 1/16 tile) joined by lines; a line with a centre is an arc around it.
    /// </summary>
    static byte[] BuildRails((float X, float Z, int HalfWidth)[] points, (int A, int B, Vector2? Centre)[] lines)
    {
        const int header = 28, pointSize = 112, lineSize = 72, shapeSize = 36;
        var p0 = header + 4;
        var l0 = p0 + points.Length * pointSize;
        var c0 = l0 + lines.Length * lineSize;
        var s0 = c0 + lines.Length * shapeSize;   // camera section: unused here, same size as the shapes
        var end = s0 + lines.Length * shapeSize;
        var d = new byte[end];
        d[0] = (byte)'R';
        d[1] = (byte)'L';
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(2), 5);
        foreach (var (i, o) in new[] { header, p0, l0, c0, s0, end }.Index())
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4 + i * 4), (uint)o);
        for (var i = 0; i < points.Length; i++)
        {
            var o = p0 + i * pointSize;
            var joined = lines.Index().Where(l => l.Item.A == i || l.Item.B == i).Select(l => l.Index).ToList();
            for (var k = 0; k < 4; k++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o + k * 4), k < joined.Count ? (uint)joined[k] : 0xFFFF);
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o + 32 + k * 4), (uint)points[i].HalfWidth);
            }
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(o + 48), (int)(points[i].X * 65536));
            BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(o + 56), (int)(points[i].Z * 65536));
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var o = l0 + i * lineSize;
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o), (uint)lines[i].A);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(o + 4), (uint)lines[i].B);
            if (lines[i].Centre is { } c)
            {
                var s = s0 + i * shapeSize;
                BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(s), 1);
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(s + 4), (int)(c.X * 65536));
                BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(s + 12), (int)(c.Y * 65536));
            }
        }
        return d;
    }

    [Fact]
    public void Straight_rails_have_a_length_a_side_and_a_width_on_the_grid()
    {
        // A street running east from (2.5, 5.5) to (12.5, 5.5), 1 tile either side.
        var rails = Gen5Rails.Parse(BuildRails([(2.5f, 5.5f, 16), (12.5f, 5.5f, 16)], [(0, 1, null)]));

        Assert.Equal(10, rails.Length(0), 3);
        Assert.Equal(new Vector2(6.5f, 5.5f), rails.At(0, 4));
        // Positive offsets are left of the direction of travel: north (up the screen) when heading east.
        Assert.Equal(new Vector2(6.5f, 3.5f), rails.Position(0, 4, 2));

        var mask = rails.Rasterize(0, 0, 16, 12);
        bool Open(int x, int y) => mask[y * 16 + x];
        Assert.True(Open(7, 5));
        Assert.True(Open(7, 4) && Open(7, 6));       // within the half-width (and the half-tile allowance)
        Assert.False(Open(7, 3) || Open(7, 8));     // beyond it
        Assert.False(Open(14, 5));                   // past the end
        Assert.False(Open(0, 5));
    }

    [Fact]
    public void Arcs_curve_around_their_centre_the_short_way()
    {
        // A quarter circle of radius 10 around (20, 20): from due east (30, 20) to due south (20, 30).
        var rails = Gen5Rails.Parse(BuildRails([(30, 20, 16), (20, 30, 16)], [(0, 1, new Vector2(20, 20))]));

        Assert.Equal(MathF.PI / 2 * 10, rails.Length(0), 2);
        var middle = rails.At(0, rails.Length(0) / 2);
        Assert.Equal(10, Vector2.Distance(middle, new Vector2(20, 20)), 2);
        Assert.True(middle.X > 26 && middle.Y > 26, $"{middle}");   // the south-east quarter, not the long way round

        // Every tile the arc covers is near the circle.
        var mask = rails.Rasterize(0, 0, 40, 40);
        for (var i = 0; i < mask.Length; i++)
            if (mask[i])
                Assert.InRange(Vector2.Distance(new Vector2(i % 40 + 0.5f, i / 40 + 0.5f), new Vector2(20, 20)), 8, 12);
        Assert.True(mask[(int)middle.Y * 40 + (int)middle.X]);
    }

    [Fact]
    public void Rasterizing_can_narrow_a_side_and_cover_tiles_left_of_the_map()
    {
        // Half-width 4; the north side (positive) narrowed to 1. The window starts at x = -8.
        var rails = Gen5Rails.Parse(BuildRails([(-4.5f, 10.5f, 64), (4.5f, 10.5f, 64)], [(0, 1, null)]));
        var mask = rails.Rasterize(-8, 0, 16, 20, _ => (1, float.MaxValue));
        bool Open(int x, int y) => mask[y * 16 + x + 8];

        Assert.True(Open(-3, 10));
        Assert.True(Open(0, 9) && !Open(0, 7));     // narrowed side
        Assert.True(Open(0, 14) && !Open(0, 16));   // full side
        var (x0, _, x1, _) = rails.Bounds();
        Assert.True(x0 < -4.5f && x1 > 4.5f);
    }

    [Fact]
    public void The_zone_to_rail_table_is_found_as_a_run_of_distinct_zones_ending_in_zero()
    {
        // Two rail table entries; the code holds (zone 28 → 0), (zone 249 → 1), then 0. Before it: other u16s that
        // look like entries (a zone repeated) must not be taken in.
        var code = new List<ushort> { 0x1234, 249, 28, 0x0000 | 28, 0x0800 | 249, 0, 0xFFFF };
        var bytes = code.SelectMany(v => new[] { (byte)v, (byte)(v >> 8) }).ToArray();
        byte[][] table = [[5, 0, 0xFF, 0xFF], [11, 0, 0xFF, 0xFF]];

        var rails = Gen5Rails.ZoneRails(bytes, 400, table);

        Assert.Equal(2, rails.Count);
        Assert.Equal(5, rails[28]);
        Assert.Equal(11, rails[249]);
    }

    [Fact]
    public void Map_types_cell_sizes_come_from_their_72_byte_records()
    {
        var code = new byte[8 + 3 * 72];
        foreach (var (k, size) in new uint[] { 0x200000, 0x800000, 0x400000 }.Index())
        {
            BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(8 + k * 72), size);
            BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(8 + k * 72 + 4), 0x400000);
        }
        Assert.Equal([32, 128, 64], Gen5Rails.BlockTiles([new byte[100], code], 3));
        Assert.Equal([32, 32], Gen5Rails.BlockTiles([new byte[100]], 2));   // not found: the usual size
    }

    [Fact]
    public void A_chunk_can_carry_a_64_by_64_collision_grid()
    {
        var model = new byte[16];
        "BMD0"u8.CopyTo(model);
        var collision = new byte[4 + 64 * 64 * 8];
        BinaryPrimitives.WriteUInt16LittleEndian(collision, 64);
        BinaryPrimitives.WriteUInt16LittleEndian(collision.AsSpan(2), 64);
        for (var i = 0; i < 64 * 64; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(collision.AsSpan(4 + i * 8 + 6), 0x80);
        BinaryPrimitives.WriteUInt16LittleEndian(collision.AsSpan(4 + (50 * 64 + 40) * 8 + 6), 0x81);
        var header = new byte[4 + 3 * 4];
        "WB"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)header.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)(header.Length + model.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)(header.Length + model.Length + collision.Length));

        var map = Gen5Map.Parse([.. header, .. model, .. collision]);

        Assert.Equal(64, map.Size);
        Assert.True(map.IsBlocked(40, 50));
        Assert.False(map.IsBlocked(50, 40));
    }
}
