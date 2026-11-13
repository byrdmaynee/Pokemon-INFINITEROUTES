using System.Text.Json;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Tests;

public class InfiniteRouteTests
{
    /// <summary>
    /// A made-up 96×96-tile theme: forest, with blocks of open ground, path, tall grass and water drawn on a 2×2 grid
    /// (as a real route's are), so it has every kind in many surroundings.
    /// </summary>
    static TerrainTheme Theme()
    {
        const int size = 96;
        var rng = new Random(3);
        var kind = Enumerable.Repeat(TileKind.Tree, size * size).ToArray();
        for (var blob = 0; blob < 70; blob++)
        {
            int bx = rng.Next(1, size / 2 - 8) * 2, by = rng.Next(1, size / 2 - 8) * 2, bw = rng.Next(1, 7) * 2, bh = rng.Next(1, 5) * 2;
            var k = rng.Next(5) switch { 0 => TileKind.Path, 1 => TileKind.Grass, 2 => TileKind.Water, _ => TileKind.Ground };
            for (var y = by; y < by + bh; y++)
                for (var x = bx; x < bx + bw; x++)
                    kind[y * size + x] = k;
        }
        var flags = kind.Select(k => k switch { TileKind.Ground or TileKind.Path => (byte)0, TileKind.Grass => (byte)2, TileKind.Water => (byte)5, _ => (byte)1 }).ToArray();
        var cells = new List<WorldCell>();
        for (var cy = 0; cy < 3; cy++)
            for (var cx = 0; cx < 3; cx++)
                cells.Add(new WorldCell(cx, cy, cy * 3 + cx, 300, "Route 0", 7));
        return new TerrainTheme
        {
            Area = 7, Name = "Route 0", SourceZone = 300, Cells = cells, Width = size, Height = size, OriginX = 0, OriginY = 0,
            CellOf = Enumerable.Range(0, size * size).Select(t => t / size / 32 * 3 + t % size / 32).ToArray(),
            Flags = flags, Kind = kind, Ground = new float[size * size], BaseHeight = 0,
        };
    }

    internal static TerrainTheme SharedTheme => Shared.Value;
    internal static IReadOnlyDictionary<int, ZoneInfo> TestZones => Zones;

    static readonly Dictionary<int, ZoneInfo> Zones = new() { [300] = new ZoneInfo(300, "m000", "Route 0", -1, [], [], -1) };

    static readonly Lazy<TerrainTheme> Shared = new(Theme);

    static GeneratedRoute Route(int node) => UnchartedRoutes.Generate(node, [7], _ => Shared.Value, Zones, [])
        ?? throw new InvalidOperationException($"route {node} wasn't built");

    [Fact]
    public void The_route_tree_is_numbered_like_a_heap()
    {
        Assert.Equal(1, UnchartedRoutes.Depth(0));
        Assert.Equal(2, UnchartedRoutes.Depth(1));
        Assert.Equal(2, UnchartedRoutes.Depth(3));
        Assert.Equal(3, UnchartedRoutes.Depth(4));
        Assert.Equal(13, UnchartedRoutes.Child(4, 0));
        Assert.True(UnchartedRoutes.TryParseKey(UnchartedRoutes.MapKey(42), out var node) && node == 42);
        Assert.False(UnchartedRoutes.TryParseKey("m000", out _));
        Assert.True(UnchartedRoutes.TryParseCellKey(UnchartedRoutes.CellKey(42, 3, 1), out var n, out var cx, out var cy) && (n, cx, cy) == (42, 3, 1));
    }

    [Fact]
    public void A_route_is_the_same_every_time_it_is_built()
    {
        for (var node = 0; node < 8; node++)
        {
            GeneratedRoute a = Route(node), b = UnchartedRoutes.Generate(node, [7], _ => Theme(), Zones, [])!;
            Assert.Equal(a.Source, b.Source);
            Assert.Equal(a.Flags, b.Flags);
            Assert.Equal(JsonSerializer.Serialize(a.Zone), JsonSerializer.Serialize(b.Zone));
        }
        Assert.NotEqual(Route(1).Flags, Route(2).Flags);
    }

