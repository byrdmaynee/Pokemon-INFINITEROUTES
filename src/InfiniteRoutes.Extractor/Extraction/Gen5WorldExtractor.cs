using System.Numerics;
using System.Text.Json;
using InfiniteRoutes.Extractor.Gen5;
using InfiniteRoutes.Extractor.Graphics;
using InfiniteRoutes.Extractor.Nds;
using InfiniteRoutes.Extractor.Nitro3D;
using static InfiniteRoutes.Extractor.Nitro3D.NitroDict;

namespace InfiniteRoutes.Extractor.Extraction;

/// <summary>
/// One matrix cell of an exported map, in tile units (1 tile = 1 unit, 32 per chunk). Zone is -1 when the map has
/// no per-cell zones. <see cref="Seasons"/> is 4 when the cell has a model per season (area + season), else 1.
/// </summary>
/// <see cref="Animation"/> is the map texture animation (<c>anims/{n}.json</c>) for its materials, or -1.
public sealed record WorldCell(int X, int Y, int Chunk, int Zone, string Location, int Area, int Seasons = 1, int Animation = -1)
{
    /// <summary>The area whose model to show in <paramref name="season"/> (0 spring … 3 winter).</summary>
    public int AreaFor(int season) => season > 0 && season < Seasons ? Area + season : Area;
}

public enum Season { Spring, Summer, Autumn, Winter }

/// <summary>An exported map (one map matrix): the overworld, or an interior, cave, etc.</summary>
public sealed record WorldIndex(string Key, int Width, int Height, int ChunkTiles, IReadOnlyList<WorldCell> Cells);

public enum WarpKind { Other, Mat, Stairs, Door, Edge }

/// <summary>A warp in its zone's map, in whole tiles. <see cref="Facing"/> is how the player faces on arriving here.</summary>
public sealed record WarpInfo(int X, int Y, int Width, int Height, int TargetZone, int TargetWarp, Facing? Facing, WarpKind Kind)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + Math.Max(1, Width) && y < Y + Math.Max(1, Height);
}

/// <summary>
/// An NPC or overworld object on its zone's map, in whole tiles. <see cref="Flag"/> is the story flag that
/// hides it (0 = always there); <see cref="Movement"/> is the ROM's movement code (0 still, 2 looks around,
/// 3 wanders within <see cref="RangeX"/>/<see cref="RangeY"/> of home, others scripted).
/// </summary>
public sealed record NpcInfo(int Id, int Sprite, int X, int Y, float Height, Facing Facing, int Movement,
    int RangeX, int RangeY, int Script, int Flag, int Sight = 0)
{
    /// <summary>The trainer this NPC battles as (its script is <see cref="Gen5Layout.TrainerScriptBase"/> + id), or -1.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int TrainerId => Script >= Gen5Layout.TrainerScriptBase && Script < Gen5Layout.TrainerScriptBase + 1000
        ? Script - Gen5Layout.TrainerScriptBase : -1;
}

/// <summary>An interactable tile (furniture event) and the script it runs, e.g. <see cref="Gen5Layout.PcScript"/>.</summary>
public sealed record FurnitureInfo(int X, int Y, int Script);

