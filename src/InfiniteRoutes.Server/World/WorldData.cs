using System.Collections.Concurrent;
using InfiniteRoutes.Server.Accounts;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Server.World;

/// <summary>The map facts the server checks movement against.</summary>
public interface IWorldData
{
    /// <summary>Where new characters start.</summary>
    StartPoint Start { get; }

    /// <summary>The map key zone <paramref name="zone"/> is drawn in, or null for an unknown zone.</summary>
    string? MapOf(int zone);

    /// <summary>
    /// Whether a map is one big shared space (the overworld, where each 32×32 cell has its own zone) rather than one
    /// room per zone.
    /// </summary>
    bool IsShared(string map);

    /// <summary>The zone of the cell at (x, y) on a shared map, or null to keep the current one.</summary>
    int? ZoneAt(string map, int x, int y);

    /// <summary>Whether (x, y) of <paramref name="zone"/>'s map can't be walked on (walls, or off the map).</summary>
    bool Blocked(int zone, int x, int y);

    /// <summary>The warp covering (x, y) in <paramref name="zone"/>'s map, if any.</summary>
    WarpInfo? WarpAt(int zone, int x, int y);

    /// <summary>Where a warp leads: the zone, the tile to stand on and the facing; null when it leads nowhere.</summary>
    (int Zone, int X, int Y, int Facing)? Destination(WarpInfo warp);

    /// <summary>Whether (x, y) of <paramref name="zone"/>'s map is tall grass (wild Pokémon).</summary>
    bool TallGrass(int zone, int x, int y) => false;

    /// <summary>The zone's wild encounter table (an index into the data pack's), or -1 for none.</summary>
    int EncounterTable(int zone) => -1;

    /// <summary>NPC <paramref name="id"/> of <paramref name="zone"/> as the zone events place it, or null.</summary>
    Extractor.Extraction.NpcInfo? Npc(int zone, int id) => null;

    /// <summary>Where an Infinite Route's entrance puts you, or null if <paramref name="zone"/> isn't one (or can't be built).</summary>
    (int X, int Y)? RouteEntrance(int zone) => null;

    /// <summary>Forgets the Infinite Routes built so far, so hand-made routes saved since are read again.</summary>
    void ForgetRoutes() { }

    /// <summary>A hand-made route's own wild Pokémon (and the percent chance per step in tall grass), if it has them.</summary>
    (int Rate, IReadOnlyList<RouteWild> Slots)? CustomEncounters(int zone) => null;

    /// <summary>The HM obstacle (a small tree, a boulder) standing on (x, y) of <paramref name="zone"/>'s map, if any.</summary>
    NpcInfo? ObstacleAt(int zone, int x, int y) => null;

    /// <summary>Every hand-made route, as its route file's JSON.</summary>
    IReadOnlyList<string> RouteFiles() => [];

    /// <summary>
    /// Saves a hand-made route from the route editor's JSON, in place of the one with its number. Returns its number
    /// and the JSON as stored, or why it can't be saved.
    /// </summary>
    (int Node, string? Json, string? Error) SaveRoute(string json) => (-1, null, "This server has no map data for routes.");

    /// <summary>Deletes hand-made route <paramref name="node"/> (its number goes back to a generated route); false if there was none.</summary>
    bool DeleteRoute(int node) => false;
}

/// <summary>The world from an extraction cache (the server's data pack): zones, maps, per-chunk collision and warps.</summary>
public sealed class AssetWorldData : IWorldData
{
    public const int StartZone = 389;   // Nuvema Town, as in the offline game

    readonly GameAssets _assets;
    readonly Dictionary<int, ZoneInfo> _zones;
    readonly ConcurrentDictionary<string, (WorldIndex Map, Dictionary<(int, int), WorldCell> Cells)?> _maps = new();
    readonly ConcurrentDictionary<string, byte[]> _tiles = new();
    readonly ConcurrentDictionary<int, Dictionary<(int, int), WarpInfo>> _warps = new();

    public StartPoint Start { get; }

    public AssetWorldData(GameAssets assets)
    {
        _assets = assets;
        _zones = assets.LoadZones().ToDictionary(z => z.Id);
        Start = FindStart();
    }

