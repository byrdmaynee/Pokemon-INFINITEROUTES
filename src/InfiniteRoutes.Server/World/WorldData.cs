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
}

/// <summary>The world from an extraction cache (the server's data pack): zones, maps, per-chunk collision and warps.</summary>
public sealed class AssetWorldData : IWorldData
{
    public const int StartZone = 389;   // Nuvema Town, as in the offline game

    readonly GameAssets _assets;
    readonly Dictionary<int, ZoneInfo> _zones;
    readonly ConcurrentDictionary<string, (WorldIndex Map, Dictionary<(int, int), WorldCell> Cells)?> _maps = new();
    readonly ConcurrentDictionary<int, byte[]> _tiles = new();
    readonly ConcurrentDictionary<int, Dictionary<(int, int), WarpInfo>> _warps = new();

    public StartPoint Start { get; }

    public AssetWorldData(GameAssets assets)
    {
        _assets = assets;
        _zones = assets.LoadZones().ToDictionary(z => z.Id);
        Start = FindStart();
    }

    (WorldIndex Map, Dictionary<(int, int), WorldCell> Cells)? Map(string key) =>
        _maps.GetOrAdd(key, k => _assets.LoadMap(k) is { } m ? (m, m.Cells.ToDictionary(c => (c.X, c.Y))) : null);

    public string? MapOf(int zone) => _zones.TryGetValue(zone, out var z) ? z.Map : null;

    public bool IsShared(string map) => Map(map) is { } m && m.Map.Cells.Any(c => c.Zone >= 0);

    public int? ZoneAt(string map, int x, int y) =>
        Map(map) is { } m && m.Cells.TryGetValue(Cell(m.Map, x, y), out var c) && c.Zone >= 0 && _zones.ContainsKey(c.Zone) ? c.Zone : null;

    static (int, int) Cell(WorldIndex map, int x, int y) =>
        ((int)Math.Floor(x / (double)map.ChunkTiles), (int)Math.Floor(y / (double)map.ChunkTiles));

    public bool Blocked(int zone, int x, int y)
    {
        if (MapOf(zone) is not { } key || Map(key) is not { } m)
            return true;
        var (cx, cy) = Cell(m.Map, x, y);
        if (!m.Cells.TryGetValue((cx, cy), out var cell))
            return true;
        var tiles = _tiles.GetOrAdd(cell.Chunk, _assets.ChunkTiles);
        var n = m.Map.ChunkTiles;
        var i = (y - cy * n) * n + (x - cx * n);
        return i < 0 || i >= tiles.Length || (tiles[i] & 1) != 0;
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
            foreach (var z in _zones.Values.Where(z => shared ? z.Map == key : z.Id == zone))
                foreach (var w in z.Warps.Where(w => _zones.ContainsKey(w.TargetZone)))
                    for (var y = w.Y; y < w.Y + Math.Max(1, w.Height); y++)
                        for (var x = w.X; x < w.X + Math.Max(1, w.Width); x++)
                            table.TryAdd((x, y), w);
            return table;
        });
    }

    public WarpInfo? WarpAt(int zone, int x, int y) => Warps(zone).TryGetValue((x, y), out var w) ? w : null;

    public (int Zone, int X, int Y, int Facing)? Destination(WarpInfo warp)
    {
        if (!_zones.TryGetValue(warp.TargetZone, out var zone) || warp.TargetWarp < 0 || warp.TargetWarp >= zone.Warps.Count)
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
