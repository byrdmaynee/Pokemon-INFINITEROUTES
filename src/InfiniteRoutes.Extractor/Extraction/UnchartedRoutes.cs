using InfiniteRoutes.Extractor.Gen5;
using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Extractor.Extraction;

/// <summary>
/// A generated route: its map, its zone (warps, encounters), the tile its entrance puts you on, and its terrain — for
/// every tile, the <see cref="Theme"/> sample tile it's drawn with (<see cref="Source"/>, -1 for none) and its
/// collision (<see cref="Flags"/>), row-major over <see cref="WidthTiles"/>.
/// </summary>
/// <see cref="Wild"/> (with <see cref="WildRate"/> percent per step in tall grass) are a hand-made route's own wild
/// Pokémon, when it has them.
/// <see cref="Extra"/>: tiles (by index) drawn from another set's maps — instead of <see cref="Source"/>, or (for an
/// objects-only stamp) on top of it.
public sealed record GeneratedRoute(int Node, int Depth, WorldIndex Map, ZoneInfo Zone, int EntranceX, int EntranceY,
    TerrainTheme Theme, int WidthTiles, int[] Source, byte[] Flags, IReadOnlyList<RouteWild>? Wild = null, int WildRate = 0,
    IReadOnlyDictionary<int, TileLayer>? Extra = null)
{
    /// <summary>The collision of map cell (<paramref name="cx"/>, <paramref name="cy"/>), 32×32.</summary>
    public byte[] CellTiles(int cx, int cy)
    {
        const int n = UnchartedRoutes.CellTiles;
        var tiles = new byte[n * n];
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                int tx = cx * n + x, ty = cy * n + y;
                tiles[y * n + x] = tx >= 0 && ty >= 0 && tx < WidthTiles && ty * WidthTiles + tx < Flags.Length ? Flags[ty * WidthTiles + tx] : (byte)1;
            }
        return tiles;
    }
}

/// <summary>A tile drawn from <see cref="Theme"/>'s sample tile <see cref="Source"/>: all of it, or only its placed objects.</summary>
public sealed record TileLayer(TerrainTheme Theme, int Source, bool ObjectsOnly);

/// <summary>
/// Infinite Routes (BUILD_SPEC §7.11): routes whose terrain is generated, drawn with one real route's own textures,
/// trees and cliffs.
/// <para>
/// The routes form a tree numbered like a heap: node 0 is the first route; node n's exits k = 0…2 lead to nodes
/// 3n+1+k, so a route's parent is (n−1)/3 and its depth is how many steps that takes to reach 0, plus one. Each node is
/// the zone <see cref="ZoneBase"/> + n on the map <c>ir{n}</c>, and is built from a seed made from n alone, so the
/// server and every client build the same route from their own data without sending it.
/// </para>
/// <para>
/// Building one: pick a <see cref="TerrainTheme"/>; <see cref="RouteLayout"/> designs the route by rule — what every
/// tile is; then each tile is drawn by a real tile of the theme that is of that kind and sits in the same surroundings
/// (<see cref="RouteKit"/>). Collision follows the design. Warp 0 is the entrance (back to the parent's exit, or into
/// Nuvema Town from the first route); warps 1… are the exits, in the order their child routes are numbered.
/// </para>
/// </summary>
public static class UnchartedRoutes
{
    public const int ZoneBase = 1_000_000;
    public const string Unnamed = "An Uncharted Route";
    public const int CellTiles = 32;
    const string KeyPrefix = "ir";
    const int N = CellTiles;

    /// <summary>Where the first route's entrance leads: Nuvema Town, at its first warp (until real gateways exist).</summary>
    public const int HomeZone = 389, HomeWarp = 0;

    /// <summary>
    /// HM obstacles are NPCs with these scripts: a small tree to Cut and a boulder for Strength, drawn with the ROM's
    /// own sprites. Their ids on a route start at <see cref="ObstacleIdBase"/>.
    /// </summary>
    public const int CutScript = 9001, StrengthScript = 9002, ObstacleIdBase = 1000;
    public const int CutSprite = 108, StrengthSprite = 206;

    public static bool IsObstacle(NpcInfo npc) => npc.Script is CutScript or StrengthScript;

