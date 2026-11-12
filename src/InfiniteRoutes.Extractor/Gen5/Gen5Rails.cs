using System.Numerics;
using static InfiniteRoutes.Extractor.Nitro3D.NitroDict;

namespace InfiniteRoutes.Extractor.Gen5;

/// <summary>A rail point: where lines meet. <see cref="HalfWidths"/> pairs with <see cref="Lines"/> (in tiles).</summary>
public sealed record RailPoint(int[] Lines, float[] HalfWidths, Vector3 Position);

/// <summary>A rail line from point <see cref="A"/> to <see cref="B"/>: straight, or an arc around <see cref="Centre"/> (x, z).</summary>
public sealed record RailLine(int A, int B, Vector2? Centre);

/// <summary>
/// A Black/White rail file (<see cref="Gen5Layout.Rails"/>, magic "RL"): the paths the player follows in areas walked
/// on rails instead of the tile grid (Skyarrow Bridge, Castelia's ring road, Victory Road, the towers). Header: "RL",
/// u16 version, u32 offsets to six sections. Points are 112 bytes (u32 line[4], u32 key[4], u32 half-width[4] in
/// 1/16 tile, fx32 x/y/z in tiles, u32 index, name[48]); lines 72 bytes (u32 point A, point B, u32, u32 index,
/// u32 camera, u32 attribute, name[48]); section 4 has one 36-byte shape per line (u32 kind: 0 straight, otherwise
/// an arc around the fx32 centre that follows). Positions are in the map's tiles. See docs/RAILS.md.
/// </summary>
public sealed class Gen5Rails
{
    const int PointSize = 112, LineSize = 72, ShapeSize = 36;

    public required IReadOnlyList<RailPoint> Points { get; init; }
    public required IReadOnlyList<RailLine> Lines { get; init; }

    public static Gen5Rails Parse(byte[] d)
    {
        if (d.Length < 28 || d[0] != 'R' || d[1] != 'L')
            throw new InvalidDataException("Not a rail file.");
        var offsets = Enumerable.Range(0, 6).Select(i => (int)U32(d, 4 + i * 4)).ToArray();
        if (offsets.Any(o => o > d.Length))
            throw new InvalidDataException("Rail file sections point past its end.");

        var points = new List<RailPoint>();
        for (var o = offsets[1]; o + PointSize <= offsets[2]; o += PointSize)
        {
            var lines = Enumerable.Range(0, 4).Select(i => (int)U32(d, o + i * 4)).ToArray();
            var widths = Enumerable.Range(0, 4).Select(i => U32(d, o + 32 + i * 4) / 16f).ToArray();
            points.Add(new RailPoint(lines, widths, new Vector3(S32(d, o + 48), S32(d, o + 52), S32(d, o + 56)) / 65536f));
        }
        var result = new List<RailLine>();
        for (int o = offsets[2], i = 0; o + LineSize <= offsets[3]; o += LineSize, i++)
        {
            var shape = offsets[4] + i * ShapeSize;
            Vector2? centre = shape + ShapeSize <= offsets[5] && U32(d, shape) != 0
                ? new Vector2(S32(d, shape + 4), S32(d, shape + 12)) / 65536f
                : null;
            int a = (int)U32(d, o), b = (int)U32(d, o + 4);
            if (a >= points.Count || b >= points.Count)
                throw new InvalidDataException($"Rail line {i} joins a point that isn't there.");
            result.Add(new RailLine(a, b, centre));
        }
        return new Gen5Rails { Points = points, Lines = result };
    }

    static int S32(byte[] b, int o) => BitConverter.ToInt32(b, o);

    static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    /// <summary>The line's arc (start angle, sweep, start and end radius), or null for a straight line.</summary>
    (float Angle, float Sweep, float RadiusA, float RadiusB)? Arc(RailLine line)
    {
        if (line.Centre is not { } c)
            return null;
        Vector2 a = Flat(Points[line.A].Position) - c, b = Flat(Points[line.B].Position) - c;
        float angleA = MathF.Atan2(a.Y, a.X), sweep = MathF.Atan2(b.Y, b.X) - angleA;
        // The short way round.
        while (sweep > MathF.PI) sweep -= MathF.Tau;
        while (sweep < -MathF.PI) sweep += MathF.Tau;
        return (angleA, sweep, a.Length(), b.Length());
    }

