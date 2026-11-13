namespace InfiniteRoutes.Extractor.Extraction;

/// <summary>What a tile is, for laying out and drawing Infinite Routes.</summary>
public enum TileKind : byte
{
    /// <summary>Anything a generated route doesn't use: cliffs, fences, bridges, stairs, buildings, raised ground.</summary>
    Other,
    /// <summary>Open ground you can walk on.</summary>
    Ground,
    /// <summary>The worn path.</summary>
    Path,
    /// <summary>Tall grass (walkable, wild Pokémon).</summary>
    Grass,
    /// <summary>Forest (solid).</summary>
    Tree,
    /// <summary>Water (solid until Surf).</summary>
    Water,
}

/// <summary>
/// A placed object on a texture set's real maps — a sign, a rock, a house, a vehicle (the map's building-pack models,
/// with their shadows) — as the tiles it stands on: a rectangle of the set's sample grid, named after its model.
/// </summary>
public sealed record RouteObject(string Name, int X, int Y, int W, int H);

/// <summary>
/// The look an Infinite Route is drawn in: one real route's texture set (<see cref="Area"/>) — its route cells and the
/// unnamed border cells drawn with the same textures — laid out as they sit on the overworld, as a grid of sample
/// tiles. For each tile: its collision, what <see cref="TileKind"/> it is, and its ground height.
/// </summary>
public sealed class TerrainTheme
{
    const int N = 32;

    public required int Area { get; init; }
    /// <summary>The real route it's named after (the first route in the set), whose wild Pokémon and scenery it borrows.</summary>
    public required string Name { get; init; }
    public required int SourceZone { get; init; }
    public required IReadOnlyList<WorldCell> Cells { get; init; }
    /// <summary>Sample grid size in tiles, and where its (0, 0) is on the overworld (in tiles).</summary>
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int OriginX { get; init; }
    public required int OriginY { get; init; }
    /// <summary>Per sample tile: the index of its cell in <see cref="Cells"/>, or -1 where the set has no cell.</summary>
    public required int[] CellOf { get; init; }
    public required byte[] Flags { get; init; }
    public required TileKind[] Kind { get; init; }
    /// <summary>Per sample tile: the height of the ground (the top opaque surface) at its centre.</summary>
    public required float[] Ground { get; init; }
    /// <summary>The ground level most of its walkable tiles are at (to the half tile): where routes are laid.</summary>
    public required float BaseHeight { get; init; }
    /// <summary>Per sample tile: whether a placed object (see <see cref="Objects"/>) stands over it.</summary>
    public required bool[] ObjectTile { get; init; }
    /// <summary>The placed objects on its maps, top to bottom.</summary>
    public required IReadOnlyList<RouteObject> Objects { get; init; }

    /// <summary>The tiles a route can be drawn from (<see cref="RouteKit"/>), worked out once.</summary>
    public RouteKit Kit => _kit ??= new RouteKit(this);
    RouteKit? _kit;

    /// <summary>Whether routes can be drawn from it: it has every kind of tile a layout uses.</summary>
    public bool Usable => Kit.Complete;