    /// <summary>A real zone, or an Infinite Route (<see cref="UnchartedRoutes"/>), built the first time it's asked for.</summary>
    ZoneInfo? Zone(int id) => _zones.TryGetValue(id, out var z) ? z : _assets.Route(id)?.Zone;

    /// <summary>Where an Infinite Route's entrance puts you, or null if <paramref name="zone"/> isn't one.</summary>
    public (int X, int Y)? RouteEntrance(int zone) => _assets.Route(zone) is { } r ? (r.EntranceX, r.EntranceY) : null;

    public (int Rate, IReadOnlyList<RouteWild> Slots)? CustomEncounters(int zone) =>
        _assets.Route(zone) is { Wild: { Count: > 0 } wild, WildRate: > 0 } route ? (route.WildRate, wild) : null;

    public NpcInfo? ObstacleAt(int zone, int x, int y) => UnchartedRoutes.IsRoute(zone)
        ? _assets.Route(zone)?.Zone.Npcs.FirstOrDefault(n => n.X == x && n.Y == y && UnchartedRoutes.IsObstacle(n)) : null;

    public IReadOnlyList<string> RouteFiles() => _assets.RoutesDirectory is { } directory
        ? Extractor.Extraction.RouteFiles.List(directory).Select(r => Extractor.Extraction.RouteFiles.Load(directory, r.Node))
            .OfType<RouteFile>().Select(Extractor.Extraction.RouteFiles.ToJson).ToList()
        : [];

    public (int Node, string? Json, string? Error) SaveRoute(string json)
    {
        if (_assets.RoutesDirectory is not { } directory)
            return (-1, null, "This server has no routes folder.");
        if (json.Length > 200_000 || Extractor.Extraction.RouteFiles.FromJson(json) is not { } file)
            return (-1, null, "That isn't a route.");
        const int n = UnchartedRoutes.CellTiles;
        if (file.Node < 0 || file.Node > 99_999)
            return (-1, null, "Route numbers are 0 to 99999.");
        if (file.Width % n != 0 || file.Height % n != 0 || file.Width > 8 * n || file.Height > 6 * n
            || file.Rows.Count != file.Height || file.Rows.Any(r => r is null || r.Length != file.Width))
            return (-1, null, "A route is 1–8 cells wide and 1–6 high, every row its full width.");
        file = file with { Name = WorldService.Clean(file.Name) is { Length: <= 20 } name ? name : "" };
        if (CheckParts(ref file) is { } wrong)
            return (-1, null, wrong);
        if (_assets.Theme(file.Area) is not { Usable: true } look)
            return (-1, null, "This server can't draw that look.");
        if (_assets.PreviewRoute(file) is null)
            return (-1, null, "The route has no entrance: bring walkable tiles to the edge of the area.");
        Extractor.Extraction.RouteFiles.Save(directory, file);
        ForgetRoutes();
        return (file.Node, Extractor.Extraction.RouteFiles.ToJson(file), null);
    }

