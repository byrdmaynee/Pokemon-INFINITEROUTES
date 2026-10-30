using System.Numerics;
using System.Text.Json;
using InfiniteRoutes.Extractor.Gen5;
using InfiniteRoutes.Extractor.Graphics;
using InfiniteRoutes.Extractor.Nds;
using InfiniteRoutes.Extractor.Nitro3D;
using static InfiniteRoutes.Extractor.Nitro3D.NitroDict;

namespace InfiniteRoutes.Extractor.Extraction;

/// <summary>One matrix cell of an exported map, in tile units (1 tile = 1 unit, 32 per chunk). Zone is -1 when the map has no per-cell zones.</summary>
public sealed record WorldCell(int X, int Y, int Chunk, int Zone, string Location, int Area);

/// <summary>An exported map (one map matrix): the overworld, or an interior, cave, etc.</summary>
public sealed record WorldIndex(string Key, int Width, int Height, int ChunkTiles, IReadOnlyList<WorldCell> Cells);

public enum WarpKind { Other, Mat, Stairs, Door, Edge }

/// <summary>A warp in its zone's map, in whole tiles. <see cref="Facing"/> is how the player faces on arriving here.</summary>
public sealed record WarpInfo(int X, int Y, int Width, int Height, int TargetZone, int TargetWarp, Facing? Facing, WarpKind Kind)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + Math.Max(1, Width) && y < Y + Math.Max(1, Height);
}

/// <summary>A zone (named area with its own scripts and warps) and the map it lives on.</summary>
public sealed record ZoneInfo(int Id, string Map, string Location, int Parent, IReadOnlyList<WarpInfo> Warps);

public sealed record WorldData(IReadOnlyList<WorldIndex> Maps, IReadOnlyList<ZoneInfo> Zones);

/// <summary>
/// Exports every map a zone uses: per matrix cell a glTF (terrain + buildings, chunk-local, centred on the
/// origin) and a 32×32 collision grid, plus map indices and a zone table with warps. Textures are decoded
/// once per texture set.
/// </summary>
public static class Gen5WorldExtractor
{
    const float UnitScale = 1f / 16;   // 16 model units per tile

    public const string OverworldKey = "m000";

    public static string MapKey(int matrix, int area) => area < 0 ? $"m{matrix:D3}" : $"m{matrix:D3}-a{area:D3}";

    public static WorldData Export(NitroFs fs, IReadOnlyList<string> locationNames, string outDir,
        IProgress<ExtractionProgress>? progress, double from, double span, CancellationToken ct)
    {
        var ctx = new Context(fs, outDir);
        var zoneCount = ctx.Zones.Length / Gen5Layout.ZoneHeaderSize;
        var events = Narc.Parse(fs.ReadFile(Gen5Layout.ZoneEvents));

        string Location(int zone) =>
            zone >= 0 && zone < zoneCount && ctx.Zones[zone * Gen5Layout.ZoneHeaderSize + Gen5Layout.Zone.LocationName] is var n && n < locationNames.Count
                ? locationNames[n] : "";

        // Each zone's map: matrices with per-cell zones (the overworld, some dungeons) are shared by every zone
        // on them; the rest (interiors) take their textures from the zone's area, so key by matrix + area.
        var zoneMaps = new Dictionary<int, (int Matrix, int Area)>();
        for (var z = 0; z < zoneCount; z++)
        {
            int matrix = ctx.ZoneField(z, Gen5Layout.Zone.Matrix), area = ctx.ZoneField(z, Gen5Layout.Zone.Area);
            if (matrix >= ctx.Matrices.Count)
                continue;
            zoneMaps[z] = (matrix, ctx.MatrixHasZones(matrix) ? -1 : area);
        }

        var maps = new List<WorldIndex>();
        var todo = zoneMaps.Values.Distinct().OrderBy(m => m.Matrix).ThenBy(m => m.Area).ToList();
        for (var n = 0; n < todo.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new($"Maps ({n}/{todo.Count})", from + span * n / todo.Count));
            var (matrix, area) = todo[n];
            var index = ExportMap(ctx, matrix, area, locationNames, ct);
            if (index.Cells.Count > 0)
                maps.Add(index);
        }
        var exported = maps.Select(m => m.Key).ToHashSet();

        var zones = new List<ZoneInfo>();
        foreach (var (z, (matrix, area)) in zoneMaps)
        {
            var key = MapKey(matrix, area);
            if (!exported.Contains(key))
                continue;
            var eventsFile = ctx.ZoneField(z, Gen5Layout.Zone.Events);
            var warps = eventsFile < events.Count ? ReadWarps(events.Read(eventsFile)) : [];
            zones.Add(new ZoneInfo(z, key, Location(z), ctx.ZoneField(z, Gen5Layout.Zone.Parent), warps));
        }