    [Fact]
    public void Exits_and_entrances_lead_to_each_other_and_every_exit_can_be_walked_to()
    {
        for (var node = 0; node < 40; node++)
        {
            var route = Route(node);
            int w = route.WidthTiles, h = route.Flags.Length / w;
            Assert.Equal(UnchartedRoutes.Unnamed, route.Zone.Location);
            Assert.InRange(route.Zone.Warps.Count, 2, 4);   // the entrance and 1–3 exits
            var entrance = route.Zone.Warps[0];
            Assert.True(entrance.Contains(route.EntranceX, route.EntranceY));
            if (node == 0)
                Assert.Equal((UnchartedRoutes.HomeZone, UnchartedRoutes.HomeWarp), (entrance.TargetZone, entrance.TargetWarp));
            else
                Assert.Equal((UnchartedRoutes.ZoneOf((node - 1) / 3), 1 + (node - 1) % 3), (entrance.TargetZone, entrance.TargetWarp));
            for (var k = 1; k < route.Zone.Warps.Count; k++)
                Assert.Equal((UnchartedRoutes.ZoneOf(3 * node + k), 0), (route.Zone.Warps[k].TargetZone, route.Zone.Warps[k].TargetWarp));

            // On foot from the entrance to every tile of every warp, and never out of the playable area (a cell of
            // scenery all round).
            var seen = new bool[w * h];
            var queue = new Queue<int>([route.EntranceY * w + route.EntranceX]);
            seen[queue.Peek()] = true;
            while (queue.TryDequeue(out var i))
            {
                int x = i % w, y = i / w;
                Assert.InRange(x, 32, w - 33);
                Assert.InRange(y, 32, h - 33);
                foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h && !seen[ny * w + nx] && (route.Flags[ny * w + nx] & 5) == 0)
                    {
                        seen[ny * w + nx] = true;
                        queue.Enqueue(ny * w + nx);
                    }
            }
            foreach (var warp in route.Zone.Warps)
                for (var y = warp.Y; y < warp.Y + warp.Height; y++)
                    for (var x = warp.X; x < warp.X + warp.Width; x++)
                        Assert.True(seen[y * w + x], $"route {node}: warp tile {x},{y} can't be reached");
        }
    }

    [Fact]
    public void A_design_is_one_path_through_forest_on_a_two_tile_grid()
    {
        for (var seed = 0; seed < 30; seed++)
        {
            const int w = 192, h = 128;
            var inner = (X: 32, Y: 32, W: 128, H: 64);
            var design = RouteLayout.Draw(new Random(seed), w, h, inner, 1 + seed % 3, grass: true, water: true);
            // Every 2×2 block is one kind (no tree is cut in half).
            for (var y = 0; y < h; y += 2)
                for (var x = 0; x < w; x += 2)
                    Assert.True(design.At(x, y) == design.At(x + 1, y) && design.At(x, y) == design.At(x, y + 1) && design.At(x, y) == design.At(x + 1, y + 1));
            // Mostly forest, with a real path and tall grass you can't avoid seeing.
            var counts = design.Kinds.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
            Assert.True(counts[TileKind.Tree] > w * h / 2);
            Assert.True(counts.GetValueOrDefault(TileKind.Path) >= 150, $"seed {seed}: {counts.GetValueOrDefault(TileKind.Path)} path tiles");
            Assert.True(counts.GetValueOrDefault(TileKind.Grass) >= 40, $"seed {seed}: {counts.GetValueOrDefault(TileKind.Grass)} grass tiles");
            // The edge of the playable area is forest except where the path leaves it.
            Assert.Equal(TileKind.Path, design.At(design.Ends[0].X, design.Ends[0].Y));
            Assert.InRange(design.Ends.Count, 2, 4);
            // Two blocks of forest all round the playable area, except at the ends.
            var open = Enumerable.Range(inner.Y, inner.H).Count(y => design.At(inner.X, y) != TileKind.Tree);
            Assert.Equal(6, open);   // the west entrance: the path and its verges
        }
    }

    [Fact]
    public void The_kit_draws_each_tile_with_a_real_tile_of_its_kind_and_parity()
    {
        var theme = Shared.Value;
        Assert.True(theme.Usable);
        var route = Route(5);
        int w = route.WidthTiles, exact = 0;
        var design = route.Source.Select(s => theme.Kind[s]).ToArray();
        for (var i = 0; i < route.Source.Length; i++)
        {
            int x = i % w, y = i / w, sx = route.Source[i] % theme.Width, sy = route.Source[i] / theme.Width;
            Assert.Equal((x & 1, y & 1), (sx & 1, sy & 1));
            // Collision follows the kind inside the playable area.
            if (x is >= 32 && x < w - 32 && y is >= 32 && y < route.Flags.Length / w - 32)
                Assert.Equal(design[i] is TileKind.Ground or TileKind.Path or TileKind.Grass, (route.Flags[i] & 5) == 0);
            var same = true;
            foreach (var (dx, dy) in new[] { (0, -1), (1, 0), (0, 1), (-1, 0) })
                if (x + dx >= 0 && x + dx < w && i + dy * w >= 0 && i + dy * w < design.Length)
                    same &= design[i + dy * w + dx] == theme.KindAt(sx + dx, sy + dy);
            if (same)
                exact++;
        }
        // Nearly every tile sits where a real one does (the made-up theme has all the usual surroundings).
        Assert.True(exact > route.Source.Length * 0.9, $"{exact}/{route.Source.Length} tiles in their real surroundings");
        Assert.Equal(route.Map.Width * route.Map.Height, route.Map.Cells.Count);
        Assert.True(UnchartedRoutes.TryParseCellKey(route.Map.Cells[^1].Tiles, out _, out var cx, out var cy));
        Assert.Equal(route.Flags[(cy * 32 + 5) * w + cx * 32 + 7], route.CellTiles(cx, cy)[5 * 32 + 7]);
    }

    [Theory]
    [InlineData(1, "grass01ax_lm1", "ki02dx_lm1", TileKind.Tree)]
    [InlineData(1, "gake1_1", "gake1_1", TileKind.Other)]
    [InlineData(0, "michi01a", "michi01a", TileKind.Path)]
    [InlineData(0, "grass01ax", "grass01ax", TileKind.Ground)]
    [InlineData(2, "grass01ax", "kusa_ec1", TileKind.Grass)]
    [InlineData(1, "sea_mizu1", "sea_mizu1_1", TileKind.Water)]
    [InlineData(0, "hashi01a", "hashi01a", TileKind.Other)]
    public void Tiles_are_classified_by_collision_and_texture(byte flags, string under, string seen, TileKind expected) =>
        Assert.Equal(expected, TerrainTheme.Classify(flags, under, seen));

    [Fact]
    public void Clipping_cuts_a_triangle_along_the_tile_grid_without_losing_area()
    {
        // One triangle over most of a 4×4-tile cell (positions centred on the cell: -2…2).
        var prim = new ChunkPrimitive(0, "ground", true, [-1.5f, 0, -1.5f, 1.5f, 0, -1.5f, -1.5f, 0, 1.5f], new float[6],
            Enumerable.Repeat(1f, 12).ToArray(), [0, 1, 2]);
        var tiles = ChunkGeometry.ClipToTiles([prim], 4);
        float Area(TileTriangle t) => Math.Abs((t.B.X - t.A.X) * (t.C.Z - t.A.Z) - (t.C.X - t.A.X) * (t.B.Z - t.A.Z)) / 2;
        Assert.Equal(4.5f, tiles.Sum(list => list.Sum(Area)), 3);
        Assert.Empty(tiles[3 * 4 + 3]);   // the far corner tile lies beyond the hypotenuse
        Assert.All(tiles[1 * 4 + 1], t => Assert.All(new[] { t.A, t.B, t.C }, v => Assert.InRange(v.X, -1.0001f, 0.0001f)));
    }
}