    /// <summary>Checks (and tidies) what a route carries besides its tiles; returns what's wrong, or null.</summary>
    string? CheckParts(ref RouteFile file)
    {
        const int n = UnchartedRoutes.CellTiles;
        int w = file.Width, h = file.Height;
        bool OnMap(int x, int y) => x >= -n && y >= -n && x < w + n && y < h + n;
        bool InArea(int x, int y) => x >= 0 && y >= 0 && x < w && y < h;
        if (file.Outer is { } outer && (outer.Count != h + 2 * n || outer.Any(r => r is null || r.Length != w + 2 * n)))
            return "The scenery round the route is the wrong size.";
        if (file.Links is { Count: > 4 } || file.Npcs is { Count: > 60 } || file.Obstacles is { Count: > 60 } || file.Stamps is { Count: > 400 }
            || file.Wild is { Count: > 12 })
            return "A route holds at most 4 links, 60 people, 60 obstacles, 400 stamps and 12 kinds of wild Pokémon.";
        foreach (var link in file.Links ?? [])
            if (link.Warp < 0 || !(UnchartedRoutes.IsRoute(link.Zone) ? link.Warp <= 3 : _zones.TryGetValue(link.Zone, out var z) && link.Warp < z.Warps.Count))
                return $"An end leads to zone {link.Zone}, warp {link.Warp}, which doesn't exist.";
        foreach (var npc in file.Npcs ?? [])
        {
            if (!InArea(npc.X, npc.Y))
                return "People stand inside the playable area.";
            if (!File.Exists(_assets.NpcSpritePath(npc.Sprite)))
                return $"There's no overworld sprite {npc.Sprite}.";
            if (npc.Text is null || npc.Text.Length > 600)
                return "What a person says is at most 600 characters.";
        }
        file = file with
        {
            Npcs = file.Npcs?.Select(npc => npc with
            {
                Text = string.Join("\n\n", npc.Text.Replace("\r", "").Split("\n\n").Select(page => string.Join("\n", page.Split('\n').Select(WorldService.Clean)))),
                Facing = Math.Clamp(npc.Facing, 0, 3),
                Movement = npc.Movement is 2 or 3 ? npc.Movement : 0,
                Sight = Math.Clamp(npc.Sight, 0, 8),
                GiftCount = Math.Clamp(npc.GiftCount, 0, 99),
            }).ToList(),
        };
        foreach (var o in file.Obstacles ?? [])
            if (!InArea(o.X, o.Y) || o.Kind is not ("cut" or "strength"))
                return "An obstacle is a small tree (cut) or a boulder (strength), inside the playable area.";
        foreach (var stamp in file.Stamps ?? [])
            if (stamp.W is < 1 or > 64 || stamp.H is < 1 or > 64 || !OnMap(stamp.X, stamp.Y) || !OnMap(stamp.X + stamp.W - 1, stamp.Y + stamp.H - 1))
                return "A stamp is 1–64 tiles each way and lies on the map.";
        foreach (var slot in file.Wild ?? [])
            if (slot.Species is < 1 or > 649 || slot.MinLevel < 1 || slot.MaxLevel > 100 || slot.MinLevel > slot.MaxLevel || slot.Weight is < 1 or > 100)
                return "A wild Pokémon needs a species (1–649), levels 1–100 (lowest first) and a weight of 1–100.";
        if (file.WildRate is < 0 or > 100)
            return "The chance of a wild Pokémon is 0–100% a step.";
        if (file.EncounterZone >= 0 && !(_zones.TryGetValue(file.EncounterZone, out var from) && from.Encounters >= 0))
            return $"Zone {file.EncounterZone} has no wild Pokémon to borrow.";
        return null;
    }

    public bool DeleteRoute(int node)
    {
        if (_assets.RoutesDirectory is not { } directory || !Extractor.Extraction.RouteFiles.Delete(directory, node))
            return false;
        ForgetRoutes();
        return true;
    }

    public void ForgetRoutes()
    {
        _assets.ForgetRoutes();
        foreach (var key in _maps.Keys.Where(k => UnchartedRoutes.TryParseKey(k, out _)).ToList())
            _maps.TryRemove(key, out _);
        foreach (var key in _tiles.Keys.Where(k => UnchartedRoutes.TryParseCellKey(k, out _, out _, out _)).ToList())
            _tiles.TryRemove(key, out _);
        foreach (var key in _warps.Keys.Where(UnchartedRoutes.IsRoute).ToList())
            _warps.TryRemove(key, out _);
    }

    (WorldIndex Map, Dictionary<(int, int), WorldCell> Cells)? Map(string key) =>
        _maps.GetOrAdd(key, k => _assets.LoadMap(k) is { } m ? (m, m.Cells.ToDictionary(c => (c.X, c.Y))) : null);

    public string? MapOf(int zone) => Zone(zone)?.Map;

    public bool IsShared(string map) => Map(map) is { } m && m.Map.Cells.Any(c => c.Zone >= 0);

    public int? ZoneAt(string map, int x, int y) =>
        Map(map) is { } m && m.Cells.TryGetValue(Cell(m.Map, x, y), out var c) && c.Zone >= 0 && Zone(c.Zone) is not null ? c.Zone : null;

