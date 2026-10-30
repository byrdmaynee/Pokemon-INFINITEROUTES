using Godot;
using InfiniteRoutes.Client.Ui;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// Offline test world (M1b): walk around Unova as extracted from the player's ROM.
/// Chunks of the current map stream in around the player; movement is tile-based with the ROM's
/// collision grid, ground height comes from raycasting the map geometry, and warps (doors, stairs,
/// exit mats, map edges) move the player between the overworld and interiors.
/// </summary>
public partial class OverworldScreen : Control
{
    const int ChunkTiles = 32;
    const int LoadRadius = 1;
    const float WalkSeconds = 0.26f;
    const float RunSeconds = 0.13f;
    const float FadeSeconds = 0.18f;
    const int StartZone = 389;   // Nuvema Town
    const string PlayerCharacter = "hero";

    GameAssets _assets = null!;
    readonly Dictionary<int, ZoneInfo> _zones = new();
    readonly Dictionary<int, byte[]> _tiles = new();
    readonly Dictionary<(int, int), WorldCell> _cellsByPos = new();
    readonly Dictionary<(int, int), Node3D> _loaded = new();
    readonly Dictionary<Vector2I, WarpInfo> _warps = new();
    WorldIndex _map = null!;
    int _zone = StartZone;

    Node3D _world = null!;
    Godot.Environment _environment = null!;
    Node3D _player = null!;
    Sprite3D _sprite = null!;
    Camera3D _camera = null!;
    Label _hud = null!;
    ColorRect _fade = null!;
    MeshInstance3D? _overlay;
    float _zoom = 1;

    Vector2I _tile;
    Vector2I _dir = Vector2I.Down;
    bool _moving;
    bool _running;
    int _steps;
    float _height;

    public static bool Available => ClientServices.UnovaAssets?.LoadOverworld() is not null;

    public override void _Ready()
    {
        if (ClientServices.UnovaAssets is not { } assets || assets.LoadOverworld() is not { } overworld)
        {
            var missing = Page.Create("Offline Test World", Ui.Ui.Text(
                "No map data yet. Re-extract your Black/White ROM in Settings → ROM Library.", 18, Ui.Ui.Warn, wrap: true));
            AddChild(missing);
            missing.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            return;
        }
        _assets = assets;
        foreach (var z in assets.LoadZones())
            _zones[z.Id] = z;

        var container = new SubViewportContainer { Stretch = true };
        AddChild(container);
        container.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var viewport = new SubViewport { Msaa3D = Viewport.Msaa.Disabled, HandleInputLocally = false };
        container.AddChild(viewport);

        _world = new Node3D();
        viewport.AddChild(_world);
        _environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = Colors.White,
        };
        _world.AddChild(new WorldEnvironment { Environment = _environment });