/// <summary>
/// A zone (named area with its own scripts, warps and NPCs) and the map it lives on. <see cref="Encounters"/> is its
/// wild encounter table (<c>data/encounters.json</c> id), or -1.
/// </summary>
public sealed record ZoneInfo(int Id, string Map, string Location, int Parent, IReadOnlyList<WarpInfo> Warps, IReadOnlyList<NpcInfo> Npcs,
    int Encounters = -1, IReadOnlyList<FurnitureInfo>? Furniture = null);

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
            var file = eventsFile < events.Count ? events.Read(eventsFile) : [];
            var encounters = ctx.ZoneField(z, Gen5Layout.Zone.Encounters);
            zones.Add(new ZoneInfo(z, key, Location(z), ctx.ZoneField(z, Gen5Layout.Zone.Parent), ReadWarps(file), ReadNpcs(file),
                encounters == 0xFFFF ? -1 : encounters, ReadFurniture(file)));
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

    static List<FurnitureInfo> ReadFurniture(byte[] file)
    {
        try
        {
            return Gen5Events.ParseFurniture(file).Where(f => f.OnGrid).Select(f => new FurnitureInfo(f.X, f.Y, f.Script)).ToList();
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    static List<NpcInfo> ReadNpcs(byte[] file)
    {
        try
        {
            // Rail-placed NPCs (coordinate type 1) need rail support first.
            return Gen5Events.ParseNpcs(file).Where(n => n.OnGrid).Select(n => new NpcInfo(n.Id, n.Sprite, n.X, n.Y, n.HeightTiles,
                n.Direction switch { 0 => Facing.Up, 2 => Facing.Left, 3 => Facing.Right, _ => Facing.Down },
                n.Movement, n.RangeX, n.RangeY, n.Script, n.Flag, n.Sight)).ToList();
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    /// <summary>
    /// Writes a <see cref="CharacterSheet"/> per sprite id the zones' NPCs use, as <c>{id}.png</c> in <paramref name="outDir"/>.
    /// Sprites that are 3D models rather than frame sets are skipped.
    /// </summary>
    public static int ExportNpcSprites(NitroFs fs, IEnumerable<ZoneInfo> zones, string outDir)
    {
        var table = Gen5OverworldTable.Parse(Narc.Parse(fs.ReadFile(Gen5Layout.OverworldTable)).Read(0));
        var sprites = Narc.Parse(fs.ReadFile(Gen5Layout.OverworldSprites));
        Directory.CreateDirectory(outDir);
        var written = 0;
        foreach (var id in zones.SelectMany(z => z.Npcs).Select(n => n.Sprite).Distinct())
        {
            if (!table.TryGetValue(id, out var member) || member >= sprites.Count)
                continue;
            var data = Lz.DecompressIfNeeded(sprites.Read(member));
            if (data.Length < 4 || data[0] != 'B' || data[1] != 'T' || data[2] != 'X' || Tex0.FromFile(data) is not { } tex
                || CharacterSheet.FromGen5Npc(tex) is not { } sheet)
                continue;
            Png.Save(sheet, Path.Combine(outDir, $"{id}.png"));
            written++;
        }
        return written;
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
            var seasons = ctx.IsSeasonal(area) && Enumerable.Range(1, Gen5Layout.Seasons - 1).All(s => ctx.ExportChunk((int)chunkIndex, area + s))
                ? Gen5Layout.Seasons : 1;

            var location = zone >= 0 ? ctx.Zones[zone * Gen5Layout.ZoneHeaderSize + Gen5Layout.Zone.LocationName] : -1;
            cells.Add(new WorldCell(i % width, i / width, (int)chunkIndex, zone,
                location >= 0 && location < locationNames.Count ? locationNames[location] : "", area, seasons, ctx.AnimationFor(area)));
        }
        return new WorldIndex(MapKey(matrixIndex, fixedArea), width, height, Gen5Map.Tiles, cells);
    }

    public static string ChunkFileName(int chunk, int area) => $"{chunk:D3}-a{area:D3}";

    public static bool IsSeasonal(ReadOnlySpan<byte> areas, int area)
    {
        if ((area + Gen5Layout.Seasons) * Gen5Layout.AreaSize > areas.Length)
            return false;
        var group = areas[area * Gen5Layout.AreaSize + Gen5Layout.AreaSeasonGroup];
        if (group % Gen5Layout.Seasons != 0 || areas[area * Gen5Layout.AreaSize + Gen5Layout.AreaOutdoorFlag] == 0)
            return false;
        for (var s = 1; s < Gen5Layout.Seasons; s++)
            if (areas[(area + s) * Gen5Layout.AreaSize + Gen5Layout.AreaSeasonGroup] != group + s)
                return false;
        return true;
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
        readonly Narc _srtAnimations;
        readonly Dictionary<int, bool> _animationsWritten = new();

        public Context(NitroFs fs, string outDir)
        {
            Zones = Narc.Parse(fs.ReadFile(Gen5Layout.ZoneHeaders)).Read(0);
            Areas = fs.ReadFile(Gen5Layout.AreaTable);
            Matrices = Narc.Parse(fs.ReadFile(Gen5Layout.MapMatrices));
            Chunks = Narc.Parse(fs.ReadFile(Gen5Layout.MapChunks));
            _textureSets = Narc.Parse(fs.ReadFile(Gen5Layout.MapTextures));
            _srtAnimations = Narc.Parse(fs.ReadFile(Gen5Layout.MapSrtAnimations));
            _outdoor = (Narc.Parse(fs.ReadFile(Gen5Layout.BuildingPacks)), Narc.Parse(fs.ReadFile(Gen5Layout.BuildingTextures)));
            _indoor = (Narc.Parse(fs.ReadFile(Gen5Layout.IndoorBuildingPacks)), Narc.Parse(fs.ReadFile(Gen5Layout.IndoorBuildingTextures)));
            _outDir = outDir;
            _chunkDir = Directory.CreateDirectory(Path.Combine(outDir, "chunks")).FullName;
        }

        public int ZoneField(int zone, int offset) => U16(Zones, zone * Gen5Layout.ZoneHeaderSize + offset);

        public bool MatrixHasZones(int matrix) => U32(Matrices.Read(matrix), 0) != 0;

        /// <summary>True for the spring entry of a four-season group (see <see cref="Gen5Layout.AreaSeasonGroup"/>).</summary>
        public bool IsSeasonal(int area) => Gen5WorldExtractor.IsSeasonal(Areas, area);

        /// <summary>The area's SRT animation index (exported on first use), or -1 for none.</summary>
        public int AnimationFor(int area)
        {
            var index = Areas[area * Gen5Layout.AreaSize + Gen5Layout.AreaAnimations];
            if (index == 0xFF || index >= _srtAnimations.Count)
                return -1;
            if (_animationsWritten.TryGetValue(index, out var ok))
                return ok ? index : -1;
            try
            {
                var anims = Nsbta.Parse(Lz.DecompressIfNeeded(_srtAnimations.Read(index)));
                var dir = Directory.CreateDirectory(Path.Combine(_outDir, "anims")).FullName;
                File.WriteAllText(Path.Combine(dir, $"{index}.json"), JsonSerializer.Serialize(anims, Json.Compact));
                ok = true;
            }
            catch (InvalidDataException)
            {
                ok = false;
            }
            _animationsWritten[index] = ok;
            return ok ? index : -1;
        }

        /// <summary>The spring area of the season group <paramref name="area"/> belongs to, or the area itself.</summary>
        int SpringArea(int area)
        {
            var offset = Areas[area * Gen5Layout.AreaSize + Gen5Layout.AreaSeasonGroup] % Gen5Layout.Seasons;
            return offset > 0 && area - offset >= 0 && IsSeasonal(area - offset) ? area - offset : area;
        }

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
            Tex0? PackTextures(int pack)
            {
                if (!_buildingTexCache.TryGetValue((indoor, pack), out var t))
                    _buildingTexCache[(indoor, pack)] = t = pack < packTextures.Count ? Tex0.FromFile(packTextures.Read(pack)) : null;
                return t;
            }

            var map = Gen5Map.Parse(Chunks.Read(chunkIndex));
            var terrain = Nsbmd.Decode(map.Model);
            var parts = new List<GltfPart> { new(terrain, Matrix4x4.CreateScale(UnitScale)) };

            // A summer/autumn/winter pack only holds the buildings that change with the season; the rest come
            // from the spring area's pack. Each building keeps its own pack's textures.
            var packOrder = SpringArea(area) is var spring && spring != area ? new[] { U16(Areas, spring * Gen5Layout.AreaSize), packIndex } : [packIndex];
            var buildings = new Dictionary<int, (NitroModel Model, int Pack)>();
            foreach (var pack in packOrder)
            foreach (var (id, m) in BuildingModels(indoor, packs, pack))
                buildings[id] = (m, pack);
            var materialPack = new Dictionary<NitroMaterial, int>();
            foreach (var bld in map.Buildings)
            {
                if (!buildings.TryGetValue(bld.Id, out var entry))
                    continue;
                var (model, modelPack) = entry;
                foreach (var m in model.Materials)
                    materialPack[m] = modelPack;
                // Building records store Z north-positive, unlike the terrain and collision grid (rows run south).
                var world = Matrix4x4.CreateRotationY(bld.Rotation / 65536f * MathF.Tau)
                            * Matrix4x4.CreateTranslation(bld.X, bld.Y, -bld.Z)
                            * Matrix4x4.CreateScale(UnitScale);
                parts.Add(new GltfPart(model, world));
            }

            // Terrain materials use the area's texture set; building materials use their pack's set.
            var terrainMaterials = terrain.Materials.ToHashSet();
            (Tex0? Tex, string Folder) SourceFor(NitroMaterial m) =>
                terrainMaterials.Contains(m) ? (tex, $"set{texSet:D3}")
                : materialPack.GetValueOrDefault(m, packIndex) is var pk ? (PackTextures(pk), $"{(indoor ? "ind" : "bld")}{pk:D3}") : default;
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
