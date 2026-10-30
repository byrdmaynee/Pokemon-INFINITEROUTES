using System.Numerics;
using System.Text.Json;
using InfiniteRoutes.Extractor.Gen5;
using InfiniteRoutes.Extractor.Graphics;
using InfiniteRoutes.Extractor.Nds;
using InfiniteRoutes.Extractor.Nitro3D;
using static InfiniteRoutes.Extractor.Nitro3D.NitroDict;

namespace InfiniteRoutes.Extractor.Extraction;

/// <summary>One matrix cell of the exported world, in tile units (1 tile = 1 unit, 32 per chunk).</summary>
public sealed record WorldCell(int X, int Y, int Chunk, int Zone, string Location, int Area);

public sealed record WorldIndex(int Width, int Height, int ChunkTiles, IReadOnlyList<WorldCell> Cells);

/// <summary>
/// Exports the Unova overworld: per occupied matrix cell a glTF (terrain + buildings, chunk-local,
/// centred on the origin) and a 32×32 collision grid; textures are decoded once per texture set.
/// </summary>
public static class Gen5WorldExtractor
{
    const float UnitScale = 1f / 16;   // 16 model units per tile

    public static WorldIndex Export(NitroFs fs, IReadOnlyList<string> locationNames, string outDir,
        IProgress<ExtractionProgress>? progress, double from, double span, CancellationToken ct)
    {
        var zones = Narc.Parse(fs.ReadFile(Gen5Layout.ZoneHeaders)).Read(0);
        var areas = fs.ReadFile(Gen5Layout.AreaTable);
        var matrices = Narc.Parse(fs.ReadFile(Gen5Layout.MapMatrices));
        var chunks = Narc.Parse(fs.ReadFile(Gen5Layout.MapChunks));
        var textureSets = Narc.Parse(fs.ReadFile(Gen5Layout.MapTextures));
        var buildingPacks = Narc.Parse(fs.ReadFile(Gen5Layout.BuildingPacks));
        var buildingTextures = Narc.Parse(fs.ReadFile(Gen5Layout.BuildingTextures));
        var buildingTexCache = new Dictionary<int, Tex0?>();

        var matrix = matrices.Read(Gen5Layout.OverworldMatrix);
        var hasZones = U32(matrix, 0) != 0;
        int width = U16(matrix, 4), height = U16(matrix, 6);
        var cellCount = width * height;

        var chunkDir = Directory.CreateDirectory(Path.Combine(outDir, "chunks")).FullName;
        var tex0Cache = new Dictionary<int, Tex0>();
        var packCache = new Dictionary<int, Dictionary<int, NitroModel>>();
        var cells = new List<WorldCell>();

        var occupied = Enumerable.Range(0, cellCount).Where(i => U32(matrix, 8 + i * 4) != uint.MaxValue).ToList();
        int ZoneAt(int i) => hasZones && U32(matrix, 8 + (cellCount + i) * 4) is var z && z < zones.Length / Gen5Layout.ZoneHeaderSize ? (int)z : -1;
        for (var n = 0; n < occupied.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            var i = occupied[n];
            if (n % 8 == 0)
                progress?.Report(new($"Maps ({n}/{occupied.Count})", from + span * n / occupied.Count));

            var chunkIndex = (int)U32(matrix, 8 + i * 4);
            var zone = ZoneAt(i);
            // Scenery cells (sea, cliffs) have no zone; texture them like the nearest cell that does.
            var styleZone = zone >= 0 ? zone : NearestZone(i % width, i / width, width, height, ZoneAt);
            var zh = Math.Max(styleZone, 0) * Gen5Layout.ZoneHeaderSize;
            int area = U16(zones, zh + 2);
            int location = zone >= 0 ? zones[zh + 0x1A] : -1;
            int packIndex = U16(areas, area * 10);
            int texSet = U16(areas, area * 10 + 2);

            if (!tex0Cache.TryGetValue(texSet, out var tex))
                tex0Cache[texSet] = tex = Tex0.FromFile(textureSets.Read(texSet)) ?? throw new InvalidDataException($"Texture set {texSet} has no TEX0.");

            if (!buildingTexCache.TryGetValue(packIndex, out var bldTex))
                buildingTexCache[packIndex] = bldTex = packIndex < buildingTextures.Count ? Tex0.FromFile(buildingTextures.Read(packIndex)) : null;

            var map = Gen5Map.Parse(chunks.Read(chunkIndex));
            var terrain = Nsbmd.Decode(map.Model);
            var parts = new List<GltfPart> { new(terrain, Matrix4x4.CreateScale(UnitScale)) };

            var buildings = BuildingModels(buildingPacks, packIndex, packCache);
            foreach (var bld in map.Buildings)
            {
                if (!buildings.TryGetValue(bld.Id, out var model))
                    continue;
                var world = Matrix4x4.CreateRotationY(bld.Rotation / 65536f * MathF.Tau)
                            * Matrix4x4.CreateTranslation(bld.X, bld.Y, bld.Z)
                            * Matrix4x4.CreateScale(UnitScale);
                parts.Add(new GltfPart(model, world));
            }

            // Terrain materials use the area's texture set; building materials use their pack's set.
            var terrainMaterials = terrain.Materials.ToHashSet();
            (Tex0? Tex, string Folder) SourceFor(NitroMaterial m) =>
                terrainMaterials.Contains(m) ? (tex, $"set{texSet:D3}") : (bldTex, $"bld{packIndex:D3}");
            NitroTexture? Find(NitroMaterial m) =>
                m.Texture is not null && SourceFor(m).Tex is { } t && t.Textures.TryGetValue(m.Texture, out var nt) ? nt : null;

            var gltf = Gltf.Build($"chunk{chunkIndex}", parts,
                m => SourceFor(m) is ({ } t, var folder) ? TextureUri(t, m, outDir, folder) : null,
                m => Find(m) is { } t ? (t.Width, t.Height) : (1, 1),
                m => Find(m)?.HasAlpha ?? false);
            Gltf.Save(gltf, Path.Combine(chunkDir, $"{chunkIndex:D3}.gltf"));

            var tiles = new byte[Gen5Map.Tiles * Gen5Map.Tiles];
            for (var ty = 0; ty < Gen5Map.Tiles; ty++)
            for (var tx = 0; tx < Gen5Map.Tiles; tx++)
                tiles[ty * Gen5Map.Tiles + tx] = map.TileFlags(tx, ty);
            File.WriteAllBytes(Path.Combine(chunkDir, $"{chunkIndex:D3}.tiles"), tiles);

            cells.Add(new WorldCell(i % width, i / width, chunkIndex, zone,
                location >= 0 && location < locationNames.Count ? locationNames[location] : "", area));
        }

        var index = new WorldIndex(width, height, Gen5Map.Tiles, cells);
        File.WriteAllText(Path.Combine(outDir, "overworld.json"), JsonSerializer.Serialize(index, Json.Indented));
        return index;
    }