        _player = new Node3D();
        _sprite = BuildSprite(assets.OverworldSpritePath(PlayerCharacter));
        _player.AddChild(_sprite);
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
        var help = Ui.Ui.Text("Arrows / WASD: walk · Shift: run · Wheel: zoom · F3: collision · Esc: menu", 14, Ui.Ui.Muted);
        AddChild(help);
        help.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomLeft);
        help.Position = new Vector2(16, -36);
        _fade = new ColorRect { Color = Colors.Black, Modulate = Colors.Transparent, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_fade);
        _fade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        EnterMap(overworld);
        _tile = FindStartTile();
        StreamChunks();
        UpdateHud();
        Arrive(_tile, null, autoStep: false);
    }

    static Sprite3D BuildSprite(string spritePath) => new()
    {
        Texture = File.Exists(spritePath) && Image.LoadFromFile(spritePath) is { } image ? ImageTexture.CreateFromImage(image) : null,
        Hframes = CharacterSheet.Columns,
        Vframes = CharacterSheet.Rows,
        // 32 px frames on a 16 px tile grid: 1/16 world units per pixel, feet on the ground.
        PixelSize = 1f / 16,
        Offset = new Vector2(0, CharacterSheet.FrameSize / 2f - 2),
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
        AlphaCut = SpriteBase3D.AlphaCutMode.Discard,
        Shaded = false,
    };

    /// <summary>Switches to another map: drops loaded chunks and indexes the new map's cells and warps.</summary>
    void EnterMap(WorldIndex map)
    {
        foreach (var node in _loaded.Values)
            node.QueueFree();
        _loaded.Clear();
        _cellsByPos.Clear();
        _map = map;
        foreach (var c in map.Cells)
            _cellsByPos[(c.X, c.Y)] = c;

        // Maps with per-cell zones (the overworld) gather every zone's warps; interiors use the current zone's.
        _warps.Clear();
        var perCell = map.Cells.Any(c => c.Zone >= 0);
        foreach (var zone in _zones.Values.Where(z => perCell ? z.Map == map.Key : z.Id == _zone))
        foreach (var w in zone.Warps.Where(w => _zones.ContainsKey(w.TargetZone)))
        for (var y = w.Y; y < w.Y + Math.Max(1, w.Height); y++)
        for (var x = w.X; x < w.X + Math.Max(1, w.Width); x++)
            _warps.TryAdd(new Vector2I(x, y), w);

        _environment.BackgroundColor = map.Key == Gen5WorldExtractor.OverworldKey ? new Color(0.55f, 0.75f, 0.95f) : Colors.Black;
    }

    /// <summary>
    /// Places the player once the map's collision is in the physics space, facing <paramref name="facing"/>;
    /// when arriving through a door or stairs, steps off them onto the floor.
    /// </summary>
    async void Arrive(Vector2I tile, Facing? facing, bool autoStep)
    {
        _moving = true;
        if (facing is { } f)
            _dir = FacingToDir(f);
        _player.Position = TileCenter(tile, _height);
        UpdateCamera(snap: true);
        ShowFrame(0);
        // New collision shapes only reach the physics space after a physics frame.
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        if (!IsInstanceValid(this))
            return;
        // A door tile sits under its building's roof, so take the height from where the player steps out to.
        var groundTile = autoStep ? tile + _dir : tile;
        _height = GroundHeight(groundTile, near: 40);
        _player.Position = TileCenter(tile, _height);
        UpdateCamera(snap: true);
        _moving = false;
        if (autoStep && !IsBlocked(tile + _dir))
            Move(tile + _dir, run: false);
    }

    Vector2I FindStartTile()
    {
        var cell = _map.Cells.FirstOrDefault(c => c.Zone == StartZone) ?? _map.Cells[0];
        var origin = new Vector2I(cell.X * ChunkTiles, cell.Y * ChunkTiles);
        // Spiral out from the chunk centre to the nearest walkable tile.
        for (var r = 0; r < ChunkTiles; r++)
        for (var dy = -r; dy <= r; dy++)
        for (var dx = -r; dx <= r; dx++)
        {
            var t = origin + new Vector2I(ChunkTiles / 2 + dx, ChunkTiles / 2 + dy);
            if (!IsBlocked(t) && !_warps.ContainsKey(t))
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
            var wasRunning = _running;
            var input = Vector2I.Zero;
            if (Input.IsActionPressed("ui_up") || Input.IsPhysicalKeyPressed(Key.W)) input = Vector2I.Up;
            else if (Input.IsActionPressed("ui_down") || Input.IsPhysicalKeyPressed(Key.S)) input = Vector2I.Down;
            else if (Input.IsActionPressed("ui_left") || Input.IsPhysicalKeyPressed(Key.A)) input = Vector2I.Left;
            else if (Input.IsActionPressed("ui_right") || Input.IsPhysicalKeyPressed(Key.D)) input = Vector2I.Right;
            if (input != Vector2I.Zero)
                TryStep(input, Input.IsKeyPressed(Key.Shift));
            else if (wasRunning)
            {
                _running = false;
                ShowFrame(0);
            }
        }
        UpdateCamera(snap: false, (float)delta);
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.F3 })
            ShowCollision(_overlay is null);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel)
            Zoom = _zoom * (wheel.ButtonIndex == MouseButton.WheelUp ? 1 / 1.15f : 1.15f);
    }

    /// <summary>Camera distance multiplier (1 = DS framing); mouse wheel adjusts it.</summary>
    public float Zoom
    {
        get => _zoom;
        set => _zoom = Mathf.Clamp(value, 0.5f, 4f);
    }

    /// <summary>Debug overlay: blocked tiles red, warps blue, tall grass green, around the player.</summary>
    public void ShowCollision(bool on)
    {
        _overlay?.QueueFree();
        _overlay = null;
        if (!on)
            return;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var ry = Mathf.CeilToInt(12 * _zoom);
        var rx = Mathf.CeilToInt(16 * _zoom);
        for (var y = _tile.Y - ry; y <= _tile.Y + ry; y++)
        for (var x = _tile.X - rx; x <= _tile.X + rx; x++)
        {
            var t = new Vector2I(x, y);
            var color = _warps.ContainsKey(t) ? new Color(0.2f, 0.4f, 1, 0.6f)
                : IsBlocked(t) ? new Color(1, 0.15f, 0.15f, 0.45f)
                : (TileFlags(t) & 2) != 0 ? new Color(0.2f, 1, 0.3f, 0.35f) : Colors.Transparent;
            if (color.A == 0)
                continue;
            st.SetColor(color);
            var h = _height + 0.05f;
            Vector3 a = new(x + 0.05f, h, y + 0.05f), b = new(x + 0.95f, h, y + 0.05f), c = new(x + 0.95f, h, y + 0.95f), d = new(x + 0.05f, h, y + 0.95f);
            st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
            st.AddVertex(a); st.AddVertex(c); st.AddVertex(d);
        }
        _overlay = new MeshInstance3D
        {
            Mesh = st.Commit(),
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        _world.AddChild(_overlay);
    }

    /// <summary>
    /// Moves one tile in a direction if it isn't blocked, or takes a warp: doors and stairs when stepped into,
    /// exit mats and map edges when pushed off. Public so smoke tests can drive it.
    /// </summary>
    public bool TryStep(Vector2I dir, bool run = false)
    {
        if (_moving)
            return false;
        _dir = dir;
        _running = run;
        var target = _tile + dir;

        if (_warps.TryGetValue(_tile, out var here) && here.Kind is WarpKind.Mat or WarpKind.Edge
            && here.Facing is { } arrival && dir == -FacingToDir(arrival))
        {
            TakeWarp(here);
            return true;
        }
        if (_warps.TryGetValue(target, out var warp) && warp.Kind is not (WarpKind.Mat or WarpKind.Edge))
        {
            TakeWarp(warp);
            return true;
        }
        if (IsBlocked(target))
        {
            ShowFrame(0);
            return false;
        }
        Move(target, run);
        return true;
    }

    void Move(Vector2I target, bool run)
    {
        _moving = true;
        _tile = target;
        StreamChunks();
        _height = GroundHeight(_tile, _height);
        var seconds = run ? RunSeconds : WalkSeconds;
        var tween = CreateTween();
        tween.TweenProperty(_player, "position", TileCenter(_tile, _height), seconds);
        // Alternate feet each step: foot frame for the first half, standing frame for the second.
        ShowFrame(1 + _steps++ % 2);
        tween.Parallel().TweenCallback(Callable.From(() => ShowFrame(0))).SetDelay(seconds / 2);
        tween.Finished += () =>
        {
            _moving = false;
            if (_cellsByPos.TryGetValue(CellOf(_tile), out var cell) && cell.Zone >= 0)
                _zone = cell.Zone;
            UpdateHud();
        };
    }

    async void TakeWarp(WarpInfo warp)
    {
        if (!_zones.TryGetValue(warp.TargetZone, out var zone) || warp.TargetWarp >= zone.Warps.Count
            || _assets.LoadMap(zone.Map) is not { } map)
        {
            ShowFrame(0);
            return;
        }
        _moving = true;
        ShowFrame(0);
        var fadeOut = CreateTween();
        fadeOut.TweenProperty(_fade, "modulate", Colors.White, FadeSeconds);
        await ToSignal(fadeOut, Tween.SignalName.Finished);
        if (!IsInstanceValid(this))
            return;

        var dest = zone.Warps[warp.TargetWarp];
        _zone = zone.Id;
        EnterMap(map);
        // Mats are several tiles wide; arrive in the middle.
        _tile = new Vector2I(dest.X + (Math.Max(1, dest.Width) - 1) / 2, dest.Y + (Math.Max(1, dest.Height) - 1) / 2);
        StreamChunks();
        UpdateHud();
        Arrive(_tile, dest.Facing, autoStep: dest.Kind is WarpKind.Door or WarpKind.Stairs);
        CreateTween().TweenProperty(_fade, "modulate", Colors.Transparent, FadeSeconds).SetDelay(0.05);
    }

    static Vector2I FacingToDir(Facing f) => f switch
    {
        Facing.Up => Vector2I.Up,
        Facing.Left => Vector2I.Left,
        Facing.Right => Vector2I.Right,
        _ => Vector2I.Down,
    };

    void ShowFrame(int step)
    {
        var facing = _dir == Vector2I.Up ? Facing.Up : _dir == Vector2I.Left ? Facing.Left : _dir == Vector2I.Right ? Facing.Right : Facing.Down;
        _sprite.Frame = (int)facing * CharacterSheet.Columns + CharacterSheet.Column(_running, step);
    }

    public Vector2I PlayerTile => _tile;

    /// <summary>The zone the player is in (an interior's own zone, not its town's).</summary>
    public int CurrentZone => _zone;

    /// <summary>True while a step, warp or fade is in progress.</summary>
    public bool Busy => _moving;

    public bool IsWalkable(Vector2I t) => !IsBlocked(t);

    public bool IsWarp(Vector2I t) => _warps.ContainsKey(t);

    /// <summary>Tiles of the current map's warps that lead to a zone; for smoke tests.</summary>
    public IReadOnlyDictionary<Vector2I, WarpInfo> Warps => _warps;

    /// <summary>Name of the location the tile belongs to ("" for scenery).</summary>
    public string LocationAt(Vector2I t) =>
        _cellsByPos.TryGetValue(CellOf(t), out var c) ? c.Zone >= 0 ? c.Location : ZoneLocation : "";

    string ZoneLocation => _zones.TryGetValue(_zone, out var z) ? z.Location : "";

    static (int, int) CellOf(Vector2I t) => (Mathf.FloorToInt(t.X / (float)ChunkTiles), Mathf.FloorToInt(t.Y / (float)ChunkTiles));

    bool IsBlocked(Vector2I t) => (TileFlags(t) & 1) != 0;

    /// <summary>Exported tile flags (bit 0 blocked, 1 tall grass, 2 water); off-map tiles are blocked.</summary>
    byte TileFlags(Vector2I t)
    {
        var (cx, cy) = CellOf(t);
        if (!_cellsByPos.TryGetValue((cx, cy), out var cell))
            return 1;
        if (!_tiles.TryGetValue(cell.Chunk, out var tiles))
            _tiles[cell.Chunk] = tiles = _assets.ChunkTiles(cell.Chunk);
        var lx = t.X - cx * ChunkTiles;
        var ly = t.Y - cy * ChunkTiles;
        return tiles[ly * ChunkTiles + lx];
    }

    static Vector3 TileCenter(Vector2I t, float y) => new(t.X + 0.5f, y, t.Y + 0.5f);

    void StreamChunks()
    {
        var (cx, cy) = CellOf(_tile);
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
        if (doc.AppendFromFile(_assets.ChunkModelPath(cell), state) != Error.Ok)
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

    /// <summary>Ground height at a tile: highest surface under its centre, casting from a little above <paramref name="near"/>.</summary>
    float GroundHeight(Vector2I t, float near = 0)
    {
        var space = _world.GetWorld3D().DirectSpaceState;
        var from = new Vector3(t.X + 0.5f, near + 6, t.Y + 0.5f);
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * (near + 46)));
        return hit.Count > 0 ? ((Vector3)hit["position"]).Y : near;
    }

    void UpdateCamera(bool snap, float delta = 0)
    {
        // Roughly the DS framing: about 16 tiles across, looking down at ~45°.
        var offset = new Vector3(0, 17, 17) * _zoom;
        var target = _player.Position + offset;
        _camera.Position = snap ? target : _camera.Position.Lerp(target, Mathf.Min(1, delta * 8));
        _camera.LookAt(_camera.Position - offset, Vector3.Up);
    }

    void UpdateHud()
    {
        var name = LocationAt(_tile) is { Length: > 0 } location ? location : "Unova";
        _hud.Text = $"{name}   ·   tile {_tile.X}, {_tile.Y}";
    }
}
