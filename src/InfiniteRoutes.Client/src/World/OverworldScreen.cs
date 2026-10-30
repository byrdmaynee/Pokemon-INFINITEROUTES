using Godot;
using InfiniteRoutes.Client.Ui;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// Offline test world (M1b): walk around the Unova overworld extracted from the player's ROM.
/// Chunks stream in around the player; movement is tile-based with the ROM's collision grid,
/// and ground height comes from raycasting the map geometry.
/// </summary>
public partial class OverworldScreen : Control
{
    const int ChunkTiles = 32;
    const int LoadRadius = 1;
    const float WalkSeconds = 0.26f;
    const float RunSeconds = 0.13f;
    const int StartZone = 389;   // Nuvema Town

    WorldIndex _index = null!;
    GameAssets _assets = null!;
    readonly Dictionary<(int, int), WorldCell> _cellsByPos = new();
    readonly Dictionary<int, byte[]> _tiles = new();
    readonly Dictionary<(int, int), Node3D> _loaded = new();

    Node3D _world = null!;
    Node3D _player = null!;
    Camera3D _camera = null!;
    Label _hud = null!;

    Vector2I _tile;
    Vector2I _dir = Vector2I.Down;
    bool _moving;
    float _height;

    public static bool Available => ClientServices.UnovaAssets?.LoadOverworld() is not null;