    static int NearestZone(int x, int y, int width, int height, Func<int, int> zoneAt)
    {
        for (var r = 1; r < Math.Max(width, height); r++)
        for (var dy = -r; dy <= r; dy++)
        for (var dx = -r; dx <= r; dx++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r)
                continue;
            int nx = x + dx, ny = y + dy;
            if (nx >= 0 && ny >= 0 && nx < width && ny < height && zoneAt(ny * width + nx) is var z and >= 0)
                return z;
        }
        return -1;
    }

    /// <summary>Building ID → model for one pack: metadata entries [0, n) pair with models [n, 2n).</summary>
    static Dictionary<int, NitroModel> BuildingModels(Narc packs, int packIndex, Dictionary<int, Dictionary<int, NitroModel>> cache)
    {
        if (cache.TryGetValue(packIndex, out var found))
            return found;
        var result = new Dictionary<int, NitroModel>();
        if (packIndex < packs.Count)
        {
            var pack = packs.Read(packIndex);
            int count = U16(pack, 2);
            var half = count / 2;
            for (var i = 0; i < half; i++)
            {
                var meta = (int)U32(pack, 4 + i * 4);
                var start = (int)U32(pack, 4 + (half + i) * 4);
                var end = (int)U32(pack, 4 + (half + i + 1) * 4);
                if (end <= start || !pack.AsSpan(start, 4).SequenceEqual("BMD0"u8))
                    continue;
                try
                {
                    result[U16(pack, meta)] = Nsbmd.Decode(pack[start..end]);
                }
                catch (InvalidDataException)
                {
                    // Skip a model we can't decode yet rather than losing the whole chunk.
                }
            }
        }
        cache[packIndex] = result;
        return result;
    }

    static string? TextureUri(Tex0 tex, NitroMaterial m, string outDir, string folder)
    {
        if (m.Texture is null || !tex.Textures.TryGetValue(m.Texture, out var t))
            return null;
        var file = $"{m.Texture}__{m.Palette}.png";
        var path = Path.Combine(outDir, "textures", folder, file);
        if (!File.Exists(path))
            Png.Save(tex.Decode(t, m.Palette), path);
        return $"../textures/{folder}/{file}";
    }
}
