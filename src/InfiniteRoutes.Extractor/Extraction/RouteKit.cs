namespace InfiniteRoutes.Extractor.Extraction;

/// <summary>
/// The tiles an Infinite Route is drawn from: for a tile of a given <see cref="TileKind"/> whose eight neighbours are
/// of given kinds, a real tile of the theme that sits in the same surroundings — so a path tile with grass above it is
/// drawn by a real path-edge tile, a forest tile at a corner by a real forest corner. Tiles are also matched on the
/// parity of their position (x and y, odd or even): Black/White's trees and tall grass are two tiles wide, so a tile
/// only continues its neighbour's tree when it comes from the same half.
/// </summary>
public sealed class RouteKit
{
    static readonly (int X, int Y)[] Around = [(0, -1), (1, 0), (0, 1), (-1, 0), (-1, -1), (1, -1), (1, 1), (-1, 1)];

    readonly TerrainTheme _theme;
    readonly Dictionary<long, List<int>> _exact = new();
    readonly Dictionary<int, List<(int Tile, long Key)>> _byKind = new();
    readonly Dictionary<long, int[]> _nearest = new();

    public RouteKit(TerrainTheme theme)
    {
        _theme = theme;
        for (var y = 0; y < theme.Height; y++)
            for (var x = 0; x < theme.Width; x++)
            {
                var kind = theme.KindAt(x, y);
                if (kind == TileKind.Other || Around.Any(d => !theme.Has(x + d.X, y + d.Y)))
                    continue;
                var key = Key(kind, x + theme.OriginX, y + theme.OriginY, d => theme.KindAt(x + d.X, y + d.Y));
                var tile = y * theme.Width + x;
                if (!_exact.TryGetValue(key, out var list))
                    _exact[key] = list = [];
                list.Add(tile);
                var group = (int)(key & 0x1F);
                if (!_byKind.TryGetValue(group, out var all))
                    _byKind[group] = all = [];
                all.Add((tile, key));
            }
    }

    /// <summary>Kind (3 bits), parity (2 bits), then the eight neighbours' kinds (3 bits each; edges first, then corners).</summary>
    static long Key(TileKind kind, int x, int y, Func<(int X, int Y), TileKind> neighbour)
    {
        long key = (long)kind | (long)(x & 1) << 3 | (long)(y & 1) << 4;
        for (var i = 0; i < Around.Length; i++)
            key |= (long)neighbour(Around[i]) << (5 + i * 3);
        return key;
    }

    /// <summary>
    /// Whether it has what every layout needs: enough forest, open ground and path tiles for both parities of x and
    /// y (a handful of path tiles can't draw a route), and forest tiles wholly surrounded by forest.
    /// </summary>
    public bool Complete => new[] { TileKind.Tree, TileKind.Ground, TileKind.Path }.All(kind =>
        Enumerable.Range(0, 4).All(parity => _byKind.TryGetValue((int)kind | parity << 3, out var tiles) && tiles.Count >= (kind == TileKind.Tree ? 250 : 30)))
        && Enumerable.Range(0, 4).All(parity => _exact.ContainsKey(Interior(TileKind.Tree, parity)));

    public bool Has(TileKind kind) => Enumerable.Range(0, 4).All(parity => _byKind.ContainsKey((int)kind | parity << 3));

    static long Interior(TileKind kind, int parity) => Key(kind, parity & 1, parity >> 1, _ => kind);

    /// <summary>
    /// A real tile (y·theme.Width + x) to draw the tile at (<paramref name="x"/>, <paramref name="y"/>) of a layout
    /// with: of the same kind and parity and, when the theme has one, in the same surroundings; otherwise in the most
    /// alike surroundings (edge neighbours count double). <paramref name="exact"/> says which it was. -1 if the theme
    /// has no tile of that kind at all.
    /// </summary>
    public int Pick(int x, int y, Func<int, int, TileKind> layout, uint salt, out bool exact)
    {
        var kind = layout(x, y);
        var key = Key(kind, x, y, d => layout(x + d.X, y + d.Y));
        var hash = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ salt;
        hash ^= hash >> 13;
        hash *= 0x5bd1e995;
        hash ^= hash >> 15;
        if (_exact.TryGetValue(key, out var same))
        {
            exact = true;
            return same[(int)(hash % (uint)same.Count)];
        }
        exact = false;
        int[]? near;
        lock (_nearest)
            _nearest.TryGetValue(key, out near);
        if (near is null)
        {
            if (!_byKind.TryGetValue((int)(key & 0x1F), out var all))
                return -1;
            var best = int.MaxValue;
            var picks = new List<int>();
            foreach (var (tile, other) in all)
            {
                var miss = 0;
                for (var i = 0; i < Around.Length; i++)
                {
                    var theirs = (TileKind)((other >> (5 + i * 3)) & 7);
                    if (((key >> (5 + i * 3)) & 7) == (long)theirs)
                        continue;
                    // A stray shore, cliff or object beside the tile shows far more than the wrong kind of ground.
                    miss += (i < 4 ? 2 : 1) * (theirs is TileKind.Water or TileKind.Other ? 4 : 1);
                }
                if (miss < best)
                {
                    best = miss;
                    picks.Clear();
                }
                if (miss == best)
                    picks.Add(tile);
            }
            near = picks.ToArray();
            lock (_nearest)
                _nearest[key] = near;
        }
        return near[(int)(hash % (uint)near.Length)];
    }
}