    /// <summary>
    /// Trainers made for hand-made routes have ids from here: route n's person i is
    /// <see cref="TrainerIdBase"/> + n × 64 + i (beating one is remembered by that id, like the ROM's).
    /// </summary>
    public const int TrainerIdBase = 2_000_000, TrainersPerRoute = 64;

    public static int TrainerId(int node, int person) => TrainerIdBase + node * TrainersPerRoute + person;

    public static bool IsRouteTrainer(int id) => id >= TrainerIdBase;

    public static (int Node, int Person) RouteTrainerOf(int id) => ((id - TrainerIdBase) / TrainersPerRoute, (id - TrainerIdBase) % TrainersPerRoute);

    /// <summary>
    /// The trainer person <paramref name="person"/> of <paramref name="file"/> battles as, built the same way by the
    /// server and every client; null if they aren't one. <paramref name="className"/> names a trainer class.
    /// </summary>
    public static Core.Data.TrainerInfo? TrainerOf(RouteFile file, int person, Func<int, string> className)
    {
        if (file.Npcs is not { } npcs || person < 0 || person >= npcs.Count || npcs[person].Battle is not { Party.Count: > 0 } battle)
            return null;
        var npc = npcs[person];
        // Stronger the higher their level, as the ROM's own trainers are (IVs up to 31 at level 100).
        var party = battle.Party.Take(6).Select(m => new Core.Data.TrainerPokemon(m.Species, 0, Math.Clamp(m.Level, 1, 100),
            Math.Clamp(m.Level * 255 / 100, 0, 255), 0, 0, null)).ToList();
        return new Core.Data.TrainerInfo(TrainerId(file.Node, person), battle.Name, battle.Class, className(battle.Class), 0, [], 0, 0, party,
            Intro: npc.Text.Replace("\n\n", "\f"), Defeat: battle.Defeat.Replace("\n\n", "\f"), After: battle.After.Replace("\n\n", "\f"),
            Prize: Math.Max(0, battle.Prize));
    }

    /// <summary>
    /// An obstacle's field move (Cut, Strength), the Gym Badges it needs outside battle, what the obstacle looks like
    /// and what using the move on it does.
    /// </summary>
    public static (int Move, string MoveName, int Badges, string Looks, string Verb) ObstacleRule(int script) => script == CutScript
        ? (Core.Battle.ItemEffect.CutMove, "Cut", 1, "This tree looks like it can be cut down!", "cut the tree")
        : (Core.Battle.ItemEffect.StrengthMove, "Strength", 3, "It's a big boulder, but a Pokémon may be able to smash it.", "smash the boulder");

    public static bool IsRoute(int zone) => zone >= ZoneBase;

    public static int ZoneOf(int node) => ZoneBase + node;

    public static string MapKey(int node) => KeyPrefix + node;

    public static bool TryParseKey(string key, out int node)
    {
        node = -1;
        return key.StartsWith(KeyPrefix, StringComparison.Ordinal) && int.TryParse(key.AsSpan(KeyPrefix.Length), out node) && node >= 0;
    }

    /// <summary>The <see cref="WorldCell.Tiles"/> name of a generated route's cell: "ir{node}:{cx}:{cy}".</summary>
    public static string CellKey(int node, int cx, int cy) => $"{KeyPrefix}{node}:{cx}:{cy}";

