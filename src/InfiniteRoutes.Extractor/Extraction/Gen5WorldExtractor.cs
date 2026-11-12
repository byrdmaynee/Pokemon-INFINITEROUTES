using System.Numerics;
using System.Text.Json;
using InfiniteRoutes.Extractor.Gen5;
using InfiniteRoutes.Extractor.Graphics;
using InfiniteRoutes.Extractor.Nds;
using InfiniteRoutes.Extractor.Nitro3D;
using static InfiniteRoutes.Extractor.Nitro3D.NitroDict;

namespace InfiniteRoutes.Extractor.Extraction;

/// <summary>
/// One matrix cell of an exported map, in tile units (1 tile = 1 unit, <see cref="WorldIndex.ChunkTiles"/> per cell).
/// Zone is -1 when the map has no per-cell zones. <see cref="Seasons"/> is 4 when the cell has a model per season
/// (area + season), else 1.
/// </summary>
/// <see cref="Animation"/> is the map texture animation (<c>anims/{n}.json</c>) for its materials, or -1;
/// <see cref="Pattern"/> its frame-swap texture animation (<c>anims/pat{n}.json</c>), or -1.
/// <see cref="Tiles"/> names the cell's own collision (<c>chunks/{Tiles}.tiles</c>) when it doesn't use its chunk's —
/// rail maps, whose chunks have none, get a grid drawn from their rails.
public sealed record WorldCell(int X, int Y, int Chunk, int Zone, string Location, int Area, int Seasons = 1, int Animation = -1,
    int Pattern = -1, string? Tiles = null)
{
    /// <summary>The area whose model to show in <paramref name="season"/> (0 spring … 3 winter).</summary>
    public int AreaFor(int season) => season > 0 && season < Seasons ? Area + season : Area;
}

public enum Season { Spring, Summer, Autumn, Winter }

/// <summary>An exported map (one map matrix): the overworld, or an interior, cave, etc. <see cref="ChunkTiles"/> is the
/// cell size in tiles (32; larger for a few map types such as Skyarrow Bridge's).</summary>
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
    int RangeX, int RangeY, int Script, int Flag, int Sight = 0, NpcTalk? Talk = null)
{
    /// <summary>The trainer this NPC battles as (its script is <see cref="Gen5Layout.TrainerScriptBase"/> + id), or -1.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int TrainerId => Script >= Gen5Layout.TrainerScriptBase && Script < Gen5Layout.TrainerScriptBase + 1000
        ? Script - Gen5Layout.TrainerScriptBase : Talk?.Trainer ?? -1;
}

/// <summary>An interactable tile (furniture event: signs, machines) and the script it runs.</summary>
public sealed record FurnitureInfo(int X, int Y, int Script);