    public float Length(int line)
    {
        var l = Lines[line];
        return Arc(l) is (_, var sweep, var ra, var rb)
            ? MathF.Abs(sweep) * (ra + rb) / 2
            : Vector2.Distance(Flat(Points[l.A].Position), Flat(Points[l.B].Position));
    }

    /// <summary>The point <paramref name="distance"/> tiles along the line from A (x, z).</summary>
    public Vector2 At(int line, float distance)
    {
        var l = Lines[line];
        var length = Length(line);
        var t = length > 0 ? Math.Clamp(distance / length, 0, 1) : 0;
        if (Arc(l) is (var angle, var sweep, var ra, var rb))
        {
            var theta = angle + sweep * t;
            return l.Centre!.Value + new Vector2(MathF.Cos(theta), MathF.Sin(theta)) * (ra + (rb - ra) * t);
        }
        return Vector2.Lerp(Flat(Points[l.A].Position), Flat(Points[l.B].Position), t);
    }

    /// <summary>Ground height (tiles) <paramref name="distance"/> along the line, between its points' heights.</summary>
    public float HeightAt(int line, float distance)
    {
        var l = Lines[line];
        var length = Length(line);
        var t = length > 0 ? Math.Clamp(distance / length, 0, 1) : 0;
        return Points[l.A].Position.Y + (Points[l.B].Position.Y - Points[l.A].Position.Y) * t;
    }

    /// <summary>Unit direction of travel (A → B) at <paramref name="distance"/>.</summary>
    public Vector2 Tangent(int line, float distance)
    {
        var length = Length(line);
        const float h = 0.05f;
        var d = Math.Clamp(distance, h, Math.Max(h, length - h));
        var v = At(line, Math.Min(length, d + h)) - At(line, Math.Max(0, d - h));
        return v.LengthSquared() > 0 ? Vector2.Normalize(v) : Vector2.UnitX;
    }

    /// <summary>The side a positive offset goes: left of the direction of travel (x right, z down).</summary>
    public Vector2 Normal(int line, float distance) => Tangent(line, distance) is var t ? new Vector2(t.Y, -t.X) : default;

    /// <summary>A rail position (line, distance along, sideways offset) on the map, in tiles (x, z).</summary>
    public Vector2 Position(int line, float distance, float side) => At(line, distance) + Normal(line, distance) * side;

    /// <summary>The line's half-width (tiles) at <paramref name="distance"/>, between its two points' widths.</summary>
    public float HalfWidth(int line, float distance)
    {
        var l = Lines[line];
        float WidthAt(RailPoint p) => Array.IndexOf(p.Lines, line) is var k and >= 0 ? p.HalfWidths[k] : 1;
        var length = Length(line);
        var t = length > 0 ? Math.Clamp(distance / length, 0, 1) : 0;
        return WidthAt(Points[l.A]) + (WidthAt(Points[l.B]) - WidthAt(Points[l.A])) * t;
    }