    static (int, int) Cell(WorldIndex map, int x, int y) =>
        ((int)Math.Floor(x / (double)map.ChunkTiles), (int)Math.Floor(y / (double)map.ChunkTiles));

    public bool Blocked(int zone, int x, int y) => Flags(zone, x, y) is not { } f || (f & 1) != 0;

    public bool TallGrass(int zone, int x, int y) => Flags(zone, x, y) is { } f && (f & 2) != 0;

    public int EncounterTable(int zone) => Zone(zone)?.Encounters ?? -1;

    public NpcInfo? Npc(int zone, int id) => Zone(zone)?.Npcs.FirstOrDefault(n => n.Id == id);

    /// <summary>A tile's collision flags (1 blocked, 2 tall grass), or null off the map.</summary>
    byte? Flags(int zone, int x, int y)
    {
        if (MapOf(zone) is not { } key || Map(key) is not { } m)
            return null;
        var (cx, cy) = Cell(m.Map, x, y);
        if (!m.Cells.TryGetValue((cx, cy), out var cell))
            return null;
        var tiles = _tiles.GetOrAdd(GameAssets.TilesKey(cell), _ => _assets.CellTiles(cell));
        var n = m.Map.ChunkTiles;
        var i = (y - cy * n) * n + (x - cx * n);
        return i < 0 || i >= tiles.Length ? null : tiles[i];
    }

    /// <summary>
    /// A map's warps by tile: on a shared map every zone's on that map, otherwise the zone's own (the same rule the
    /// client uses). Warps into unknown zones are left out.
    /// </summary>
    Dictionary<(int, int), WarpInfo> Warps(int zone)
    {
        var key = MapOf(zone) ?? "";
        var shared = IsShared(key);
        return _warps.GetOrAdd(shared ? -1 - key.GetHashCode() : zone, _ =>
        {
            var table = new Dictionary<(int, int), WarpInfo>();
            var zones = shared ? _zones.Values.Where(z => z.Map == key) : Zone(zone) is { } own ? [own] : [];
            foreach (var z in zones)
                foreach (var w in z.Warps.Where(w => Zone(w.TargetZone) is not null))
                    for (var y = w.Y; y < w.Y + Math.Max(1, w.Height); y++)
                        for (var x = w.X; x < w.X + Math.Max(1, w.Width); x++)
                            table.TryAdd((x, y), w);
            return table;
        });
    }

    public WarpInfo? WarpAt(int zone, int x, int y) => Warps(zone).TryGetValue((x, y), out var w) ? w : null;

    public (int Zone, int X, int Y, int Facing)? Destination(WarpInfo warp)
    {
        if (Zone(warp.TargetZone) is not { } zone || warp.TargetWarp < 0 || warp.TargetWarp >= zone.Warps.Count)
            return null;
        var dest = zone.Warps[warp.TargetWarp];
        // Mats are several tiles wide; arrive in the middle (as the client does).
        return (zone.Id, dest.X + (Math.Max(1, dest.Width) - 1) / 2, dest.Y + (Math.Max(1, dest.Height) - 1) / 2,
            (int)(dest.Facing ?? Facing.Down));
    }

    /// <summary>The walkable tile nearest the middle of Nuvema Town's cell on the overworld (the offline game's start).</summary>
    StartPoint FindStart()
    {
        var key = MapOf(StartZone) ?? Gen5WorldExtractor.OverworldKey;
        if (Map(key) is not { } m || m.Map.Cells.Count == 0)
            return new StartPoint(StartZone, 0, 0, 0);
        var cell = m.Map.Cells.FirstOrDefault(c => c.Zone == StartZone) ?? m.Map.Cells[0];
        var n = m.Map.ChunkTiles;
        int ox = cell.X * n + n / 2, oy = cell.Y * n + n / 2;
        for (var r = 0; r < n; r++)
            for (var dy = -r; dy <= r; dy++)
                for (var dx = -r; dx <= r; dx++)
                    if (!Blocked(StartZone, ox + dx, oy + dy) && WarpAt(StartZone, ox + dx, oy + dy) is null)
                        return new StartPoint(StartZone, ox + dx, oy + dy, 0);
        return new StartPoint(StartZone, ox, oy, 0);
    }
}