        foreach (var map in maps)
            File.WriteAllText(Path.Combine(outDir, map.Key + ".json"), JsonSerializer.Serialize(map, Json.Indented));
        File.WriteAllText(Path.Combine(outDir, "zones.json"), JsonSerializer.Serialize(zones, Json.Indented));
        return new WorldData(maps, zones);
    }

    static List<WarpInfo> ReadWarps(byte[] file)
    {
        List<Gen5Warp> warps;
        try
        {
            warps = Gen5Events.ParseWarps(file);
        }
        catch (InvalidDataException)
        {
            return [];
        }
        // Keep list positions: other zones refer to warps by index.
        return warps.Select(w => new WarpInfo(w.TileX, w.TileY, w.Width, w.Depth, w.OnGrid ? w.TargetZone : -1, w.TargetWarp,
            w.Direction switch { 1 => Facing.Up, 2 => Facing.Down, 3 => Facing.Left, 4 => Facing.Right, _ => null },
            w.Transition switch { 1 => WarpKind.Mat, 2 => WarpKind.Stairs, 3 => WarpKind.Door, 4 => WarpKind.Edge, _ => WarpKind.Other })).ToList();
    }

    /// <param name="fixedArea">Area for every cell, or -1 to take it from each cell's zone.</param>
    static WorldIndex ExportMap(Context ctx, int matrixIndex, int fixedArea, IReadOnlyList<string> locationNames, CancellationToken ct)
    {
        var matrix = ctx.Matrices.Read(matrixIndex);
        var hasZones = U32(matrix, 0) != 0;
        int width = U16(matrix, 4), height = U16(matrix, 6);
        var cellCount = width * height;
        var zoneCount = ctx.Zones.Length / Gen5Layout.ZoneHeaderSize;
        var cells = new List<WorldCell>();

        int ZoneAt(int i) => hasZones && U32(matrix, 8 + (cellCount + i) * 4) is var z && z < zoneCount ? (int)z : -1;
        for (var i = 0; i < cellCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var chunkIndex = U32(matrix, 8 + i * 4);
            if (chunkIndex >= ctx.Chunks.Count)
                continue;

            var zone = ZoneAt(i);
            int area;
            if (fixedArea >= 0)
                area = fixedArea;
            else
            {
                // Scenery cells (sea, cliffs) have no zone; texture them like the nearest cell that does.
                var styleZone = zone >= 0 ? zone : NearestZone(i % width, i / width, width, height, ZoneAt);
                area = ctx.ZoneField(Math.Max(styleZone, 0), Gen5Layout.Zone.Area);
            }
            if (!ctx.ExportChunk((int)chunkIndex, area))
                continue;

            var location = zone >= 0 ? ctx.Zones[zone * Gen5Layout.ZoneHeaderSize + Gen5Layout.Zone.LocationName] : -1;
            cells.Add(new WorldCell(i % width, i / width, (int)chunkIndex, zone,
                location >= 0 && location < locationNames.Count ? locationNames[location] : "", area));
        }
        return new WorldIndex(MapKey(matrixIndex, fixedArea), width, height, Gen5Map.Tiles, cells);
    }

    public static string ChunkFileName(int chunk, int area) => $"{chunk:D3}-a{area:D3}";

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

    /// <summary>Archives and caches shared by every map in one export.</summary>
    sealed class Context
    {
        public readonly byte[] Zones, Areas;
        public readonly Narc Matrices, Chunks;
        readonly Narc _textureSets;
        readonly (Narc Packs, Narc Textures) _outdoor, _indoor;
        readonly string _outDir, _chunkDir;
        readonly Dictionary<int, Tex0> _tex0Cache = new();
        readonly Dictionary<(bool, int), Tex0?> _buildingTexCache = new();
        readonly Dictionary<(bool, int), Dictionary<int, NitroModel>> _packCache = new();
        readonly Dictionary<(int, int), bool> _exported = new();
        readonly HashSet<int> _tilesWritten = new();

        public Context(NitroFs fs, string outDir)
        {
            Zones = Narc.Parse(fs.ReadFile(Gen5Layout.ZoneHeaders)).Read(0);
            Areas = fs.ReadFile(Gen5Layout.AreaTable);
            Matrices = Narc.Parse(fs.ReadFile(Gen5Layout.MapMatrices));
            Chunks = Narc.Parse(fs.ReadFile(Gen5Layout.MapChunks));
            _textureSets = Narc.Parse(fs.ReadFile(Gen5Layout.MapTextures));
            _outdoor = (Narc.Parse(fs.ReadFile(Gen5Layout.BuildingPacks)), Narc.Parse(fs.ReadFile(Gen5Layout.BuildingTextures)));
            _indoor = (Narc.Parse(fs.ReadFile(Gen5Layout.IndoorBuildingPacks)), Narc.Parse(fs.ReadFile(Gen5Layout.IndoorBuildingTextures)));
            _outDir = outDir;
            _chunkDir = Directory.CreateDirectory(Path.Combine(outDir, "chunks")).FullName;
        }

        public int ZoneField(int zone, int offset) => U16(Zones, zone * Gen5Layout.ZoneHeaderSize + offset);

        public bool MatrixHasZones(int matrix) => U32(Matrices.Read(matrix), 0) != 0;

        /// <summary>Writes the chunk's glTF (for this area's textures) and collision; false if it can't be decoded.</summary>
        public bool ExportChunk(int chunkIndex, int area)
        {
            if (_exported.TryGetValue((chunkIndex, area), out var ok))
                return ok;
            try
            {
                Write(chunkIndex, area);
                ok = true;
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                ok = false;
            }
            return _exported[(chunkIndex, area)] = ok;
        }

        void Write(int chunkIndex, int area)
        {
            var a = area * Gen5Layout.AreaSize;
            if (a + Gen5Layout.AreaSize > Areas.Length)
                throw new InvalidDataException($"Area {area} is out of range.");
            int packIndex = U16(Areas, a);
            int texSet = U16(Areas, a + 2);
            var indoor = Areas[a + Gen5Layout.AreaOutdoorFlag] == 0;
            var (packs, packTextures) = indoor ? _indoor : _outdoor;

            if (!_tex0Cache.TryGetValue(texSet, out var tex))
                _tex0Cache[texSet] = tex = Tex0.FromFile(_textureSets.Read(texSet)) ?? throw new InvalidDataException($"Texture set {texSet} has no TEX0.");
            if (!_buildingTexCache.TryGetValue((indoor, packIndex), out var bldTex))
                _buildingTexCache[(indoor, packIndex)] = bldTex = packIndex < packTextures.Count ? Tex0.FromFile(packTextures.Read(packIndex)) : null;

            var map = Gen5Map.Parse(Chunks.Read(chunkIndex));
            var terrain = Nsbmd.Decode(map.Model);
            var parts = new List<GltfPart> { new(terrain, Matrix4x4.CreateScale(UnitScale)) };

            var buildings = BuildingModels(indoor, packs, packIndex);
            foreach (var bld in map.Buildings)
            {
                if (!buildings.TryGetValue(bld.Id, out var model))
                    continue;
                // Building records store Z north-positive, unlike the terrain and collision grid (rows run south).
                var world = Matrix4x4.CreateRotationY(bld.Rotation / 65536f * MathF.Tau)
                            * Matrix4x4.CreateTranslation(bld.X, bld.Y, -bld.Z)
                            * Matrix4x4.CreateScale(UnitScale);
                parts.Add(new GltfPart(model, world));
            }

            // Terrain materials use the area's texture set; building materials use their pack's set.
            var terrainMaterials = terrain.Materials.ToHashSet();
            (Tex0? Tex, string Folder) SourceFor(NitroMaterial m) =>
                terrainMaterials.Contains(m) ? (tex, $"set{texSet:D3}") : (bldTex, $"{(indoor ? "ind" : "bld")}{packIndex:D3}");
            NitroTexture? Find(NitroMaterial m) =>
                m.Texture is not null && SourceFor(m).Tex is { } t && t.Textures.TryGetValue(m.Texture, out var nt) ? nt : null;

            var gltf = Gltf.Build($"chunk{chunkIndex}", parts,
                m => SourceFor(m) is ({ } t, var folder) ? TextureUri(t, m, folder) : null,
                m => Find(m) is { } t ? (t.Width, t.Height) : (1, 1),
                m => Find(m)?.HasAlpha ?? false);
            Gltf.Save(gltf, Path.Combine(_chunkDir, ChunkFileName(chunkIndex, area) + ".gltf"));

            if (_tilesWritten.Add(chunkIndex))
            {
                var tiles = new byte[Gen5Map.Tiles * Gen5Map.Tiles];
                if (map.Collision is not null)
                    for (var ty = 0; ty < Gen5Map.Tiles; ty++)
                    for (var tx = 0; tx < Gen5Map.Tiles; tx++)
                        tiles[ty * Gen5Map.Tiles + tx] = map.TileFlags(tx, ty);
                File.WriteAllBytes(Path.Combine(_chunkDir, $"{chunkIndex:D3}.tiles"), tiles);
            }
        }

        /// <summary>Building ID → model for one pack: metadata entries [0, n) pair with models [n, 2n).</summary>
        Dictionary<int, NitroModel> BuildingModels(bool indoor, Narc packs, int packIndex)
        {
            if (_packCache.TryGetValue((indoor, packIndex), out var found))
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
            _packCache[(indoor, packIndex)] = result;
            return result;
        }

        string? TextureUri(Tex0 tex, NitroMaterial m, string folder)
        {
            if (m.Texture is null || !tex.Textures.TryGetValue(m.Texture, out var t))
                return null;
            var file = $"{m.Texture}__{m.Palette}.png";
            var path = Path.Combine(_outDir, "textures", folder, file);
            if (!File.Exists(path))
                Png.Save(tex.Decode(t, m.Palette), path);
            return $"../textures/{folder}/{file}";
        }
    }
}