/// <summary>
/// A zone (named area with its own scripts, warps and NPCs) and the map it lives on. <see cref="Encounters"/> is its
/// wild encounter table (<c>data/encounters.json</c> id), or -1.
/// </summary>
public sealed record ZoneInfo(int Id, string Map, string Location, int Parent, IReadOnlyList<WarpInfo> Warps, IReadOnlyList<NpcInfo> Npcs,
    int Encounters = -1, IReadOnlyList<FurnitureInfo>? Furniture = null, int BattleScenery = 0);

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
        var scripts = Narc.Parse(fs.ReadFile(Gen5Scripts.Archive));
        var story = Narc.Parse(fs.ReadFile(Gen5Layout.StoryText));

        string Location(int zone) =>
            zone >= 0 && zone < zoneCount && ctx.Zones[zone * Gen5Layout.ZoneHeaderSize + Gen5Layout.Zone.LocationName] is var n && n < locationNames.Count
                ? locationNames[n] : "";

        // Cell size per map type, and the rails of the zones walked on rails.
        int MapType(int zone) => ctx.Zones[zone * Gen5Layout.ZoneHeaderSize + Gen5Layout.Zone.MapType];
        var blockTiles = Gen5Rails.BlockTiles(fs.ReadOverlays(), Enumerable.Range(0, zoneCount).Max(MapType) + 1);
        var rails = ReadRails(fs, zoneCount);

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
            var type = MapType(zoneMaps.First(z => z.Value == (matrix, area)).Key);
            var index = ExportMap(ctx, matrix, area, blockTiles[type], locationNames, ct);
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
            var zoneRails = rails.GetValueOrDefault(z);
            var npcs = ReadNpcs(file, zoneRails);
            int scriptFile = ctx.ZoneField(z, Gen5Layout.Zone.Scripts), textFile = ctx.ZoneField(z, Gen5Layout.Zone.Text);
            if (scriptFile < scripts.Count && textFile < story.Count && npcs.Any(n => n.TrainerId < 0))
            {
                var script = scripts.Read(scriptFile);
                var offsets = Gen5Scripts.Offsets(script);
                var text = Text.Gen5Text.Decode(story.Read(textFile));
                npcs = npcs.Select(n => n.TrainerId >= 0 ? n : n with { Talk = Gen5Scripts.Talk(script, offsets, n.Script, text) }).ToList();
            }
            zones.Add(new ZoneInfo(z, key, Location(z), ctx.ZoneField(z, Gen5Layout.Zone.Parent), ReadWarps(file, zoneRails), npcs,
                encounters == 0xFFFF ? -1 : encounters, ReadFurniture(file), ctx.Zones[z * Gen5Layout.ZoneHeaderSize + Gen5Layout.Zone.BattleScenery]));
        }

        for (var i = 0; i < maps.Count; i++)
        {
            var key = maps[i].Key;
            maps[i] = RailCollision(ctx, maps[i], zones.Where(z => z.Map == key && rails.ContainsKey(z.Id))
                .Select(z => (rails[z.Id], RailWarps(events, ctx, z.Id))).ToList());
        }
        foreach (var map in maps)
            File.WriteAllText(Path.Combine(outDir, map.Key + ".json"), JsonSerializer.Serialize(map, Json.Indented));
        File.WriteAllText(Path.Combine(outDir, "zones.json"), JsonSerializer.Serialize(zones, Json.Indented));
        return new WorldData(maps, zones);
    }

    static List<WarpInfo> ReadWarps(byte[] file, Gen5Rails? rails)
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
        return warps.Select(w => !w.OnGrid && rails is not null && RailWarp(w, rails) is { } onRail ? onRail
            : new WarpInfo(w.TileX, w.TileY, w.Width, w.Depth, w.OnGrid ? w.TargetZone : -1, w.TargetWarp,
                w.Direction switch { 1 => Facing.Up, 2 => Facing.Down, 3 => Facing.Left, 4 => Facing.Right, _ => null },
                w.Transition switch { 1 => WarpKind.Mat, 2 => WarpKind.Stairs, 3 => WarpKind.Door, 4 => WarpKind.Edge, _ => WarpKind.Other })).ToList();
    }

    /// <summary>
    /// Zone → its rails, for zones walked on rails (see <see cref="Gen5Rails.ZoneRails"/>). Empty if the tables
    /// can't be found.
    /// </summary>
    static Dictionary<int, Gen5Rails> ReadRails(NitroFs fs, int zoneCount)
    {
        var result = new Dictionary<int, Gen5Rails>();
        if (!fs.Exists(Gen5Layout.Rails) || !fs.Exists(Gen5Layout.RailTable))
            return result;
        var files = Narc.Parse(fs.ReadFile(Gen5Layout.Rails));
        var table = Narc.Parse(fs.ReadFile(Gen5Layout.RailTable));
        var entries = Enumerable.Range(0, table.Count).Select(table.Read).ToList();
        foreach (var (zone, file) in Gen5Rails.ZoneRails(fs.ReadArm9(), zoneCount, entries))
        {
            if (file >= files.Count)
                continue;
            try
            {
                result[zone] = Gen5Rails.Parse(files.Read(file));
            }
            catch (InvalidDataException)
            {
            }
        }
        return result;
    }

    /// <summary>The zone's rail-placed warps as the ROM stores them (for <see cref="RailCollision"/>).</summary>
    static List<Gen5Warp> RailWarps(Narc events, Context ctx, int zone)
    {
        var eventsFile = ctx.ZoneField(zone, Gen5Layout.Zone.Events);
        try
        {
            return eventsFile < events.Count ? Gen5Events.ParseWarps(events.Read(eventsFile)).Where(w => !w.OnGrid).ToList() : [];
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    /// <summary>A rail warp's sideways offset: the stored u16 is signed.</summary>
    static int Side(Gen5Warp w) => (short)w.Z;

    /// <summary>A door beside the rail (a building's entrance), rather than a crossing of it (an exit at a line's end).</summary>
    static bool IsRailDoor(Gen5Warp w) => Math.Abs(Side(w)) >= 2 && w.Depth <= 1;

    /// <summary>
    /// A rail warp (X = line, Height = distance along it, Z = sideways offset; Width runs along the line, Depth
    /// across) as the tiles it covers. Doors beside the rail are walked into; crossings are stood on (edges).
    /// </summary>
    static WarpInfo? RailWarp(Gen5Warp w, Gen5Rails rails)
    {
        var line = w.X;
        if (line >= rails.Lines.Count)
            return null;
        var tiles = new List<(int X, int Y)>();
        for (var i = 0; i < Math.Max(1, w.Width); i++)
        for (var j = 0; j < Math.Max(1, w.Depth); j++)
        {
            var p = rails.Position(line, w.Height + i, Side(w) + j);
            tiles.Add(((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y)));
        }
        int x0 = tiles.Min(t => t.X), y0 = tiles.Min(t => t.Y);
        var middle = w.Height + (Math.Max(1, w.Width) - 1) / 2f;
        var door = IsRailDoor(w);
        // Arriving: through a door, face away from it (towards the rail); at a crossing, face into the line.
        var way = door ? rails.Normal(line, middle) * -Math.Sign(Side(w))
            : rails.Tangent(line, middle) * (middle < rails.Length(line) / 2 ? 1 : -1);
        var facing = MathF.Abs(way.X) > MathF.Abs(way.Y) ? way.X > 0 ? Facing.Right : Facing.Left
            : way.Y > 0 ? Facing.Down : Facing.Up;
        return new WarpInfo(x0, y0, tiles.Max(t => t.X) - x0 + 1, tiles.Max(t => t.Y) - y0 + 1, w.TargetZone, w.TargetWarp, facing,
            door ? WarpKind.Door : WarpKind.Edge);
    }

    /// <summary>
    /// Collision for the rails of the zones on a map. The rails' widths are walkable, narrowed on a side lined with
    /// doors to stop just short of them; doors are solid (walked into) and crossings open. Cells whose chunks have no
    /// collision (rail-only maps) get a grid drawn from the rails alone. On maps that are part grid, part rail
    /// (Castelia's plaza, Village Bridge, Icirrus, the League) the rails run over the grid's solid tiles from the
    /// junction tiles where they start, so the rails open those (and water a rail bridges). Rails can also run past the matrix (the
    /// models overhang their cells — Castelia's ring road does), so the map gains model-less cells (chunk -1) there
    /// that only carry collision.
    /// </summary>
    static WorldIndex RailCollision(Context ctx, WorldIndex map, List<(Gen5Rails Rails, List<Gen5Warp> Warps)> zones)
    {
        if (zones.Count == 0)
            return map;
        var n = map.ChunkTiles;
        int cx0 = 0, cy0 = 0, cx1 = map.Width - 1, cy1 = map.Height - 1;
        foreach (var (rails, _) in zones)
        {
            var (bx0, by0, bx1, by1) = rails.Bounds();
            (cx0, cy0) = (Math.Min(cx0, (int)MathF.Floor(bx0 / n)), Math.Min(cy0, (int)MathF.Floor(by0 / n)));
            (cx1, cy1) = (Math.Max(cx1, (int)MathF.Floor(bx1 / n)), Math.Max(cy1, (int)MathF.Floor(by1 / n)));
        }
        int ox = cx0 * n, oy = cy0 * n, width = (cx1 - cx0 + 1) * n, height = (cy1 - cy0 + 1) * n;
        var walkable = new bool[width * height];
        var doors = new List<WarpInfo>();
        var crossings = new List<WarpInfo>();
        foreach (var (rails, warps) in zones)
        {
            (float, float) Limit(int line)
            {
                var sides = warps.Where(w => w.X == line && IsRailDoor(w)).Select(Side).ToList();
                // The farthest door on a side is the building line; nearer ones are kiosks and alleys within the street.
                return (sides.Where(s => s > 0).Select(s => s - 1f).DefaultIfEmpty(float.MaxValue).Max(),
                    sides.Where(s => s < 0).Select(s => -s - 1f).DefaultIfEmpty(float.MaxValue).Max());
            }
            var mask = rails.Rasterize(ox, oy, width, height, Limit);
            for (var i = 0; i < mask.Length; i++)
                walkable[i] |= mask[i];
            foreach (var w in warps)
                if (RailWarp(w, rails) is { } info)
                    (info.Kind == WarpKind.Door ? doors : crossings).Add(info);
        }
        void Set(WarpInfo w, bool open)
        {
            for (var y = w.Y - oy; y < w.Y - oy + w.Height; y++)
            for (var x = w.X - ox; x < w.X - ox + w.Width; x++)
                if (x >= 0 && y >= 0 && x < width && y < height)
                    walkable[y * width + x] = open;
        }
        crossings.ForEach(w => Set(w, true));
        doors.ForEach(w => Set(w, false));

        WorldCell? Draw(WorldCell c)
        {
            var grid = c.Chunk >= 0 && !ctx.NoCollision.Contains(c.Chunk) ? ctx.ReadChunkTiles(c.Chunk) : null;
            if (grid is not null && grid.Length != n * n)
                return c;
            var tiles = new byte[n * n];
            var changed = grid is null;
            for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var open = walkable[((c.Y - cy0) * n + y) * width + (c.X - cx0) * n + x];
                var i = y * n + x;
                if (grid is null)
                    tiles[i] = open ? (byte)0 : (byte)1;
                else
                {
                    // Bit 0 blocked, bit 2 water (see Gen5Map.TileFlags): a rail is a path, over water too (Village Bridge's deck).
                    tiles[i] = open ? (byte)(grid[i] & ~5) : grid[i];
                    changed |= tiles[i] != grid[i];
                }
            }
            if (c.Chunk < 0 && tiles.All(t => t != 0))
                return null;
            if (!changed)
                return c;
            var name = $"rail-{map.Key}-{c.X}-{c.Y}";
            ctx.WriteTiles(name, tiles);
            return c with { Tiles = name };
        }
        var cells = map.Cells.Select(c => Draw(c) ?? c).ToList();
        var have = cells.Select(c => (c.X, c.Y)).ToHashSet();
        var template = map.Cells[0];
        for (var cy = cy0; cy <= cy1; cy++)
        for (var cx = cx0; cx <= cx1; cx++)
            if (!have.Contains((cx, cy)) && Draw(template with { X = cx, Y = cy, Chunk = -1, Zone = -1, Location = "" }) is { } extra)
                cells.Add(extra);
        return map with { Cells = cells };
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

    static List<NpcInfo> ReadNpcs(byte[] file, Gen5Rails? rails)
    {
        try
        {
            // Rail-placed NPCs (coordinate type 1): X = line, Y = distance along it, the low half of Height = sideways offset.
            return Gen5Events.ParseNpcs(file).Where(n => n.OnGrid || rails is not null && n.X < rails.Lines.Count).Select(n =>
            {
                var (x, y, height) = (n.X, n.Y, n.HeightTiles);
                if (!n.OnGrid)
                {
                    var p = rails!.Position(n.X, n.Y, (short)(n.Height & 0xFFFF));
                    (x, y, height) = ((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), rails.HeightAt(n.X, n.Y));
                }
                return new NpcInfo(n.Id, n.Sprite, x, y, height,
                    n.Direction switch { 0 => Facing.Up, 2 => Facing.Left, 3 => Facing.Right, _ => Facing.Down },
                    n.Movement, n.RangeX, n.RangeY, n.Script, n.Flag, n.Sight);
            }).ToList();
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    /// <summary>
    /// Writes every battle scenery model (<see cref="Gen5Layout.BattleStages"/>) as <c>{name}.gltf</c> in
    /// <paramref name="outDir"/> — backdrops (batt_bg*) and platforms (batt_stage*) — with their own textures.
    /// </summary>
    public static void ExportBattleScenery(NitroFs fs, string outDir)
    {
        var narc = Narc.Parse(fs.ReadFile(Gen5Layout.BattleStages));
        var texDir = Directory.CreateDirectory(Path.Combine(outDir, "textures")).FullName;
        for (var i = 0; i < narc.Count; i++)
        {
            var bytes = Lz.DecompressIfNeeded(narc.Read(i));
            if (bytes.Length < 4 || !bytes.AsSpan(0, 4).SequenceEqual("BMD0"u8))
                continue;
            NitroModel model;
            try
            {
                model = Nsbmd.Decode(bytes);
            }
            catch (InvalidDataException)
            {
                continue;
            }
            if (!model.Name.StartsWith("batt_", StringComparison.Ordinal) || model.Textures is not { } tex)
                continue;
            string? Uri(NitroMaterial m)
            {
                if (m.Texture is null || !tex.Textures.TryGetValue(m.Texture, out var t))
                    return null;
                var file = $"{m.Texture}__{m.Palette}.png";
                var path = Path.Combine(texDir, file);
                if (!File.Exists(path))
                    Png.Save(tex.Decode(t, m.Palette), path);
                return "textures/" + file;
            }
            (int, int) Size(NitroMaterial m) => m.Texture is not null && tex.Textures.TryGetValue(m.Texture, out var t) ? (t.Width, t.Height) : (1, 1);
            bool Alpha(NitroMaterial m) => m.Texture is not null && tex.Textures.TryGetValue(m.Texture, out var t) && t.HasAlpha;
            Gltf.Save(Gltf.Build(model, 1f / 16, Vector3.Zero, Uri, Size, Alpha), Path.Combine(outDir, model.Name + ".gltf"));
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
    static WorldIndex ExportMap(Context ctx, int matrixIndex, int fixedArea, int chunkTiles, IReadOnlyList<string> locationNames, CancellationToken ct)
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
                location >= 0 && location < locationNames.Count ? locationNames[location] : "", area, seasons, ctx.AnimationFor(area),
                ctx.PatternFor(area)));
        }
        return new WorldIndex(MapKey(matrixIndex, fixedArea), width, height, chunkTiles, cells);
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
        /// <summary>Chunks without a collision grid (rail maps' "NG" chunks).</summary>
        public readonly HashSet<int> NoCollision = new();
        readonly Narc _srtAnimations;
        readonly Dictionary<int, bool> _animationsWritten = new();
        readonly Narc _patternAnimations;
        readonly Dictionary<int, bool> _patternsWritten = new();

        public Context(NitroFs fs, string outDir)
        {
            Zones = Narc.Parse(fs.ReadFile(Gen5Layout.ZoneHeaders)).Read(0);
            Areas = fs.ReadFile(Gen5Layout.AreaTable);
            Matrices = Narc.Parse(fs.ReadFile(Gen5Layout.MapMatrices));
            Chunks = Narc.Parse(fs.ReadFile(Gen5Layout.MapChunks));
            _textureSets = Narc.Parse(fs.ReadFile(Gen5Layout.MapTextures));
            _srtAnimations = Narc.Parse(fs.ReadFile(Gen5Layout.MapSrtAnimations));
            _patternAnimations = Narc.Parse(fs.ReadFile(Gen5Layout.MapPatternAnimations));
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

        /// <summary>The area's frame-swap texture animation index (exported on first use), or -1 for none.</summary>
        public int PatternFor(int area)
        {
            var index = Areas[area * Gen5Layout.AreaSize + Gen5Layout.AreaAnimations + 1];
            if (index == 0xFF || index >= _patternAnimations.Count)
                return -1;
            if (_patternsWritten.TryGetValue(index, out var ok))
                return ok ? index : -1;
            try
            {
                var dir = Directory.CreateDirectory(Path.Combine(_outDir, "anims")).FullName;
                var patterns = Gen5TexturePatterns.Parse(Lz.DecompressIfNeeded(_patternAnimations.Read(index)), Path.Combine(dir, $"pat{index}"));
                File.WriteAllText(Path.Combine(dir, $"pat{index}.json"), JsonSerializer.Serialize(patterns, Json.Compact));
                ok = patterns.Count > 0;
            }
            catch (InvalidDataException)
            {
                ok = false;
            }
            _patternsWritten[index] = ok;
            return ok ? index : -1;
        }

        /// <summary>The spring area of the season group <paramref name="area"/> belongs to, or the area itself.</summary>
        int SpringArea(int area)
        {
            var offset = Areas[area * Gen5Layout.AreaSize + Gen5Layout.AreaSeasonGroup] % Gen5Layout.Seasons;
            return offset > 0 && area - offset >= 0 && IsSeasonal(area - offset) ? area - offset : area;
        }

        /// <summary>A chunk's collision as written by <see cref="ExportChunk"/>.</summary>
        public byte[] ReadChunkTiles(int chunk) => File.ReadAllBytes(Path.Combine(_chunkDir, $"{chunk:D3}.tiles"));

        /// <summary>Writes a collision grid of its own (<c>chunks/{name}.tiles</c>).</summary>
        public void WriteTiles(string name, byte[] tiles) => File.WriteAllBytes(Path.Combine(_chunkDir, name + ".tiles"), tiles);

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
                if (map.Collision is null)
                    NoCollision.Add(chunkIndex);
                var tiles = new byte[map.Size * map.Size];
                if (map.Collision is not null)
                    for (var ty = 0; ty < map.Size; ty++)
                    for (var tx = 0; tx < map.Size; tx++)
                        tiles[ty * map.Size + tx] = map.TileFlags(tx, ty);
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
