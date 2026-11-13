using Godot;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// Draws an Infinite Route's cell (<see cref="UnchartedRoutes"/>): for each of its tiles, the triangles that lie over
/// the real tile it was quilted from (<see cref="GeneratedRoute.Source"/>), cut from that tile's chunk model in the
/// current season and moved into place — ground, tall grass, and the parts of trees and cliffs over it — with the
/// model's own materials.
/// </summary>
static class RouteTerrain
{
    const int N = UnchartedRoutes.CellTiles;

    sealed record Model(List<TileTriangle>[] Tiles, Godot.Collections.Array<Material> Materials);

    static readonly Dictionary<string, Model?> Models = new();

    static Model? Load(string path)
    {
        if (Models.TryGetValue(path, out var model))
            return model;
        try
        {
            var doc = new GltfDocument();
            var state = new GltfState();
            model = doc.AppendFromFile(path, state) == Error.Ok
                ? new Model(ChunkGeometry.ClipToTiles(ChunkGeometry.Load(path), N), state.GetMaterials())
                : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or FormatException or KeyNotFoundException)
        {
            GD.PushWarning($"Couldn't read {path}: {e.Message}");
            model = null;
        }
        Models[path] = model;
        return model;
    }

    /// <summary>The cell's terrain, centred on the cell like a chunk model; null if it isn't a generated route's cell.</summary>
    public static Node3D? Build(GameAssets assets, WorldCell cell, Season season) =>
        UnchartedRoutes.TryParseCellKey(cell.Tiles, out var node, out var cx, out var cy)
        && assets.Route(UnchartedRoutes.ZoneOf(node)) is { } route ? Build(assets, route, cx, cy, season) : null;

    /// <summary>Cell (<paramref name="cx"/>, <paramref name="cy"/>) of <paramref name="route"/> (the route editor draws unsaved routes).</summary>
    public static Node3D Build(GameAssets assets, GeneratedRoute route, int cx, int cy, Season season)
    {
        var node = route.Node;
        var theme = route.Theme;
        var surfaces = new Dictionary<(Model Model, int Material), SurfaceTool>();
        for (var ty = 0; ty < N; ty++)
        for (var tx = 0; tx < N; tx++)
        {
            int gx = cx * N + tx, gy = cy * N + ty;
            if (gx >= route.WidthTiles || gy * route.WidthTiles + gx >= route.Source.Length)
                continue;
            var src = route.Source[gy * route.WidthTiles + gx];
            if (src < 0)
                continue;
            var (source, lx, ly) = theme.Source(src % theme.Width, src / theme.Width);
            if (Load(assets.ChunkModelPath(source, season)) is not { } model)
                continue;
            // From the source tile's place in its chunk to this tile's place in this cell.
            float dx = tx - lx, dz = ty - ly;
            foreach (var tri in model.Tiles[ly * N + lx])
            {
                if (tri.Material < 0 || tri.Material >= model.Materials.Count)
                    continue;
                if (!surfaces.TryGetValue((model, tri.Material), out var st))
                {
                    st = new SurfaceTool();
                    st.Begin(Mesh.PrimitiveType.Triangles);
                    st.SetMaterial(model.Materials[tri.Material]);
                    surfaces[(model, tri.Material)] = st;
                }
                // glTF winds counter-clockwise; Godot's front faces are clockwise.
                Add(st, tri.A, dx, dz);
                Add(st, tri.C, dx, dz);
                Add(st, tri.B, dx, dz);
            }
        }
        var mesh = new ArrayMesh();
        foreach (var st in surfaces.Values)
            st.Commit(mesh);
        var root = new Node3D { Name = $"route{node}-{cx}-{cy}" };
        root.AddChild(new MeshInstance3D { Mesh = mesh, Name = "terrain" });
        return root;
    }

    static void Add(SurfaceTool st, TileVertex v, float dx, float dz)
    {
        st.SetColor(new Color(v.R, v.G, v.B, v.Alpha));
        st.SetUV(new Vector2(v.U, v.V));
        st.AddVertex(new Vector3(v.X + dx, v.Y, v.Z + dz));
    }
}
