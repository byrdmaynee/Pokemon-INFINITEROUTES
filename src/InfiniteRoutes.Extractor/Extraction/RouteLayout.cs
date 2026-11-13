namespace InfiniteRoutes.Extractor.Extraction;

/// <summary>
/// An Infinite Route's design: what every tile is, where its ends are (the entrance on the west edge first, then the
/// exits; each the first tile of its two-tile-wide path and the way you face arriving there) and its item spots (the
/// ends of its side pockets).
/// </summary>
public sealed record RouteDesign(int Width, int Height, TileKind[] Kinds, IReadOnlyList<(int X, int Y, Facing Inward)> Ends,
    IReadOnlyList<(int X, int Y)> Pockets)
{
    public TileKind At(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height ? Kinds[y * Width + x] : TileKind.Tree;
}

/// <summary>
/// Designs Infinite Routes by rule, the way Black/White's own routes read: one clear path through forest, in straight
/// runs with right-angle bends; a strip of open ground either side of it; clearings where it turns; tall grass laid
/// right across it (so you have to walk through) and in the clearings; a side pocket or two off it (for items, later
/// trainers); sometimes a pond. Everything else is forest.
/// <para>
/// It's drawn on a grid of 2×2-tile blocks, since the forest is made of trees two tiles wide: every edge then falls
/// between trees, never through one.
/// </para>
/// </summary>
public static class RouteLayout
{
    /// <summary>Blocks of open ground either side of the path.</summary>
    const int Verge = 1;

    /// <summary>
    /// A design for a <paramref name="width"/>×<paramref name="height"/>-tile map (even numbers) whose playable area
    /// is <paramref name="inner"/> (even too): the path runs from the area's west edge to its east edge, with
    /// <paramref name="exits"/> − 1 branches to its north and south edges; outside the area is forest, except that the
    /// path carries on a little past each end (for the look).
    /// </summary>
    public static RouteDesign Draw(Random rng, int width, int height, (int X, int Y, int W, int H) inner, int exits, bool grass, bool water)
    {
        int w = width / 2, h = height / 2;
        var kinds = new TileKind[w * h];
        Array.Fill(kinds, TileKind.Tree);
        int x0 = inner.X / 2, y0 = inner.Y / 2, x1 = (inner.X + inner.W) / 2 - 1, y1 = (inner.Y + inner.H) / 2 - 1;
        // Rows and columns the path's middle may use: clear of the area's edge by the verge and two blocks of forest.
        int top = y0 + Verge + 2, bottom = y1 - Verge - 2, left = x0 + Verge + 2, right = x1 - Verge - 2;

        bool In(int x, int y) => x >= 0 && y >= 0 && x < w && y < h;
        void Set(int x, int y, TileKind kind)
        {
            if (In(x, y))
                kinds[y * w + x] = kind;
        }
        TileKind Get(int x, int y) => In(x, y) ? kinds[y * w + x] : TileKind.Tree;

        // The path's middle line, block by block, in straight runs.
        var line = new List<(int X, int Y)>();
        void Run(int ax, int ay, int bx, int by)
        {
            for (int x = ax, y = ay; ; x += Math.Sign(bx - ax), y += Math.Sign(by - ay))
            {
                if (line.Count == 0 || line[^1] != (x, y))
                    line.Add((x, y));
                if ((x, y) == (bx, by))
                    break;
            }
        }

        // Waypoints west to east; each leg goes along, then up or down (or the other way round).
        var points = new List<(int X, int Y)> { (x0, rng.Next(top + 1, bottom)) };
        while (points[^1].X < x1)
        {
            var (px, py) = points[^1];
            var nx = Math.Min(x1, px + rng.Next(8, 13));
            if (x1 - nx < 6)
                nx = x1;
            // The last leg runs straight out of the east edge; the others end on a new row, well away from the last.
            var ny = py;
            for (var tries = 0; nx != x1 && tries < 8 && Math.Abs(ny - py) < 6; tries++)
                ny = rng.Next(top, bottom + 1);
            points.Add((nx, ny));
        }
        var endedUpOrDown = false;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (ax, ay) = points[i];
            var (bx, by) = points[i + 1];
            // The first leg comes straight in from the west edge. The others turn first or last — but never straight
            // after a run up or down (it could double back along it, leaving a dead-end spur).
            if (i == 0 || endedUpOrDown || rng.Next(2) == 0)
            {
                Run(ax, ay, bx, ay);
                Run(bx, ay, bx, by);
                endedUpOrDown = ay != by;
            }
            else
            {
                Run(ax, ay, ax, by);
                Run(ax, by, bx, by);
                endedUpOrDown = false;
            }
        }
        var ends = new List<(int X, int Y, Facing Inward)> { (x0, points[0].Y, Facing.Right), (x1, points[^1].Y, Facing.Left) };
        var main = line.ToList();

        // Branches: from a turning point in the middle of the route, straight to the north or south edge.
        var corners = points.Skip(1).Take(points.Count - 2).Where(p => p.X > left + 2 && p.X < right - 2).ToList();
        var branches = new List<List<(int X, int Y)>>();
        for (var k = 1; k < exits && corners.Count > 0; k++)
        {
            var from = corners[rng.Next(corners.Count)];
            corners.RemoveAll(c => Math.Abs(c.X - from.X) < 6);
            var north = k == 1 ? rng.Next(2) == 0 : !ends.Any(e => e.Inward == Facing.Down);
            var branch = new List<(int X, int Y)>();
            for (var y = from.Y; north ? y >= y0 : y <= y1; y += north ? -1 : 1)
                branch.Add((from.X, y));
            // Don't run a branch along the main path (it would just widen it).
            if (branch.Skip(2).Any(b => main.Any(m => Math.Abs(m.X - b.X) <= 2 * Verge + 1 && m.Y == b.Y && m != from)))
                continue;
            branches.Add(branch);
            ends.Add((from.X, north ? y0 : y1, north ? Facing.Down : Facing.Up));
        }
        // Past each end, out of the playable area (for the look).
        var beyond = new List<(int X, int Y)>();
        foreach (var (ex, ey, inward) in ends)
        {
            var (dx, dy) = inward switch { Facing.Right => (-1, 0), Facing.Left => (1, 0), Facing.Down => (0, -1), _ => (0, 1) };
            for (var k = 1; k <= 5; k++)
                beyond.Add((ex + dx * k, ey + dy * k));
        }

        var all = main.Concat(branches.SelectMany(b => b)).Concat(beyond).ToList();
        foreach (var (x, y) in all)
            for (var dy = -Verge; dy <= Verge; dy++)
                for (var dx = -Verge; dx <= Verge; dx++)
                    Set(x + dx, y + dy, TileKind.Ground);

        // Clearings at some of the turns.
        var clearings = new List<(int X, int Y, int W, int H)>();
        foreach (var (px, py) in points.Skip(1).Take(points.Count - 2))
        {
            if (rng.Next(4) == 0)
                continue;
            int cw = rng.Next(5, 9), ch = rng.Next(4, 7);
            int cx = Math.Clamp(px - cw / 2, x0 + 2, x1 - 1 - cw), cy = Math.Clamp(py - ch / 2, y0 + 2, y1 - 1 - ch);
            clearings.Add((cx, cy, cw, ch));
            for (var y = cy; y < cy + ch; y++)
                for (var x = cx; x < cx + cw; x++)
                    Set(x, y, TileKind.Ground);
        }

        // Side pockets: a short dead end off a straight run, into the forest.
        var pockets = new List<(int X, int Y)>();
        for (var tries = 0; tries < 12 && pockets.Count < 2; tries++)
        {
            var i = rng.Next(3, Math.Max(4, main.Count - 3));
            var (px, py) = main[i];
            var horizontal = main[i - 1].Y == py && main[i + 1].Y == py;
            var vertical = main[i - 1].X == px && main[i + 1].X == px;
            if (!horizontal && !vertical)
                continue;
            var side = rng.Next(2) == 0 ? -1 : 1;
            var (dx, dy) = horizontal ? (0, side) : (side, 0);
            var length = rng.Next(3, 6);
            int ex = px + dx * (Verge + length), ey = py + dy * (Verge + length);
            // Room for it: forest all the way, and two blocks of forest left beyond it.
            var clear = ex - 2 >= x0 + 2 && ex + 2 <= x1 - 2 && ey - 2 >= y0 + 2 && ey + 2 <= y1 - 2;
            for (var k = Verge + 1; clear && k <= Verge + length + 1; k++)
                for (var a = -2; a <= 2; a++)
                    clear &= Get(px + dx * k + (dx == 0 ? a : 0), py + dy * k + (dy == 0 ? a : 0)) == TileKind.Tree;
            if (!clear)
                continue;
            for (var k = Verge + 1; k <= Verge + length; k++)
                Set(px + dx * k, py + dy * k, TileKind.Ground);
            // A little room at the end.
            for (var a = -1; a <= 0; a++)
                Set(ex + (dx == 0 ? a : 0), ey + (dy == 0 ? a : 0), TileKind.Ground);
            pockets.Add((ex * 2, ey * 2));
        }

        // A pond in a clearing, off the path.
        if (water && clearings.Count > 0 && rng.Next(3) == 0)
        {
            var (cx, cy, cw, ch) = clearings[rng.Next(clearings.Count)];
            for (var tries = 0; tries < 6; tries++)
            {
                int px = rng.Next(cx, cx + cw - 1), py = rng.Next(cy, cy + ch - 1);
                if (Enumerable.Range(-1, 4).Any(dy => Enumerable.Range(-1, 4).Any(dx => all.Contains((px + dx, py + dy)))))
                    continue;
                for (var dy = 0; dy < 2; dy++)
                    for (var dx = 0; dx < 2; dx++)
                        Set(px + dx, py + dy, TileKind.Water);
                break;
            }
        }

        // Tall grass: right across the path in a couple of places, and part of each clearing.
        if (grass)
        {
            var gates = 1 + inner.W / 64 + rng.Next(2);
            for (var g = 0; g < gates * 4 && gates > 0; g++)
            {
                var i = rng.Next(4, Math.Max(5, main.Count - 6));
                var length = rng.Next(2, 5);
                if (i + length >= main.Count - 3)
                    continue;
                var stretch = main.Skip(i).Take(length).ToList();
                // A straight stretch, not already grass and not at a branch.
                if (stretch.Any(p => p.Y != stretch[0].Y) && stretch.Any(p => p.X != stretch[0].X)
                    || stretch.Any(p => Get(p.X, p.Y) == TileKind.Grass || branches.Any(b => b.Contains(p))))
                    continue;
                foreach (var (x, y) in stretch)
                    for (var dy = -Verge; dy <= Verge; dy++)
                        for (var dx = -Verge; dx <= Verge; dx++)
                            if (Get(x + dx, y + dy) == TileKind.Ground)
                                Set(x + dx, y + dy, TileKind.Grass);
                gates--;
            }
            foreach (var (cx, cy, cw, ch) in clearings)
            {
                // One side of the clearing (a block in from its edge would leave slivers).
                int gw = rng.Next(3, Math.Max(4, cw)), gh = rng.Next(2, Math.Max(3, ch));
                int gx = rng.Next(2) == 0 ? cx : cx + cw - gw, gy = rng.Next(2) == 0 ? cy : cy + ch - gh;
                for (var y = gy; y < gy + gh; y++)
                    for (var x = gx; x < gx + gw; x++)
                        if (Get(x, y) == TileKind.Ground)
                            Set(x, y, TileKind.Grass);
            }
        }

        // The worn path itself, down the middle (through the grass gates it gives way to the grass).
        foreach (var (x, y) in all)
            if (Get(x, y) == TileKind.Ground)
                Set(x, y, TileKind.Path);

        // Blocks to tiles.
        var tiles = new TileKind[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                tiles[y * width + x] = kinds[y / 2 * w + x / 2];
        return new RouteDesign(width, height, tiles, ends.Select(e => (e.X * 2, e.Y * 2, e.Inward)).ToList(), pockets);
    }
}
