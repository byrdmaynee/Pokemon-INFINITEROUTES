using System.Text.Json;

namespace InfiniteRoutes.Extractor.Extraction;

/// <summary>
/// One primitive of a chunk glTF as the extractor writes it: positions in tiles (centred on the cell, y up), texture
/// coordinates, vertex colours (RGBA) and triangle indices, with the glTF material index it uses.
/// </summary>
public sealed record ChunkPrimitive(int Material, string MaterialName, bool Opaque, float[] Positions, float[] Uvs, float[] Colors, int[] Indices);

/// <summary>A triangle cut to one tile: three vertices of position (cell-local tiles), UV and colour.</summary>
public readonly record struct TileTriangle(int Material, TileVertex A, TileVertex B, TileVertex C);

public readonly record struct TileVertex(float X, float Y, float Z, float U, float V, float R, float G, float B, float Alpha);

/// <summary>Reads chunk models (glTF with one embedded buffer) for Infinite Routes' terrain.</summary>
public static class ChunkGeometry
{
    public const float NoGround = -1e6f;

    public static List<ChunkPrimitive> Load(string gltfPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(gltfPath));
        var root = doc.RootElement;
        var uri = root.GetProperty("buffers")[0].GetProperty("uri").GetString()!;
        var buffer = Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]);
        var accessors = root.GetProperty("accessors");
        var views = root.GetProperty("bufferViews");
        var materials = root.TryGetProperty("materials", out var m) ? m : default;

        float[] Floats(int accessorIndex, int width)
        {
            var a = accessors[accessorIndex];
            var count = a.GetProperty("count").GetInt32();
            var offset = Offset(a);
            var type = a.GetProperty("componentType").GetInt32();
            var result = new float[count * width];
            if (type == 5126)
                Buffer.BlockCopy(buffer, offset, result, 0, result.Length * 4);
            else
            {
                var normalized = a.TryGetProperty("normalized", out var nz) && nz.GetBoolean();
                for (var i = 0; i < result.Length; i++)
                    result[i] = type switch
                    {
                        5121 => buffer[offset + i] / (normalized ? 255f : 1f),
                        5123 => BitConverter.ToUInt16(buffer, offset + i * 2) / (normalized ? 65535f : 1f),
                        _ => 0,
                    };
            }
            return result;
        }

        int Offset(JsonElement accessor) =>
            (views[accessor.GetProperty("bufferView").GetInt32()].TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0)
            + (accessor.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);

        var result = new List<ChunkPrimitive>();
        foreach (var mesh in root.GetProperty("meshes").EnumerateArray())
        foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
        {
            var materialIndex = prim.TryGetProperty("material", out var mi) ? mi.GetInt32() : -1;
            string name = "";
            var opaque = true;
            if (materialIndex >= 0 && materials.ValueKind == JsonValueKind.Array)
            {
                var material = materials[materialIndex];
                opaque = !material.TryGetProperty("alphaMode", out var alpha) || alpha.GetString() == "OPAQUE";
                name = material.TryGetProperty("name", out var nm) ? string.Intern(nm.GetString() ?? "") : "";
            }
            var attributes = prim.GetProperty("attributes");
            var positions = Floats(attributes.GetProperty("POSITION").GetInt32(), 3);
            var count = positions.Length / 3;
            var uvs = attributes.TryGetProperty("TEXCOORD_0", out var uv) ? Floats(uv.GetInt32(), 2) : new float[count * 2];
            float[] colors;
            if (attributes.TryGetProperty("COLOR_0", out var col))
            {
                var width = accessors[col.GetInt32()].GetProperty("type").GetString() == "VEC3" ? 3 : 4;
                var raw = Floats(col.GetInt32(), width);
                colors = width == 4 ? raw : Enumerable.Range(0, count).SelectMany(i => new[] { raw[i * 3], raw[i * 3 + 1], raw[i * 3 + 2], 1f }).ToArray();
            }
            else
                colors = Enumerable.Repeat(1f, count * 4).ToArray();
            int[] indices;
            if (prim.TryGetProperty("indices", out var ii))
            {
                var ia = accessors[ii.GetInt32()];
                int iCount = ia.GetProperty("count").GetInt32(), iOff = Offset(ia);
                var type = ia.GetProperty("componentType").GetInt32();
                indices = new int[iCount];
                for (var k = 0; k < iCount; k++)
                    indices[k] = type switch
                    {
                        5125 => (int)BitConverter.ToUInt32(buffer, iOff + k * 4),
                        5123 => BitConverter.ToUInt16(buffer, iOff + k * 2),
                        _ => buffer[iOff + k],
                    };
            }
            else
                indices = Enumerable.Range(0, count).ToArray();
            result.Add(new ChunkPrimitive(materialIndex, name, opaque, positions, uvs, colors, indices));
        }
        return result;
    }

    /// <summary>
    /// The top opaque surface over an s×s grid of points in each of the n×n tiles (as the client's ground raycast sees
    /// it): heights (below <see cref="NoGround"/> where there's nothing) and the material there. Indexed
    /// [(ty·s + sy)·n·s + tx·s + sx].
    /// </summary>
    /// <param name="opaqueOnly">False to count cut-out and blended surfaces too (tree canopies, fences): what's seen from above.</param>
    public static (float[] Height, string?[] Material) Surface(IReadOnlyList<ChunkPrimitive> prims, int n, int s, bool opaqueOnly = true)
    {
        var side = n * s;
        var h = new float[side * side];
        Array.Fill(h, float.MinValue);
        var mat = new string?[side * side];
        var half = n / 2f;
        foreach (var p in prims)
        {
            if (opaqueOnly && !p.Opaque)
                continue;
            var pos = p.Positions;
            for (var t = 0; t + 2 < p.Indices.Length; t += 3)
            {
                int i0 = p.Indices[t] * 3, i1 = p.Indices[t + 1] * 3, i2 = p.Indices[t + 2] * 3;
                float ax = (pos[i0] + half) * s, ay = pos[i0 + 1], az = (pos[i0 + 2] + half) * s;
                float bx = (pos[i1] + half) * s, by = pos[i1 + 1], bz = (pos[i1 + 2] + half) * s;
                float cx = (pos[i2] + half) * s, cy = pos[i2 + 1], cz = (pos[i2 + 2] + half) * s;
                var d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
                if (Math.Abs(d) < 1e-9f)
                    continue;
                int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)) - 0.5f)), x1 = Math.Min(side - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx)) - 0.5f));
                int z0 = Math.Max(0, (int)Math.Floor(Math.Min(az, Math.Min(bz, cz)) - 0.5f)), z1 = Math.Min(side - 1, (int)Math.Ceiling(Math.Max(az, Math.Max(bz, cz)) - 0.5f));
                for (var tz = z0; tz <= z1; tz++)
                for (var tx = x0; tx <= x1; tx++)
                {
                    float px = tx + 0.5f, pz = tz + 0.5f;
                    var l1 = ((bz - cz) * (px - cx) + (cx - bx) * (pz - cz)) / d;
                    var l2 = ((cz - az) * (px - cx) + (ax - cx) * (pz - cz)) / d;
                    var l3 = 1 - l1 - l2;
                    if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f)
                        continue;
                    var y = l1 * ay + l2 * by + l3 * cy;
                    var i = tz * side + tx;
                    if (y > h[i])
                    {
                        h[i] = y;
                        mat[i] = p.MaterialName;
                    }
                }
            }
        }
        for (var i = 0; i < h.Length; i++)
            if (h[i] == float.MinValue)
                h[i] = NoGround - 1;
        return (h, mat);
    }

    /// <summary>The tiles any triangle of <paramref name="prims"/> lies over (by each triangle's bounding box).</summary>
    public static IEnumerable<(int X, int Y)> Covered(IEnumerable<ChunkPrimitive> prims, int n)
    {
        var seen = new HashSet<(int, int)>();
        var half = n / 2f;
        foreach (var p in prims)
            for (var t = 0; t + 2 < p.Indices.Length; t += 3)
            {
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                for (var k = 0; k < 3; k++)
                {
                    var i = p.Indices[t + k] * 3;
                    minX = Math.Min(minX, p.Positions[i]);
                    maxX = Math.Max(maxX, p.Positions[i]);
                    minZ = Math.Min(minZ, p.Positions[i + 2]);
                    maxZ = Math.Max(maxZ, p.Positions[i + 2]);
                }
                for (var z = Math.Max(0, (int)Math.Floor(minZ + half)); z <= Math.Min(n - 1, (int)Math.Ceiling(maxZ + half) - 1); z++)
                    for (var x = Math.Max(0, (int)Math.Floor(minX + half)); x <= Math.Min(n - 1, (int)Math.Ceiling(maxX + half) - 1); x++)
                        seen.Add((x, z));
            }
        return seen;
    }

    /// <summary>
    /// Every triangle of a chunk cut along the tile grid (in x and z), so each of the n×n tiles gets the pieces that lie
    /// over it — its ground, and the parts of trees, cliffs and objects above it. Indexed ty·n + tx.
    /// </summary>
    public static List<TileTriangle>[] ClipToTiles(IReadOnlyList<ChunkPrimitive> prims, int n)
    {
        var tiles = new List<TileTriangle>[n * n];
        for (var i = 0; i < tiles.Length; i++)
            tiles[i] = [];
        var half = n / 2f;
        var poly = new List<TileVertex>(8);
        var next = new List<TileVertex>(8);
        foreach (var p in prims)
        {
            TileVertex V(int i) => new(p.Positions[i * 3], p.Positions[i * 3 + 1], p.Positions[i * 3 + 2], p.Uvs[i * 2], p.Uvs[i * 2 + 1],
                p.Colors[i * 4], p.Colors[i * 4 + 1], p.Colors[i * 4 + 2], p.Colors[i * 4 + 3]);
            for (var t = 0; t + 2 < p.Indices.Length; t += 3)
            {
                TileVertex a = V(p.Indices[t]), b = V(p.Indices[t + 1]), c = V(p.Indices[t + 2]);
                var minX = Math.Min(a.X, Math.Min(b.X, c.X)) + half;
                var maxX = Math.Max(a.X, Math.Max(b.X, c.X)) + half;
                var minZ = Math.Min(a.Z, Math.Min(b.Z, c.Z)) + half;
                var maxZ = Math.Max(a.Z, Math.Max(b.Z, c.Z)) + half;
                int tx0 = Math.Clamp((int)Math.Floor(minX), 0, n - 1), tx1 = Math.Clamp((int)Math.Ceiling(maxX) - 1, 0, n - 1);
                int tz0 = Math.Clamp((int)Math.Floor(minZ), 0, n - 1), tz1 = Math.Clamp((int)Math.Ceiling(maxZ) - 1, 0, n - 1);
                if (tx1 < tx0) tx1 = tx0;
                if (tz1 < tz0) tz1 = tz0;
                for (var tz = tz0; tz <= tz1; tz++)
                for (var tx = tx0; tx <= tx1; tx++)
                {
                    // Edge tiles of the chunk keep what hangs over the chunk's own edge.
                    float lo = tx == 0 ? float.NegativeInfinity : tx - half, hi = tx == n - 1 ? float.PositiveInfinity : tx + 1 - half;
                    float loZ = tz == 0 ? float.NegativeInfinity : tz - half, hiZ = tz == n - 1 ? float.PositiveInfinity : tz + 1 - half;
                    poly.Clear();
                    poly.Add(a);
                    poly.Add(b);
                    poly.Add(c);
                    Clip(poly, next, v => v.X - lo);
                    Clip(poly, next, v => hi - v.X);
                    Clip(poly, next, v => v.Z - loZ);
                    Clip(poly, next, v => hiZ - v.Z);
                    for (var k = 1; k + 1 < poly.Count; k++)
                        tiles[tz * n + tx].Add(new TileTriangle(p.Material, poly[0], poly[k], poly[k + 1]));
                }
            }
        }
        return tiles;
    }

    /// <summary>Sutherland–Hodgman: keeps the part of <paramref name="poly"/> where <paramref name="inside"/> ≥ 0.</summary>
    static void Clip(List<TileVertex> poly, List<TileVertex> scratch, Func<TileVertex, float> inside)
    {
        if (poly.Count == 0)
            return;
        scratch.Clear();
        for (var i = 0; i < poly.Count; i++)
        {
            TileVertex cur = poly[i], prev = poly[(i + poly.Count - 1) % poly.Count];
            float dc = inside(cur), dp = inside(prev);
            if (float.IsInfinity(dc) || float.IsInfinity(dp) || float.IsNaN(dc) || float.IsNaN(dp))
            {
                scratch.Add(cur);
                continue;
            }
            if (dc >= 0)
            {
                if (dp < 0)
                    scratch.Add(Lerp(prev, cur, dp / (dp - dc)));
                scratch.Add(cur);
            }
            else if (dp >= 0)
                scratch.Add(Lerp(prev, cur, dp / (dp - dc)));
        }
        poly.Clear();
        poly.AddRange(scratch);
    }

    static TileVertex Lerp(TileVertex a, TileVertex b, float t) => new(
        a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.U + (b.U - a.U) * t, a.V + (b.V - a.V) * t,
        a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.Alpha + (b.Alpha - a.Alpha) * t);
}