    public bool Has(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && CellOf[y * Width + x] >= 0;

    public TileKind KindAt(int x, int y) => Has(x, y) ? Kind[y * Width + x] : TileKind.Other;

    /// <summary>The real cell and its local tile a sample tile came from.</summary>
    public (WorldCell Cell, int X, int Y) Source(int x, int y)
    {
        var cell = Cells[CellOf[y * Width + x]];
        return (cell, x + OriginX - cell.X * N, y + OriginY - cell.Y * N);
    }

    /// <summary>The texture sets that make a theme: those with at least one route cell, smallest area id first.</summary>
    public static IReadOnlyList<int> Areas(GameAssets assets) =>
        assets.LoadOverworld()?.Cells.Where(c => c.Chunk >= 0 && c.Tiles is null && c.Location.StartsWith("Route", StringComparison.Ordinal))
            .Select(c => c.Area).Distinct().Order().ToList() ?? [];

    /// <summary>Every texture set of the overworld, with a name from the places drawn in it (for picking objects from).</summary>
    public static IReadOnlyList<(int Area, string Name)> AllAreas(GameAssets assets) =>
        assets.LoadOverworld()?.Cells.Where(c => c.Chunk >= 0 && c.Tiles is null).GroupBy(c => c.Area)
            .Select(g => (g.Key, Name: string.Join(", ", g.Select(c => c.Location).Where(l => l.Length > 0).Distinct().Take(3)) is { Length: > 0 } names ? names : "Unova's edge"))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? [];

    /// <param name="everything">
    /// Every cell drawn with the set — towns and cities too — not just its routes and border (to copy objects and
    /// pieces from, not to draw routes with).
    /// </param>
    public static TerrainTheme? Build(GameAssets assets, int area, bool everything = false)
    {
        if (assets.LoadOverworld() is not { } overworld || overworld.ChunkTiles != N)
            return null;
        var cells = overworld.Cells
            .Where(c => c.Area == area && c.Chunk >= 0 && c.Tiles is null
                && (everything || c.Location.Length == 0 || c.Location.StartsWith("Route", StringComparison.Ordinal)))
            .ToList();
        var route = cells.FirstOrDefault(c => c.Location.StartsWith("Route", StringComparison.Ordinal))
            ?? (everything ? cells.FirstOrDefault(c => c.Location.Length > 0) ?? cells.FirstOrDefault() : null);
        if (route is null)
            return null;
        int cx0 = cells.Min(c => c.X), cy0 = cells.Min(c => c.Y), cx1 = cells.Max(c => c.X), cy1 = cells.Max(c => c.Y);
        int width = (cx1 - cx0 + 1) * N, height = (cy1 - cy0 + 1) * N;
        var cellOf = new int[width * height];
        Array.Fill(cellOf, -1);
        var flags = new byte[width * height];
        var ground = new float[width * height];
        var kind = new TileKind[width * height];
        var objectName = new string?[width * height];
        var kept = new List<WorldCell>();
        foreach (var cell in cells)
        {
            byte[] tiles;
            List<ChunkPrimitive> prims;
            try
            {
                tiles = assets.ChunkTiles(cell.Chunk);
                prims = ChunkGeometry.Load(assets.ChunkModelPath(cell));
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
            {
                continue;
            }
            if (tiles.Length != N * N)
                continue;
            var index = kept.Count;
            kept.Add(cell);
            // The ground under each tile's centre, and what's seen there from above (tree canopies are cut-outs).
            var (h, under) = ChunkGeometry.Surface(prims, N, 1);
            var (_, seen) = ChunkGeometry.Surface(prims, N, 1, opaqueOnly: false);
            // Placed objects (buildings, signs, rocks: the building pack's "_lm" materials) — but not the forest
            // blocks, which are placed the same way.
            var objectPrims = prims.Where(p => IsObjectMaterial(p.MaterialName)).ToList();
            var objects = ChunkGeometry.Covered(objectPrims, N).ToHashSet();
            int ox = (cell.X - cx0) * N, oy = (cell.Y - cy0) * N;
            // Which model each object tile belongs to (shadows don't name one).
            foreach (var prim in objectPrims.Where(p => !p.MaterialName.Contains("kage", StringComparison.Ordinal)))
                foreach (var (x, y) in ChunkGeometry.Covered([prim], N))
                    objectName[(oy + y) * width + ox + x] ??= ObjectName(prim.MaterialName);
            for (var ly = 0; ly < N; ly++)
            for (var lx = 0; lx < N; lx++)
            {
                var t = (oy + ly) * width + ox + lx;
                var i = ly * N + lx;
                cellOf[t] = index;
                flags[t] = tiles[i];
                ground[t] = h[i];
                kind[t] = objects.Contains((lx, ly)) ? TileKind.Other : Classify(tiles[i], under[i], seen[i]);
            }
        }
        if (kept.Count == 0)
            return null;

        var levels = Enumerable.Range(0, kind.Length)
            .Where(t => kind[t] is TileKind.Ground or TileKind.Path or TileKind.Grass && ground[t] > ChunkGeometry.NoGround)
            .GroupBy(t => MathF.Round(ground[t] * 2) / 2).OrderByDescending(g => g.Count()).FirstOrDefault();
        var baseHeight = levels?.Key ?? 0;
        // Routes are flat: land off the main level (ledges, plateaus, hills) isn't used.
        for (var t = 0; t < kind.Length; t++)
            if (kind[t] is TileKind.Ground or TileKind.Path or TileKind.Grass or TileKind.Tree && Math.Abs(ground[t] - baseHeight) > 0.3f)
                kind[t] = TileKind.Other;
        // Objects: tiles under placed models, joined where they touch (a house and its shadow are one object).
        var isObject = new bool[width * height];
        var found = new List<RouteObject>();
        var visited = new bool[width * height];
        for (var start = 0; start < kind.Length; start++)
        {
            if (visited[start] || objectName[start] is null)
                continue;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            var names = new Dictionary<string, int>();
            var queue = new Queue<int>([start]);
            visited[start] = true;
            while (queue.TryDequeue(out var t))
            {
                int x = t % width, y = t / width;
                (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
                names[objectName[t]!] = names.GetValueOrDefault(objectName[t]!) + 1;
                isObject[t] = true;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx >= 0 && ny >= 0 && nx < width && ny < height && !visited[ny * width + nx] && objectName[ny * width + nx] is not null)
                        {
                            visited[ny * width + nx] = true;
                            queue.Enqueue(ny * width + nx);
                        }
                    }
            }
            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            if (w <= 64 && h <= 64)
                found.Add(new RouteObject(names.MaxBy(n => n.Value).Key, x0, y0, w, h));
        }
        return new TerrainTheme
        {
            ObjectTile = isObject,
            Objects = found.OrderBy(o => o.Y).ThenBy(o => o.X).ToList(),
            Area = area,
            Name = route.Location,
            SourceZone = route.Zone,
            Cells = kept,
            Width = width,
            Height = height,
            OriginX = cx0 * N,
            OriginY = cy0 * N,
            CellOf = cellOf,
            Flags = flags,
            Kind = kind,
            Ground = ground,
            BaseHeight = baseHeight,
        };
    }

    static bool IsTree(string material) => material.StartsWith("ki0", StringComparison.Ordinal);

    /// <summary>
    /// A material of a placed model (the building pack's, "…_lm1"): signs, rocks, houses, vehicles and their shadows —
    /// not the forest blocks and ground placed the same way.
    /// </summary>
    public static bool IsObjectMaterial(string material) => material.Contains("_lm", StringComparison.Ordinal)
        && !IsTree(material) && !material.StartsWith("grass", StringComparison.Ordinal);

    /// <summary>A model's name from its material: "sign01a_lm1" → "sign01a".</summary>
    static string ObjectName(string material) => material[..material.IndexOf("_lm", StringComparison.Ordinal)];

    static bool IsWater(string material) =>
        material.StartsWith("sea", StringComparison.Ordinal) || material.Contains("mizu", StringComparison.Ordinal)
        || material.StartsWith("ike", StringComparison.Ordinal) || material.StartsWith("kawa", StringComparison.Ordinal);

    /// <summary>
    /// A tile's kind from its collision and its textures (Black/White's names): <paramref name="under"/> is the ground
    /// texture at its centre, <paramref name="seen"/> the topmost one there counting cut-outs. Trees are "ki…", paths
    /// "michi…", water "sea…", "…mizu…", "ike…" (ponds) and "kawa…" (rivers).
    /// </summary>
    public static TileKind Classify(byte flags, string? under, string? seen)
    {
        if (under is null)
            return TileKind.Other;
        if ((flags & 4) != 0 || IsWater(under) || seen is not null && IsWater(seen))
            return (flags & 5) != 0 ? TileKind.Water : TileKind.Other;
        if ((flags & 1) != 0)
            return seen is not null && IsTree(seen) ? TileKind.Tree : TileKind.Other;
        if ((flags & 2) != 0)
            return TileKind.Grass;
        if (under.StartsWith("michi", StringComparison.Ordinal))
            return TileKind.Path;
        // Open ground: grass or sand underfoot (not bridges, stairs or cliff paths).
        return under.StartsWith("grass", StringComparison.Ordinal) || under.StartsWith("sabaku", StringComparison.Ordinal)
            || under.StartsWith("yamagrs", StringComparison.Ordinal) ? TileKind.Ground : TileKind.Other;
    }
}