    public override void _Ready()
    {
        if (ClientServices.UnovaAssets is not { } assets || assets.LoadOverworld() is not { } index)
        {
            var missing = Page.Create("Offline Test World", Ui.Ui.Text(
                "No map data yet. Re-extract your Black/White ROM in Settings → ROM Library.", 18, Ui.Ui.Warn, wrap: true));
            AddChild(missing);
            missing.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            return;
        }
        _assets = assets;
        _index = index;
        foreach (var c in index.Cells)
            _cellsByPos[(c.X, c.Y)] = c;

        var container = new SubViewportContainer { Stretch = true };
        AddChild(container);
        container.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var viewport = new SubViewport { Msaa3D = Viewport.Msaa.Disabled, HandleInputLocally = false };
        container.AddChild(viewport);

        _world = new Node3D();
        viewport.AddChild(_world);
        _world.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.55f, 0.75f, 0.95f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = Colors.White,
            },
        });

        _player = BuildPlayer();
        _world.AddChild(_player);
        _camera = new Camera3D { Fov = 32, Current = true };
        _world.AddChild(_camera);

        _hud = Ui.Ui.Text("", 18);
        _hud.AddThemeStyleboxOverride("normal", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.55f), ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 6, ContentMarginBottom = 6,
        });
        AddChild(_hud);
        _hud.Position = new Vector2(16, 16);
        var help = Ui.Ui.Text("Arrows / WASD: walk · Shift: run · Esc: menu", 14, Ui.Ui.Muted);
        AddChild(help);
        help.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomLeft);
        help.Position = new Vector2(16, -36);

        _tile = FindStartTile();
        StreamChunks();
        _player.Position = TileCenter(_tile, 0);
        UpdateCamera(snap: true);
        UpdateHud();
        SettleOnGround();
    }

    /// <summary>New collision shapes only reach the physics space after a physics frame.</summary>
    async void SettleOnGround()
    {
        _moving = true;
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        if (!IsInstanceValid(this))
            return;
        _height = GroundHeight(_tile);
        _player.Position = TileCenter(_tile, _height);
        UpdateCamera(snap: true);
        _moving = false;
    }

    static Node3D BuildPlayer()
    {
        // Placeholder body until overworld character sprites are extracted.
        var root = new Node3D();
        var body = new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = 0.3f, Height = 1.2f },
            Position = new Vector3(0, 0.6f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.9f, 0.25f, 0.25f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
        };
        var nose = new MeshInstance3D
        {
            Name = "Facing",
            Mesh = new BoxMesh { Size = new Vector3(0.2f, 0.2f, 0.35f) },
            Position = new Vector3(0, 0.9f, 0.35f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Colors.White, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
        };
        root.AddChild(body);
        root.AddChild(nose);
        return root;
    }

    Vector2I FindStartTile()
    {
        var cell = _index.Cells.FirstOrDefault(c => c.Zone == StartZone) ?? _index.Cells[0];
        var origin = new Vector2I(cell.X * ChunkTiles, cell.Y * ChunkTiles);
        // Spiral out from the chunk centre to the nearest walkable tile.
        for (var r = 0; r < ChunkTiles; r++)
        for (var dy = -r; dy <= r; dy++)
        for (var dx = -r; dx <= r; dx++)
        {
            var t = origin + new Vector2I(ChunkTiles / 2 + dx, ChunkTiles / 2 + dy);
            if (!IsBlocked(t))
                return t;
        }
        return origin + new Vector2I(ChunkTiles / 2, ChunkTiles / 2);
    }

    public override void _Process(double delta)
    {
        if (_world is null)
            return;
        if (!_moving)
        {
            var input = Vector2I.Zero;
            if (Input.IsActionPressed("ui_up") || Input.IsPhysicalKeyPressed(Key.W)) input = Vector2I.Up;
            else if (Input.IsActionPressed("ui_down") || Input.IsPhysicalKeyPressed(Key.S)) input = Vector2I.Down;
            else if (Input.IsActionPressed("ui_left") || Input.IsPhysicalKeyPressed(Key.A)) input = Vector2I.Left;
            else if (Input.IsActionPressed("ui_right") || Input.IsPhysicalKeyPressed(Key.D)) input = Vector2I.Right;
            if (input != Vector2I.Zero)
                TryStep(input, Input.IsKeyPressed(Key.Shift));
        }
        UpdateCamera(snap: false, (float)delta);
    }

    /// <summary>Moves one tile in a direction if it isn't blocked. Public so smoke tests can drive it.</summary>
    public bool TryStep(Vector2I dir, bool run = false)
    {
        if (_moving)
            return false;
        _dir = dir;
        _player.Rotation = new Vector3(0, Mathf.Atan2(dir.X, dir.Y), 0);
        var target = _tile + dir;
        if (IsBlocked(target))
            return false;

        _moving = true;
        _tile = target;
        StreamChunks();
        _height = GroundHeight(_tile, _height);
        var tween = CreateTween();
        tween.TweenProperty(_player, "position", TileCenter(_tile, _height), run ? RunSeconds : WalkSeconds);
        tween.Finished += () =>
        {
            _moving = false;
            UpdateHud();
        };
        return true;
    }

    public Vector2I PlayerTile => _tile;

    public bool IsWalkable(Vector2I t) => !IsBlocked(t);

    /// <summary>Name of the location the tile belongs to ("" for scenery).</summary>
    public string LocationAt(Vector2I t) =>
        _cellsByPos.TryGetValue((Mathf.FloorToInt(t.X / (float)ChunkTiles), Mathf.FloorToInt(t.Y / (float)ChunkTiles)), out var c) ? c.Location : "";

    bool IsBlocked(Vector2I t)
    {
        var cx = Mathf.FloorToInt(t.X / (float)ChunkTiles);
        var cy = Mathf.FloorToInt(t.Y / (float)ChunkTiles);
        if (!_cellsByPos.TryGetValue((cx, cy), out var cell))
            return true;
        if (!_tiles.TryGetValue(cell.Chunk, out var tiles))
            _tiles[cell.Chunk] = tiles = _assets.ChunkTiles(cell.Chunk);
        var lx = t.X - cx * ChunkTiles;
        var ly = t.Y - cy * ChunkTiles;
        return (tiles[ly * ChunkTiles + lx] & 1) != 0;
    }

    static Vector3 TileCenter(Vector2I t, float y) => new(t.X + 0.5f, y, t.Y + 0.5f);

    void StreamChunks()
    {
        var cx = Mathf.FloorToInt(_tile.X / (float)ChunkTiles);
        var cy = Mathf.FloorToInt(_tile.Y / (float)ChunkTiles);
        var wanted = new HashSet<(int, int)>();
        for (var dy = -LoadRadius; dy <= LoadRadius; dy++)
        for (var dx = -LoadRadius; dx <= LoadRadius; dx++)
            if (_cellsByPos.ContainsKey((cx + dx, cy + dy)))
                wanted.Add((cx + dx, cy + dy));

        foreach (var key in _loaded.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _loaded[key].QueueFree();
            _loaded.Remove(key);
        }
        foreach (var key in wanted.Where(k => !_loaded.ContainsKey(k)))
        {
            var node = LoadChunk(_cellsByPos[key]);
            if (node is not null)
                _loaded[key] = node;
        }
    }

    Node3D? LoadChunk(WorldCell cell)
    {
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(_assets.ChunkModelPath(cell.Chunk), state) != Error.Ok)
        {
            GD.PushWarning($"Couldn't load map chunk {cell.Chunk}.");
            return null;
        }
        if (doc.GenerateScene(state) is not Node3D scene)
            return null;

        scene.Position = new Vector3(cell.X * ChunkTiles + ChunkTiles / 2f, 0, cell.Y * ChunkTiles + ChunkTiles / 2f);
        _world.AddChild(scene);
        foreach (var mesh in scene.FindChildren("*", nameof(MeshInstance3D), owned: false).OfType<MeshInstance3D>())
        {
            ForcePixelArt(mesh);
            mesh.CreateTrimeshCollision();
        }
        return scene;
    }

    static void ForcePixelArt(MeshInstance3D mesh)
    {
        for (var s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
            if (mesh.Mesh.SurfaceGetMaterial(s) is BaseMaterial3D m)
            {
                m.TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
                m.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
            }
    }

    /// <summary>Ground height at a tile: highest surface under its centre near the current height.</summary>
    float GroundHeight(Vector2I t, float near = 0)
    {
        var space = _world.GetWorld3D().DirectSpaceState;
        var from = new Vector3(t.X + 0.5f, near + 6, t.Y + 0.5f);
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * 40));
        return hit.Count > 0 ? ((Vector3)hit["position"]).Y : near;
    }

    void UpdateCamera(bool snap, float delta = 0)
    {
        // Roughly the DS framing: about 16 tiles across, looking down at ~45°.
        var offset = new Vector3(0, 17, 17);
        var target = _player.Position + offset;
        _camera.Position = snap ? target : _camera.Position.Lerp(target, Mathf.Min(1, delta * 8));
        _camera.LookAt(_camera.Position - offset, Vector3.Up);
    }

    void UpdateHud()
    {
        var cx = Mathf.FloorToInt(_tile.X / (float)ChunkTiles);
        var cy = Mathf.FloorToInt(_tile.Y / (float)ChunkTiles);
        var name = _cellsByPos.TryGetValue((cx, cy), out var c) && c.Location.Length > 0 ? c.Location : "Unova";
        _hud.Text = $"{name}   ·   tile {_tile.X}, {_tile.Y}";
    }
}