    public static bool TryParseCellKey(string? key, out int node, out int cx, out int cy)
    {
        node = cx = cy = -1;
        if (key is null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            return false;
        var parts = key[KeyPrefix.Length..].Split(':');
        return parts.Length == 3 && int.TryParse(parts[0], out node) && int.TryParse(parts[1], out cx) && int.TryParse(parts[2], out cy);
    }

    public static int Depth(int node)
    {
        var depth = 1;
        for (; node > 0; node = (node - 1) / 3)
            depth++;
        return depth;
    }

    /// <summary>The node exit <paramref name="exit"/> of <paramref name="node"/> leads to, or -1 past the numbering's range.</summary>
    public static int Child(int node, int exit) => node <= (int.MaxValue - ZoneBase - 3) / 3 ? 3 * node + 1 + exit : -1;

    static int Seed(int node)
    {
        unchecked
        {
            var h = (uint)node * 2654435761u ^ 0x9E3779B9u;
            h ^= h >> 15;
            h *= 0x85EBCA6Bu;
            h ^= h >> 13;
            return (int)(h & 0x7FFFFFFF);
        }
    }

    /// <summary>
    /// Builds route <paramref name="node"/>, or null if no theme can be had. <paramref name="areas"/> are the themes'
    /// texture sets to choose from and <paramref name="theme"/> builds (or fetches) one; <paramref name="zones"/> and
    /// <paramref name="tables"/> give the theme's real route's wild Pokémon and battle scenery.
    /// </summary>
    public static GeneratedRoute? Generate(int node, IReadOnlyList<int> areas, Func<int, TerrainTheme?> theme,
        IReadOnlyDictionary<int, ZoneInfo> zones, IReadOnlyList<EncounterTable> tables)
    {
        if (node < 0 || areas.Count == 0)
            return null;
        var rng = new Random(Seed(node));
        var depth = Depth(node);
        // A seeded pick among the usable themes: draw again until one is (each equally likely).
        TerrainTheme? look = null;
        var left = areas.ToList();
        while (look is null && left.Count > 0)
        {
            var area = left[rng.Next(left.Count)];
            left.Remove(area);
            look = theme(area) is { Usable: true } t ? t : null;
        }
        if (look is null)
            return null;
        // The playable area, inside a margin of one cell of scenery all round (so the map never ends in sky).
        // Two cells tall, so the path's legs fill it; longer with depth.
        int innerW = 3 + Math.Min(2, (depth - 1) / 2) + rng.Next(2), innerH = 2;
        int cellsW = innerW + 2, cellsH = innerH + 2;
        int w = cellsW * N, h = cellsH * N;
        var inner = (X: N, Y: N, W: innerW * N, H: innerH * N);
        var wantExits = 1 + rng.Next(3);

        // The design: what every tile is.
        var design = RouteLayout.Draw(rng, w, h, inner, wantExits, look.Kit.Has(TileKind.Grass), look.Kit.Has(TileKind.Water));
        // Each end: the path and its verges where they meet the edge of the playable area (6 tiles).
        var ends = design.Ends.Select(e => e.Inward switch
        {
            Facing.Right => (X: inner.X, Y: e.Y - 2, Along: 6, Vertical: true, e.Inward),
            Facing.Left => (X: inner.X + inner.W - 1, Y: e.Y - 2, Along: 6, Vertical: true, e.Inward),
            Facing.Down => (X: e.X - 2, Y: inner.Y, Along: 6, Vertical: false, e.Inward),
            _ => (X: e.X - 2, Y: inner.Y + inner.H - 1, Along: 6, Vertical: false, e.Inward),
        }).ToList();
        var route = Assemble(node, Unnamed, look, design.At, w, h, inner, ends, zones, tables);
        return Reachable(route.Flags, w, h, ends) ? route : null;
    }

    /// <summary>
    /// Builds route <paramref name="file"/>.Node from a hand-made design (the route editor's): the saved tiles are the
    /// playable area, with a cell of forest added all round. Its ends are the runs of walkable tiles on the area's
    /// edge: the entrance is the one the file names (or the first on the west edge, or the first of all), the others
    /// are the exits (three at most), in the order north, east, south, west. Null if it has no ends at all, or
    /// <paramref name="look"/> can't draw routes.
    /// </summary>
    /// <param name="sources">Texture sets' whole maps by area, for stamps copied from them.</param>
    public static GeneratedRoute? FromFile(RouteFile file, TerrainTheme look, IReadOnlyDictionary<int, ZoneInfo> zones,
        IReadOnlyList<EncounterTable> tables, Func<int, TerrainTheme?>? sources = null)
    {
        if (file.Width <= 0 || file.Height <= 0 || file.Width % N != 0 || file.Height % N != 0 || !look.Usable)
            return null;
        int w = file.Width + 2 * N, h = file.Height + 2 * N;
        var inner = (X: N, Y: N, W: file.Width, H: file.Height);
        var ends = Ends(file).Select(e => (X: e.X + N, Y: e.Y + N, e.Along, e.Vertical, e.Inward)).ToList();
        if (ends.Count == 0)
            return null;

        TileKind At(int x, int y)
        {
            if (Inside(inner, x, y))
                return file.At(x - N, y - N);
            // The scenery round the area: as painted, if it was.
            if (file.Outer is not null)
                return file.OuterAt(x - N, y - N) ?? TileKind.Tree;
            // Otherwise forest, with the ground carrying on a little past each end (for the look).
            foreach (var e in ends)
            {
                var (dx, dy) = e.Inward switch { Facing.Right => (-1, 0), Facing.Left => (1, 0), Facing.Down => (0, -1), _ => (0, 1) };
                var out_ = dx != 0 ? (x - e.X) * dx : (y - e.Y) * dy;
                var along = e.Vertical ? y - e.Y : x - e.X;
                if (out_ is >= 1 and <= 10 && along >= 0 && along < e.Along)
                    return file.At((e.Vertical ? e.X : x) - N, (e.Vertical ? y : e.Y) - N);
            }
            return TileKind.Tree;
        }

        // Stamps: real tiles copied (later ones on top) — whole, or just the objects on them.
        Dictionary<(int, int), (TerrainTheme Theme, int Source, bool ObjectsOnly)>? stamped = null;
        foreach (var stamp in file.Stamps ?? [])
        {
            var from = stamp.Area < 0 ? look : sources?.Invoke(stamp.Area);
            if (from is null)
                continue;
            for (var dy = 0; dy < stamp.H; dy++)
                for (var dx = 0; dx < stamp.W; dx++)
                {
                    int sx = stamp.SourceX + dx, sy = stamp.SourceY + dy;
                    if (!from.Has(sx, sy) || stamp.ObjectsOnly && !from.ObjectTile[sy * from.Width + sx])
                        continue;
                    (stamped ??= new())[(stamp.X + dx + N, stamp.Y + dy + N)] = (from, sy * from.Width + sx, stamp.ObjectsOnly);
                }
        }

        // Where each end leads, if the file says.
        var links = ends.Select(e => (file.Links ?? []).FirstOrDefault(l =>
            e.Vertical ? l.X + N == e.X && l.Y + N >= e.Y && l.Y + N < e.Y + e.Along : l.Y + N == e.Y && l.X + N >= e.X && l.X + N < e.X + e.Along)).ToList();

        // People, then the HM obstacles (which are NPCs too).
        var npcs = new List<NpcInfo>();
        foreach (var (npc, i) in (file.Npcs ?? []).Select((npc, i) => (npc, i)))
        {
            var pages = npc.Text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var wander = npc.Movement == 3 ? 2 : 0;
            // A trainer made for the route has its own id; what they say after the battle is theirs too.
            var trainer = npc.Battle is { Party.Count: > 0 } ? TrainerId(file.Node, i) : npc.Trainer;
            var later = npc.Battle is { After.Length: > 0 } b
                ? b.After.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
            var win = npc.Battle is { Defeat.Length: > 0 } d
                ? d.Defeat.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
            npcs.Add(new NpcInfo(i, npc.Sprite, npc.X + N, npc.Y + N, look.BaseHeight, (Facing)Math.Clamp(npc.Facing, 0, 3), npc.Movement,
                wander, wander, 0, 0, trainer >= 0 ? Math.Max(0, npc.Sight) : 0,
                new NpcTalk(pages, trainer, Win: win, Later: later, GiftItem: trainer >= 0 ? 0 : npc.GiftItem, GiftCount: trainer >= 0 ? 0 : npc.GiftCount)));
        }
        foreach (var (o, i) in (file.Obstacles ?? []).Select((o, i) => (o, i)))
        {
            var cut = o.Kind != "strength";
            npcs.Add(new NpcInfo(ObstacleIdBase + i, cut ? CutSprite : StrengthSprite, o.X + N, o.Y + N, look.BaseHeight, Facing.Down, 0, 0, 0,
                cut ? CutScript : StrengthScript, 0));
        }

        var encounters = file.EncounterZone >= 0 && zones.GetValueOrDefault(file.EncounterZone) is { Encounters: >= 0 } borrowed
            && borrowed.Encounters < tables.Count ? borrowed.Encounters : (int?)null;
        var wild = file.Wild is { Count: > 0 } && file.WildRate > 0 ? file.Wild : null;
        return Assemble(file.Node, file.Name is { Length: > 0 } name ? name : Unnamed, look, At, w, h, inner, ends, zones, tables,
            stamped, links, npcs, encounters, wild, file.WildRate);
    }

    static bool Walkable(TileKind kind) => kind is TileKind.Ground or TileKind.Path or TileKind.Grass;

    /// <summary>
    /// The ends of a hand-made route, in its own tile coordinates: each run of walkable tiles along an edge of the
    /// area, entrance first (see <see cref="FromFile"/>), then up to three exits.
    /// </summary>
    public static List<(int X, int Y, int Along, bool Vertical, Facing Inward)> Ends(RouteFile file)
    {
        var runs = new List<(int X, int Y, int Along, bool Vertical, Facing Inward)>();
        void Scan(bool vertical, int fixedAt, Facing inward)
        {
            var length = vertical ? file.Height : file.Width;
            var from = -1;
            for (var k = 0; k <= length; k++)
            {
                var open = k < length && Walkable(vertical ? file.At(fixedAt, k) : file.At(k, fixedAt));
                if (open && from < 0)
                    from = k;
                else if (!open && from >= 0)
                {
                    runs.Add(vertical ? (fixedAt, from, k - from, true, inward) : (from, fixedAt, k - from, false, inward));
                    from = -1;
                }
            }
        }
        Scan(false, 0, Facing.Down);
        Scan(true, file.Width - 1, Facing.Left);
        Scan(false, file.Height - 1, Facing.Up);
        Scan(true, 0, Facing.Right);
        if (runs.Count == 0)
            return runs;
        bool Holds((int X, int Y, int Along, bool Vertical, Facing Inward) r, int x, int y) =>
            r.Vertical ? x == r.X && y >= r.Y && y < r.Y + r.Along : y == r.Y && x >= r.X && x < r.X + r.Along;
        var entrance = runs.FindIndex(r => Holds(r, file.EntranceX, file.EntranceY));
        if (entrance < 0)
            entrance = runs.FindIndex(r => r.Inward == Facing.Right);
        if (entrance < 0)
            entrance = 0;
        var first = runs[entrance];
        runs.RemoveAt(entrance);
        return [first, .. runs.Take(3)];
    }

    /// <summary>
    /// Makes the route from its design: each tile is drawn by a real tile of that kind in the same surroundings
    /// (<see cref="RouteKit"/>), or by its stamp's real tile; collision follows the design (a stamp's follows the real
    /// tile), and outside the playable area everything is solid; warp 0 is the first end, the rest are the exits.
    /// </summary>
    static GeneratedRoute Assemble(int node, string name, TerrainTheme look, Func<int, int, TileKind> at, int w, int h,
        (int X, int Y, int W, int H) inner, List<(int X, int Y, int Along, bool Vertical, Facing Inward)> ends,
        IReadOnlyDictionary<int, ZoneInfo> zones, IReadOnlyList<EncounterTable> tables,
        Dictionary<(int, int), (TerrainTheme Theme, int Source, bool ObjectsOnly)>? stamped = null, IReadOnlyList<RouteLink?>? links = null,
        IReadOnlyList<NpcInfo>? npcs = null,
        int? encounterTable = null, IReadOnlyList<RouteWild>? wild = null, int wildRate = 0)
    {
        var salt = (uint)Seed(node);
        var source = new int[w * h];
        var flags = new byte[w * h];
        Dictionary<int, TileLayer>? extra = null;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (stamped is not null && stamped.TryGetValue((x, y), out var copied))
                {
                    var inside = Inside(inner, x, y);
                    if (copied.ObjectsOnly)
                    {
                        // The object stands on the route's own ground, and is solid where it stands.
                        source[y * w + x] = look.Kit.Pick(x, y, at, salt, out _);
                        (extra ??= new())[y * w + x] = new TileLayer(copied.Theme, copied.Source, true);
                        flags[y * w + x] = !inside ? (byte)1 : (copied.Theme.Flags[copied.Source] & 5) != 0 ? (byte)1 : Ground(at(x, y));
                        continue;
                    }
                    if (copied.Theme == look)
                        source[y * w + x] = copied.Source;
                    else
                    {
                        source[y * w + x] = look.Kit.Pick(x, y, at, salt, out _);
                        (extra ??= new())[y * w + x] = new TileLayer(copied.Theme, copied.Source, false);
                    }
                    flags[y * w + x] = inside ? copied.Theme.Flags[copied.Source] : (byte)1;
                    continue;
                }
                source[y * w + x] = look.Kit.Pick(x, y, at, salt, out _);
                flags[y * w + x] = !Inside(inner, x, y) ? (byte)1 : at(x, y) switch
                {
                    TileKind.Ground or TileKind.Path => (byte)0,
                    TileKind.Grass => (byte)2,
                    TileKind.Water => (byte)5,
                    _ => (byte)1,
                };
            }

        var warps = new List<WarpInfo>();
        var parent = node == 0 ? (Zone: HomeZone, Warp: HomeWarp) : (Zone: ZoneOf((node - 1) / 3), Warp: 1 + (node - 1) % 3);
        RouteLink? Link(int end) => links is not null && end < links.Count ? links[end] : null;
        warps.Add(Link(0) is { } back ? Warp(ends[0], back.Zone, back.Warp) : Warp(ends[0], parent.Zone, parent.Warp));
        for (var k = 1; k < ends.Count; k++)
            if (Link(k) is { } to)
                warps.Add(Warp(ends[k], to.Zone, to.Warp));
            else if (Child(node, k - 1) is var child and >= 0)
                warps.Add(Warp(ends[k], ZoneOf(child), 0));

        int cellsW = w / N, cellsH = h / N;
        var cells = new List<WorldCell>();
        var seasons = look.Cells.Min(c => c.Seasons);
        for (var cy = 0; cy < cellsH; cy++)
            for (var cx = 0; cx < cellsW; cx++)
                cells.Add(new WorldCell(cx, cy, -1, -1, name, look.Area, seasons, Tiles: CellKey(node, cx, cy)));

        // Wild Pokémon and battle scenery: the theme's own route's.
        var real = zones.GetValueOrDefault(look.SourceZone);
        var encounters = encounterTable ?? (real is { Encounters: >= 0 } && real.Encounters < tables.Count ? real.Encounters : -1);
        var key = MapKey(node);
        var zone = new ZoneInfo(ZoneOf(node), key, name, -1, warps, npcs ?? [], encounters, null, real?.BattleScenery ?? 0);
        var map = new WorldIndex(key, cellsW, cellsH, N, cells);
        var entrance = warps[0];
        return new GeneratedRoute(node, Depth(node), map, zone, entrance.X + (Math.Max(1, entrance.Width) - 1) / 2,
            entrance.Y + (Math.Max(1, entrance.Height) - 1) / 2, look, w, source, flags, wild, wildRate, extra);
    }