public class RouteFileTests
{
    /// <summary>A 64×32 area: a path in from the west along rows 10–11 to a clearing, and out of the north edge.</summary>
    static RouteFile Sample(int entranceX = -1, int entranceY = -1)
    {
        var kinds = Enumerable.Repeat(TileKind.Tree, 64 * 32).ToArray();
        for (var x = 0; x < 40; x++)
            kinds[10 * 64 + x] = kinds[11 * 64 + x] = TileKind.Path;
        for (var y = 0; y < 12; y++)
            kinds[y * 64 + 38] = kinds[y * 64 + 39] = TileKind.Ground;
        kinds[20 * 64 + 20] = TileKind.Grass;
        kinds[21 * 64 + 21] = TileKind.Water;
        return RouteFile.From(7, "Misty Hollow", 6, 64, 32, kinds, entranceX, entranceY);
    }

    [Fact]
    public void A_route_file_survives_saving_and_loading()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ir-routes-{Guid.NewGuid():N}");
        try
        {
            var file = Sample();
            RouteFiles.Save(dir, file);
            var loaded = RouteFiles.Load(dir, 7);
            Assert.NotNull(loaded);
            Assert.Equal(file.Kinds(), loaded.Kinds());
            Assert.Equal(("Misty Hollow", 6, 64, 32), (loaded.Name, loaded.Area, loaded.Width, loaded.Height));
            Assert.Equal([(7, "Misty Hollow")], RouteFiles.List(dir));
            Assert.Null(RouteFiles.Load(dir, 8));
            File.WriteAllText(RouteFiles.PathOf(dir, 8), "{ not json");
            Assert.Null(RouteFiles.Load(dir, 8));
            Assert.True(RouteFiles.Delete(dir, 7));
            Assert.Null(RouteFiles.Load(dir, 7));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_hand_made_route_carries_its_people_obstacles_stamps_links_scenery_and_wild_Pokemon()
    {
        var theme = InfiniteRouteTests.SharedTheme;
        // A 64×32 area: a path along rows 10–11 from the west edge to the east edge.
        var kinds = Enumerable.Repeat(TileKind.Tree, 64 * 32).ToArray();
        for (var x = 0; x < 64; x++)
            kinds[10 * 64 + x] = kinds[11 * 64 + x] = TileKind.Path;
        var outer = Enumerable.Range(0, 32 + 64).Select(y => new string(y == 0 ? '.' : 'T', 64 + 64)).ToList();
        var file = RouteFile.From(5, "Misty Hollow", 7, 64, 32, kinds) with
        {
            Outer = outer,
            Links = [new RouteLink(63, 10, 389, 2)],
            Npcs = [new RouteNpc(4, 10, 12, Facing: 3, Text: "Hello!\n\nGoodbye!", Trainer: 7, Sight: 3)],
            Obstacles = [new RouteObstacle(20, 11, "strength")],
            Stamps = [new RouteStamp(30, 2, 2, 2, 10, 10)],
            Wild = [new RouteWild(504, 3, 5, 20)],
            WildRate = 15,
        };
        var route = UnchartedRoutes.FromFile(file, theme, InfiniteRouteTests.TestZones, [])!;
        Assert.NotNull(route);
        Assert.Equal("Misty Hollow", route.Zone.Location);
        int w = route.WidthTiles;
        const int n = RouteFile.Margin;

        // The exit leads where its link says; the entrance keeps its default (route 1, exit 2 for node 5).
        Assert.Equal((389, 2), (route.Zone.Warps[1].TargetZone, route.Zone.Warps[1].TargetWarp));
        Assert.Equal((UnchartedRoutes.ZoneOf(1), 2), (route.Zone.Warps[0].TargetZone, route.Zone.Warps[0].TargetWarp));

        // People and obstacles become the zone's NPCs, moved into map coordinates.
        var person = route.Zone.Npcs.Single(p => p.Id == 0);
        Assert.Equal((4 + n, 10 + n, 12, Facing.Right, 3, 7), (person.X, person.Y, person.Sprite, person.Facing, person.Sight, person.TrainerId));
        Assert.Equal(["Hello!", "Goodbye!"], person.Talk!.Lines);
        var boulder = route.Zone.Npcs.Single(UnchartedRoutes.IsObstacle);
        Assert.Equal((UnchartedRoutes.StrengthScript, 20 + n, 11 + n), (boulder.Script, boulder.X, boulder.Y));

        // A stamp copies the theme's real tiles, look and collision.
        Assert.Equal(10 * theme.Width + 10, route.Source[(2 + n) * w + 30 + n]);
        Assert.Equal(theme.Flags[11 * theme.Width + 11], route.Flags[(3 + n) * w + 31 + n]);

        // Painted scenery is drawn as painted (the top row is ground there), but solid all the same.
        Assert.Equal(TileKind.Ground, theme.Kind[route.Source[5]]);
        Assert.Equal(1, route.Flags[5]);

        // Its own wild Pokémon.
        Assert.Equal((15, 504), (route.WildRate, route.Wild!.Single().Species));
    }

    [Fact]
    public void Ends_are_the_walkable_runs_on_the_edge_with_the_entrance_first()
    {
        // By default the west opening is the entrance; the north one is the exit.
        var ends = UnchartedRoutes.Ends(Sample());
        Assert.Equal([(0, 10, 2, true, Facing.Right), (38, 0, 2, false, Facing.Down)], ends);
        // Naming a tile of the north opening makes that the entrance.
        ends = UnchartedRoutes.Ends(Sample(39, 0));
        Assert.Equal([(38, 0, 2, false, Facing.Down), (0, 10, 2, true, Facing.Right)], ends);
        Assert.Empty(UnchartedRoutes.Ends(RouteFile.From(1, "", 6, 32, 32, Enumerable.Repeat(TileKind.Tree, 32 * 32).ToArray())));
    }
}