    /// <summary>The area (tiles: min x, min z, max x, max z) the rails and their widths cover.</summary>
    public (float X0, float Y0, float X1, float Y1) Bounds()
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (var line = 0; line < Lines.Count; line++)
        {
            var length = Length(line);
            for (var d = 0f; d <= length + 0.5f; d += 1)
            {
                var p = At(line, Math.Min(d, length));
                var w = HalfWidth(line, d) + 1;
                (x0, y0, x1, y1) = (Math.Min(x0, p.X - w), Math.Min(y0, p.Y - w), Math.Max(x1, p.X + w), Math.Max(y1, p.Y + w));
            }
        }
        return Lines.Count == 0 ? default : (x0, y0, x1, y1);
    }

    /// <summary>
    /// The tiles the rails cover in a <paramref name="width"/> × <paramref name="height"/> window whose top-left tile
    /// is (<paramref name="originX"/>, <paramref name="originY"/>): within each line's half-width of it (a tile counts
    /// when its centre is). <paramref name="limit"/> can narrow a line's sides (positive side, negative side) — rows
    /// of doors mark where a street's walkable part ends.
    /// </summary>
    public bool[] Rasterize(int originX, int originY, int width, int height, Func<int, (float Plus, float Minus)>? limit = null)
    {
        var mask = new bool[width * height];
        const float step = 0.25f, slack = 0.5f;
        for (var line = 0; line < Lines.Count; line++)
        {
            var length = Length(line);
            var (plus, minus) = limit?.Invoke(line) ?? (float.MaxValue, float.MaxValue);
            for (var d = 0f; d <= length + step / 2; d += step)
            {
                var p = At(line, d);
                Vector2 t = Tangent(line, d), n = new(t.Y, -t.X);
                var w = HalfWidth(line, d);
                float right = Math.Min(w, plus) + slack, left = Math.Min(w, minus) + slack;
                var reach = (int)MathF.Ceiling(Math.Max(right, left)) + 1;
                for (var ty = (int)MathF.Floor(p.Y) - reach; ty <= (int)MathF.Floor(p.Y) + reach; ty++)
                for (var tx = (int)MathF.Floor(p.X) - reach; tx <= (int)MathF.Floor(p.X) + reach; tx++)
                {
                    int mx = tx - originX, my = ty - originY;
                    if (mx < 0 || my < 0 || mx >= width || my >= height)
                        continue;
                    var v = new Vector2(tx + 0.5f, ty + 0.5f) - p;
                    float along = Vector2.Dot(v, t), side = Vector2.Dot(v, n);
                    if (MathF.Abs(along) <= step / 2 + slack && side <= right && side >= -left)
                        mask[my * width + mx] = true;
                }
            }
        }
        return mask;
    }

    /// <summary>
    /// Zone → rail file. The ARM9 holds a short table of u16s (zone in the low 11 bits, an index into
    /// <see cref="Gen5Layout.RailTable"/> in the high 5) ending in 0; each index's entry starts with the rail file.
    /// Found as the longest run, read back from a 0, of distinct zones whose indices cover the whole table.
    /// </summary>
    public static Dictionary<int, int> ZoneRails(byte[] arm9, int zoneCount, IReadOnlyList<byte[]> railTable)
    {
        var count = railTable.Count;
        List<(int Zone, int Index)> best = [];
        for (var end = 2; end + 2 <= arm9.Length; end += 2)
        {
            if (arm9[end] != 0 || arm9[end + 1] != 0)
                continue;
            var run = new List<(int, int)>();
            var zones = new HashSet<int>();
            for (var o = end - 2; o >= 0; o -= 2)
            {
                var v = arm9[o] | arm9[o + 1] << 8;
                int zone = v & 0x7FF, index = v >> 11;
                if (v == 0 || zone >= zoneCount || index >= count || !zones.Add(zone))
                    break;
                run.Add((zone, index));
            }
            if (run.Count > best.Count && run.Select(r => r.Item2).Distinct().Count() == count)
                best = run;
        }
        var result = new Dictionary<int, int>();
        foreach (var (zone, index) in best)
            if (railTable[index].Length >= 2)
                result[zone] = railTable[index][0] | railTable[index][1] << 8;
        return result;
    }

    /// <summary>
    /// Tiles per map-matrix cell for each map type (zone header byte 0). An overlay holds one 72-byte record per type
    /// starting with the cell size as fx32 (0x200000 = 512 units = 32 tiles; Skyarrow Bridge's type uses 128).
    /// </summary>
    public static int[] BlockTiles(IEnumerable<byte[]> overlays, int types)
    {
        const int stride = 72;
        static bool Size(uint v) => v is 0x200000 or 0x400000 or 0x800000;
        foreach (var code in overlays)
            for (var o = 0; o + types * stride <= code.Length; o += 4)
            {
                if (U32(code, o) != 0x200000)
                    continue;
                var ok = true;
                for (var k = 0; k < types && ok; k++)
                    ok = Size(U32(code, o + k * stride)) && Size(U32(code, o + k * stride + 4));
                if (ok)
                    return Enumerable.Range(0, types).Select(k => (int)(U32(code, o + k * stride) / 0x10000)).ToArray();
            }
        return Enumerable.Repeat(Gen5Map.Tiles, types).ToArray();
    }
}