    /// <summary>The collision a tile of the design has.</summary>
    static byte Ground(TileKind kind) => kind switch
    {
        TileKind.Ground or TileKind.Path => 0,
        TileKind.Grass => 2,
        TileKind.Water => 5,
        _ => 1,
    };

    static bool Inside((int X, int Y, int W, int H) r, int x, int y) => x >= r.X && y >= r.Y && x < r.X + r.W && y < r.Y + r.H;

    /// <summary>Whether every end can be walked to from the first (the entrance).</summary>
    static bool Reachable(byte[] flags, int w, int h, List<(int X, int Y, int Along, bool Vertical, Facing Inward)> ends)
    {
        var seen = new bool[w * h];
        var queue = new Queue<int>();
        var start = ends[0].Y * w + ends[0].X;
        if ((flags[start] & 5) != 0)
            return false;
        seen[start] = true;
        queue.Enqueue(start);
        while (queue.TryDequeue(out var i))
        {
            int x = i % w, y = i / w;
            foreach (var (nx, ny) in (ReadOnlySpan<(int, int)>)[(x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)])
                if (nx >= 0 && ny >= 0 && nx < w && ny < h && !seen[ny * w + nx] && (flags[ny * w + nx] & 5) == 0)
                {
                    seen[ny * w + nx] = true;
                    queue.Enqueue(ny * w + nx);
                }
        }
        return ends.All(e => seen[e.Y * w + e.X]);
    }

    static WarpInfo Warp((int X, int Y, int Along, bool Vertical, Facing Inward) end, int targetZone, int targetWarp) =>
        new(end.X, end.Y, end.Vertical ? 1 : end.Along, end.Vertical ? end.Along : 1, targetZone, targetWarp, end.Inward, WarpKind.Edge);
}
