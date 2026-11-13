using Godot;
using InfiniteRoutes.Client.Ui;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// Offline test world (M1b): walk around Unova as extracted from the player's ROM.
/// Chunks of the current map stream in around the player; movement is tile-based with the ROM's
/// collision grid, ground height comes from raycasting the map geometry, and warps (doors, stairs,
/// exit mats, map edges) move the player between the overworld and interiors. NPCs from the zones' event
/// data stand, look around or wander, block their tiles, and turn to face the player when talked to.
/// </summary>
public partial class OverworldScreen : Control
{
    /// <summary>Tiles per map cell: 32, more on a few map types (Skyarrow Bridge's cells are 128).</summary>
    int ChunkTiles => _map?.ChunkTiles ?? 32;
    const int LoadRadius = 1;
    const float WalkSeconds = 0.26f;
    const float RunSeconds = 0.13f;
    const float FadeSeconds = 0.18f;
    const int StartZone = 389;   // Nuvema Town

    GameAssets _assets = null!;
    readonly ZoneTable _zones = new();
    readonly Dictionary<string, byte[]> _tiles = new();
    readonly Dictionary<(int, int), WorldCell> _cellsByPos = new();
    readonly Dictionary<(int, int), Node3D> _loaded = new();
    readonly Dictionary<Vector2I, WarpInfo> _warps = new();
    readonly List<NpcInfo> _mapNpcs = new();
    /// <summary>The zone each of <see cref="_mapNpcs"/> belongs to.</summary>
    readonly Dictionary<NpcInfo, int> _npcZones = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<(int, int), List<NpcActor>> _npcs = new();
    readonly Dictionary<int, Texture2D?> _npcSheets = new();
    readonly Dictionary<int, IReadOnlyList<Extractor.Nitro3D.SrtAnimation>> _animations = new();
    readonly Dictionary<(int, int), TextureAnimator> _animators = new();
    readonly Dictionary<int, IReadOnlyList<Extractor.Gen5.TexturePattern>> _patterns = new();
    readonly Dictionary<string, Texture2D?> _patternFrames = new();
    WorldIndex _map = null!;
    int _zone = StartZone;

    Node3D _world = null!;
    Godot.Environment _environment = null!;
    Node3D _player = null!;
    Sprite3D _sprite = null!;
    Camera3D _camera = null!;
    Label _hud = null!;
    ColorRect _fade = null!;
    PanelContainer _dialog = null!;
    Label _dialogText = null!;
    MeshInstance3D? _overlay;
    BattleScreen? _battle;

    GameMenu? _menu;
    Label _questHud = null!;
    Label _toast = null!;
    Label _saved = null!;
    Tween? _toastTween;
    /// <summary>False when the cache has no battle data (extracted before 0.6.0): no encounters then.</summary>
    bool _battles;
    float _zoom = 1;
    // Black/White change season every month: January spring, February summer, March autumn, April winter, …
    Season _season = (Season)((DateTime.Now.Month - 1) % 4);

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
            // Say why: most often the ROM's drive isn't connected (the library checks the file at every launch).
            var rom = new[] { "white", "black" }.Select(id => ClientServices.Library[id]).FirstOrDefault(e => e.Path is not null);
            var why = rom is { Status: Extractor.Roms.RomStatus.Unreadable or Extractor.Roms.RomStatus.Missing }
                ? $"Your {(rom.GameId == "black" ? "Black" : "White")} ROM wasn't found at {rom.Path}.\n{rom.Message}\nReconnect it (or point Settings → ROM Library at its new place) and restart."
                : "No map data yet. Re-extract your Black/White ROM in Settings → ROM Library.";
            GD.PushWarning($"Overworld unavailable: {why}");
            var missing = Page.Create(_online ? "Unova" : "Offline Test World", Ui.Ui.Text(why, 18, Ui.Ui.Warn, wrap: true));
            AddChild(missing);
            missing.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            return;
        }
        _assets = assets;
        // Online the game data (names) is still needed, but the offline save isn't played.
        _battles = OfflineSession.Ensure(assets) && !_online;
        // Hand-made routes are the server's: offline there are none.
        if (!_online)
            OnlineRoutes.Clear(assets);
        _zones.Load(assets);

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
        _player.AddChild(MyBeacon);
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
        // Top right: the tracked quest and unread mail. Top centre: a toast for finished quests and new badges.
        _questHud = Ui.Ui.Text("", 15);
        _questHud.AddThemeStyleboxOverride("normal", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0.55f), ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 8, ContentMarginBottom = 8,
            BorderColor = new Color(0.95f, 0.76f, 0.31f, 0.8f), BorderWidthLeft = 3,
        });
        _questHud.HorizontalAlignment = HorizontalAlignment.Left;
        AddChild(_questHud);
        _questHud.SetAnchorsAndOffsetsPreset(LayoutPreset.TopRight);
        _questHud.GrowHorizontal = GrowDirection.Begin;
        _questHud.OffsetRight = -16;
        _questHud.OffsetTop = 16;
        _toast = Ui.Ui.Text("", 20, Ui.Ui.Accent);
        _toast.AddThemeStyleboxOverride("normal", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.13f, 0.92f), ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 10, ContentMarginBottom = 10,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
        });
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.Modulate = Colors.Transparent;
        AddChild(_toast);
        _toast.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterTop);
        _toast.GrowHorizontal = GrowDirection.Both;
        _toast.OffsetTop = 70;
        _saved = Ui.Ui.Text("", 15, Ui.Ui.Good);
        _saved.Modulate = Colors.Transparent;
        AddChild(_saved);
        _saved.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight);
        _saved.GrowHorizontal = GrowDirection.Begin;
        _saved.OffsetRight = -16;
        _saved.OffsetBottom = -12;
        var help = Ui.Ui.Text(_online
            ? "Arrows / WASD: walk · Shift: run · Enter / T: chat · /: command · X: menu · Wheel: zoom · Esc: leave"
            : "Arrows / WASD: walk · Shift: run · Z / Enter: talk · X: menu · Wheel: zoom · F3: collision · F4: season · Esc: menu", 14, Ui.Ui.Muted);
        AddChild(help);
        help.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomLeft);
        help.Position = new Vector2(16, -36);
        BuildChat();
        _dialog = new PanelContainer { Visible = false };
        _dialog.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.1f, 0.92f), BorderColor = new Color(1, 1, 1, 0.8f),
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 20, ContentMarginRight = 20, ContentMarginTop = 14, ContentMarginBottom = 14,
        });
        _dialogText = Ui.Ui.Text("", 20, wrap: true);
        _dialog.AddChild(_dialogText);
        AddChild(_dialog);
        _dialog.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        _dialog.OffsetLeft = 80;
        _dialog.OffsetRight = -80;
        _dialog.OffsetTop = -170;
        _dialog.OffsetBottom = -60;
        _fade = new ColorRect { Color = Colors.Black, Modulate = Colors.Transparent, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_fade);
        _fade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // Continue where the save left off, or start at home.
        Facing? facing = null;
        if (_online && OnlineStart() is { } start && _zones.TryGetValue(start.Zone, out var startZone)
            && assets.LoadMap(startZone.Map) is { } startMap)
        {
            // Online: where the server says.
            _zone = start.Zone;
            EnterMap(startMap);
            _tile = start.Tile;
            facing = start.Facing;
            _dir = FacingToDir(start.Facing);
        }
        else if (_battles && OfflineSession.State.Location is { } saved && _zones.TryGetValue(saved.Zone, out var savedZone)
            && assets.LoadMap(savedZone.Map) is { } savedMap)
        {
            _zone = saved.Zone;
            EnterMap(savedMap);
            _tile = new Vector2I(saved.X, saved.Y);
            facing = (Facing)Math.Clamp(saved.Facing, 0, 3);
            _dir = FacingToDir(facing.Value);
        }
        else
        {
            EnterMap(overworld);
            _tile = FindStartTile();
        }
        StreamChunks();
        UpdateHud();
        Arrive(_tile, facing, autoStep: false);
        if (_battles && OfflineSession.Continued && OfflineSession.LastSaved is { } when)
            Toast($"Welcome back! Continuing your save from {when:g}.");
    }

    /// <summary>Seconds between autosaves while walking around.</summary>
    const double AutosaveSeconds = 60;
    double _sinceSave;

    /// <summary>Saves the game (offline) and flashes "Saved" in the corner. Public so smoke tests can force one.</summary>
    public void SaveGame()
    {
        if (!_battles || _battle is not null)
            return;
        _sinceSave = 0;
        if (!OfflineSession.Save())
            return;
        _saved.Text = "✓ Saved";
        var tween = CreateTween();
        _saved.Modulate = Colors.White;
        tween.TweenInterval(1.2);
        tween.TweenProperty(_saved, "modulate", Colors.Transparent, 0.6);
    }

    public override void _Notification(int what)
    {
        // Closing the window or leaving the world (back to the main menu) saves.
        if (what is (int)NotificationWMCloseRequest or (int)NotificationExitTree && _world is not null && _battles)
            OfflineSession.Save();
        // Leaving the world online closes the game connection (back to the character list).
        if (what == (int)NotificationExitTree && _online)
            InfiniteRoutes.Client.Online.OnlineSession.Leave();
    }

    static Sprite3D BuildSprite(string spritePath) =>
        CharacterSprite.Create(File.Exists(spritePath) && Image.LoadFromFile(spritePath) is { } image ? ImageTexture.CreateFromImage(image) : null);

    /// <summary>Switches to another map: drops loaded chunks and indexes the new map's cells and warps.</summary>
    void EnterMap(WorldIndex map)
    {
        ClearPhenomenon();
        foreach (var node in _loaded.Values)
            node.QueueFree();
        _loaded.Clear();
        foreach (var key in _npcs.Keys.ToList())
            DespawnNpcs(key);
        _cellsByPos.Clear();
        _map = map;
        foreach (var c in map.Cells)
            _cellsByPos[(c.X, c.Y)] = c;

        // Maps with per-cell zones (the overworld) gather every zone's warps; interiors use the current zone's.
        _warps.Clear();
        var perCell = map.Cells.Any(c => c.Zone >= 0);
        _mapNpcs.Clear();
        _npcZones.Clear();
        foreach (var zone in perCell ? _zones.Values.Where(z => z.Map == map.Key) : _zones.GetValueOrDefault(_zone) is { } own ? [own] : [])
        {
            foreach (var w in zone.Warps.Where(w => _zones.ContainsKey(w.TargetZone)))
            for (var y = w.Y; y < w.Y + Math.Max(1, w.Height); y++)
            for (var x = w.X; x < w.X + Math.Max(1, w.Width); x++)
                _warps.TryAdd(new Vector2I(x, y), w);
            foreach (var npc in ShownNpcs(zone))
            {
                _mapNpcs.Add(npc);
                _npcZones[npc] = zone.Id;
            }
        }

        _environment.BackgroundColor = map.Key == Gen5WorldExtractor.OverworldKey || UnchartedRoutes.TryParseKey(map.Key, out _)
            ? new Color(0.55f, 0.75f, 0.95f) : Colors.Black;
    }

    /// <summary>
    /// Places the player once the map's collision is in the physics space, facing <paramref name="facing"/>;
    /// when arriving through a door or stairs, steps off them onto the floor.
    /// </summary>
    async void Arrive(Vector2I tile, Facing? facing, bool autoStep)
    {
        _moving = true;
        // Arriving (or a save continuing) on water means surfing.
        SetSurfing(_battles && IsWater(tile));
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
        PlaceBuddy();
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
        PumpOnline(delta);
        if (_online)
            TickAdmin(delta);
        var seconds = Time.GetTicksMsec() / 1000.0;
        foreach (var key in _loaded.Keys)
            if (_animators.TryGetValue(key, out var animator))
                animator.Update(seconds);
        foreach (var npc in _npcs.Values.SelectMany(l => l).ToList())
            if (npc.Tick(delta))
                ActNpc(npc);
        if (!_moving && !_dialog.Visible && _battle is null && _menu is null && !OnlineUiBusy)
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
        UpdateBuddy();
        UpdateCamera(snap: false, (float)delta);
        if ((_sinceSave += delta) >= AutosaveSeconds && !_moving && _battle is null)
            SaveGame();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (AskInput(e))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_world is null || _battle is not null)
            return;
        if (OnlineInput(e))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        var talk =e.IsActionPressed("ui_accept") || e is InputEventKey { Pressed: true, Keycode: Key.Z, Echo: false };
        if (_dialog.Visible && (talk || e.IsActionPressed("ui_cancel")))
        {
            _dialog.Visible = false;
            GetViewport().SetInputAsHandled();
        }
        else if (talk && Interact())
            GetViewport().SetInputAsHandled();
        else if (e is InputEventKey { Pressed: true, Keycode: Key.X, Echo: false } && _menu is null)
        {
            ToggleMenu();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.F3 })
            ShowCollision(_overlay is null);
        else if (e is InputEventKey { Pressed: true, Keycode: Key.F4 })
            Season = (Season)(((int)_season + 1) % 4);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel)
            Zoom = _zoom * (wheel.ButtonIndex == MouseButton.WheelUp ? 1 / 1.15f : 1.15f);
    }

    /// <summary>Season the outdoor maps are shown in; changing it reloads the chunks around the player.</summary>
    public Season Season
    {
        get => _season;
        set
        {
            if (_season == value)
                return;
            _season = value;
            foreach (var node in _loaded.Values)
                node.QueueFree();
            _loaded.Clear();
            StreamChunks();
            UpdateHud();
        }
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
            if (!AskWarp(here, _tile))
                TakeWarp(here);
            return true;
        }
        // Doors and stairs are walked into; so is an edge warp on a solid tile (town gates: the way through the wall).
        if (_warps.TryGetValue(target, out var warp) && (warp.Kind is not (WarpKind.Mat or WarpKind.Edge) || warp.Kind == WarpKind.Edge && IsBlocked(target)))
        {
            if (!AskWarp(warp, target))
                TakeWarp(warp);
            return true;
        }
        if (!AdminNoclip && (!CanEnter(target) || NpcAt(target) is not null))
        {
            ShowFrame(0);
            SendTurn();
            return false;
        }
        Move(target, _running = run && !_surfing);
        return true;
    }

    void Move(Vector2I target, bool run)
    {
        _moving = true;
        _buddy?.StepTo(_tile, DirToFacing(target - _tile), _height, run ? RunSeconds : WalkSeconds);
        SendStep(_tile, target, run);
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
            if (_battles && OfflineSession.State.RepelStep())
                ShowMessage("The Repel's effect wore off...");
            AfterWaterStep();
            if (SpottedBy() is { } trainer)
                Challenge(trainer);
            else if (WaterOrPhenomenonEncounter(out var handled) is { } special)
                StartBattle(special);
            else if (!handled && RollEncounter() is { } wild)
                StartBattle(wild);
        };
    }

    /// <summary>An undefeated trainer whose line of sight (straight ahead, up to its sight range, until something blocks it) reaches the player.</summary>
    NpcActor? SpottedBy()
    {
        // Staff walking invisibly aren't seen (the server agrees).
        if (!TrainerBattles || NpcGame?.Lead is null || (_online && OfflineSession.Data is null) || _online && _admin is { Elevated: true, Invisible: true })
            return null;
        foreach (var npc in Npcs)
        {
            if (npc.Moving || npc.Info.Sight <= 0 || TrainerFor(npc) is null)
                continue;
            var d = FacingToDir(npc.Facing);
            for (var k = 1; k <= npc.Info.Sight; k++)
            {
                var t = npc.Tile + d * k;
                if (t == _tile)
                    return npc;
                if (IsBlocked(t) || NpcAt(t) is not null)
                    break;
            }
        }
        return null;
    }

    /// <summary>A trainer saw the player: "!" over its head, it walks up until it's next to the player, then the battle starts.</summary>
    async void Challenge(NpcActor npc)
    {
        _moving = true;
        ShowFrame(0);
        npc.Pause(30);
        var mark = new Label3D
        {
            Text = "!", FontSize = 96, PixelSize = 0.012f, Modulate = new Color(1, 0.25f, 0.2f), OutlineSize = 18,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, Position = new Vector3(0, 2.7f, 0),
        };
        npc.AddChild(mark);
        await ToSignal(GetTree().CreateTimer(0.8), SceneTreeTimer.SignalName.Timeout);
        if (!IsInstanceValid(this) || !IsInstanceValid(npc))
            return;
        mark.QueueFree();

        var d = FacingToDir(npc.Facing);
        while (npc.Tile + d != _tile)
        {
            var next = npc.Tile + d;
            npc.StepTo(next, GroundHeight(next, npc.Position.Y));
            while (npc.Moving)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!IsInstanceValid(this) || !IsInstanceValid(npc))
                    return;
            }
        }
        _dir = -d;
        ShowFrame(0);
        await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
        if (!IsInstanceValid(this))
            return;
        if (TrainerFor(npc) is { } trainer)
        {
            var intro = Pages(npc.Info.Talk?.Lines);
            await Say(intro.Count > 0 ? intro : Core.Data.TrainerInfo.Pages(trainer.Intro));
        }
        if (!IsInstanceValid(this))
            return;
        await Act(npc, spotted: true);
        if (IsInstanceValid(this) && _battle is null)
            _moving = false;
    }

    /// <summary>Shows <paramref name="pages"/> one after another in the dialogue box, each until the player closes it.</summary>
    async Task Say(IReadOnlyList<string> pages)
    {
        foreach (var page in pages)
        {
            ShowMessage(page);
            while (_dialog.Visible)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (!IsInstanceValid(this))
                    return;
            }
        }
    }

    /// <summary>Starts the battle with <paramref name="npc"/>'s trainer team. Public so smoke tests can force one.</summary>
    public void StartTrainerBattle(NpcActor npc)
    {
        if (TrainerFor(npc) is not { } trainer)
            return;
        _battleNpc = npc;
        StartTrainerBattle(trainer);
    }

    /// <summary>Starts a battle with <paramref name="trainer"/>'s team wherever the player is (smoke tests use it for gym leaders).</summary>
    public void StartTrainerBattle(Core.Data.TrainerInfo trainer)
    {
        var team = Core.Battle.SingleBattle.TrainerParty(OfflineSession.Data, trainer, OfflineSession.Rng);
        if (team.Count == 0 || OfflineSession.Lead is null)
            return;

        OpenBattle(new Core.Battle.SingleBattle(OfflineSession.Data, OfflineSession.Party, OfflineSession.Bag, team, trainer, OfflineSession.Rng));
    }

    /// <summary>Whether trainers who see the player challenge them (smoke tests turn it off while walking).</summary>
    public bool TrainerBattles { get; set; } = true;

    /// <summary>
    /// After a step into tall grass: the zone's grass rate is the percent chance of a wild Pokémon, picked from the
    /// current season's grass slots by their weights.
    /// </summary>
    Core.Battle.Pokemon? RollEncounter()
    {
        if (!_battles || !RandomEncounters || !IsTallGrass(_tile) || OfflineSession.Lead is null
            || !_zones.TryGetValue(_zone, out var zone) || zone.Encounters < 0 || zone.Encounters >= OfflineSession.Encounters.Count)
            return null;
        var set = OfflineSession.Encounters[zone.Encounters].ForSeason((int)_season);
        var rng = OfflineSession.Rng;
        if (set.GrassRate <= 0 || rng.Next(100) >= set.GrassRate
            || Core.Data.EncounterSet.Pick(set.Grass, Core.Data.EncounterSet.GrassWeights, rng) is not { } slot)
            return null;
        var level = rng.Next(slot.MinLevel, slot.MaxLevel + 1);
        // A Repel keeps away wild Pokémon below the lead's level.
        if (OfflineSession.State.RepelSteps > 0 && level < OfflineSession.Lead.Level)
            return null;
        return Core.Battle.Pokemon.Create(OfflineSession.Data, slot.Species, level, rng);
    }

    /// <summary>Escape Rope: back to where the player last left the overworld. Only works off the overworld.</summary>
    async void UseEscapeRope()
    {
        var outside = OfflineSession.State.LastOutdoor;
        if (_moving || outside is null || _map.Key == _assets.LoadOverworld()?.Key || !_zones.TryGetValue(outside.Zone, out var zone)
            || _assets.LoadMap(zone.Map) is not { } map || !OfflineSession.Bag.Take(Core.Battle.ItemEffect.EscapeRope))
        {
            _menu?.Notice("You can't use that here.");
            return;
        }
        if (_menu is not null)
            ToggleMenu();
        _moving = true;
        var fadeOut = CreateTween();
        fadeOut.TweenProperty(_fade, "modulate", Colors.White, FadeSeconds);
        await ToSignal(fadeOut, Tween.SignalName.Finished);
        if (!IsInstanceValid(this))
            return;
        _zone = outside.Zone;
        EnterMap(map);
        _tile = new Vector2I(outside.X, outside.Y);
        StreamChunks();
        UpdateHud();
        Arrive(_tile, Facing.Down, autoStep: false);
        CreateTween().TweenProperty(_fade, "modulate", Colors.Transparent, FadeSeconds).SetDelay(0.05);
        ShowMessage("You used the Escape Rope and climbed back outside.");
        SaveGame();
    }

    /// <summary>Starts a wild battle against <paramref name="wild"/> with the party's lead. Public so smoke tests can force one.</summary>
    public void StartBattle(Core.Battle.Pokemon wild)
    {
        if (OfflineSession.Lead is null)
            return;

        OpenBattle(new Core.Battle.SingleBattle(OfflineSession.Data, OfflineSession.Party, OfflineSession.Bag, wild, OfflineSession.Rng));
    }

    /// <summary>
    /// The ROM's battle backdrop and platform models for a battle here. Picked by the kind of place (the zone header's
    /// scenery group, indoors or out, tall grass) and the season, since the game's own zone-to-backdrop table isn't
    /// decoded yet: seasonal fields in grass, seasonal roads elsewhere outdoors, the forest, sand and beach sets,
    /// caves, plain rooms, and the gym arena for gym leaders.
    /// </summary>
    (string Backdrop, string Stage) BattleScenery(Core.Battle.SingleBattle battle)
    {
        var zone = _zones.GetValueOrDefault(_zone);
        var outdoor = _map.Key == _assets.LoadOverworld()?.Key;
        var season = "abcd"[(int)_season];
        var set = battle.Trainer is { } trainer && Core.Player.Badges.EarnedBy(trainer) is not null ? "19"
            : !outdoor ? (zone?.Encounters >= 0 ? "05" : "14")
            : zone?.BattleScenery switch
            {
                2 => "03",   // Pinwheel Forest
                4 => "10",   // the desert routes
                15 => "11",  // the beaches of Routes 17 and 18
                _ => IsTallGrass(_tile) ? $"02{season}" : $"04{season}",
            };
        var stage = set.StartsWith("04", StringComparison.Ordinal) ? "04" : set;
        return ($"batt_bg{set}", $"batt_stage{stage}");
    }

    async void OpenBattle(Core.Battle.SingleBattle battle, IBattleLink? link = null)
    {
        if (_battle is not null)
            return;
        _moving = true;
        ShowFrame(0);
        // The classic flash: two quick white blinks, then black, then the battle.
        var flash = CreateTween();
        for (var i = 0; i < 2; i++)
        {
            flash.TweenProperty(_fade, "modulate", new Color(1, 1, 1, 0.8f), 0.08);
            flash.TweenProperty(_fade, "modulate", Colors.Transparent, 0.08);
        }
        flash.TweenProperty(_fade, "modulate", Colors.White, 0.25);
        await ToSignal(flash, Tween.SignalName.Finished);
        if (!IsInstanceValid(this))
            return;

        _battle = new BattleScreen(battle, _assets, BattleScenery(battle));
        if (link is not null)
            _battle.Online(link);
        _battle.Finished += EndBattle;
        AddChild(_battle);
        MoveChild(_fade, -1);
        CreateTween().TweenProperty(_fade, "modulate", Colors.Transparent, 0.25);
    }

    async void EndBattle(int outcome)
    {
        var fadeOut = CreateTween();
        fadeOut.TweenProperty(_fade, "modulate", Colors.White, 0.3);
        await ToSignal(fadeOut, Tween.SignalName.Finished);
        if (!IsInstanceValid(this))
            return;
        if (_online)
        {
            // The server records the battle (and sends the player home if they lost).
            _battle?.QueueFree();
            _battle = null;
            _moving = false;
            EndOnlineBattle();
            CreateTween().TweenProperty(_fade, "modulate", Colors.Transparent, 0.3).SetDelay(0.05);
            return;
        }
        var lines = _battle?.Battle is { } finished ? OfflineSession.RecordBattle(finished) : [];
        _battle?.QueueFree();
        _battle = null;

        if (lines.Count > 0)
            ShowMessage(string.Join("\n", lines));
        AfterTrainerBattle((Core.Battle.BattleOutcome)outcome);
        CheckQuests();
        if ((Core.Battle.BattleOutcome)outcome == Core.Battle.BattleOutcome.Lost)
        {
            // Black out back home to Nuvema Town, fully healed (the games use the last Pokémon Center visited).
            OfflineSession.HealParty();
            GoHome();
        }
        else
            _moving = false;
        CreateTween().TweenProperty(_fade, "modulate", Colors.Transparent, 0.3).SetDelay(0.05);
    }

    /// <summary>Puts the player where the save in play left off (after switching save slots), or at home for a new game.</summary>
    void PlaceFromSave()
    {
        if (OfflineSession.State.Location is not { } saved || !_zones.TryGetValue(saved.Zone, out var zone) || _assets.LoadMap(zone.Map) is not { } map)
        {
            GoHome();
            return;
        }
        _zone = saved.Zone;
        EnterMap(map);
        _tile = new Vector2I(saved.X, saved.Y);
        var facing = (Facing)Math.Clamp(saved.Facing, 0, 3);
        _dir = FacingToDir(facing);
        StreamChunks();
        UpdateHud();
        UpdateQuestHud();
        Arrive(_tile, facing, autoStep: false);
    }

    /// <summary>Puts the player back at the start in Nuvema Town (blacking out, starting over).</summary>
    void GoHome()
    {
        _zone = StartZone;
        if (_assets.LoadOverworld() is { } overworld)
            EnterMap(overworld);
        _tile = FindStartTile();
        StreamChunks();
        UpdateHud();
        UpdateQuestHud();
        Arrive(_tile, Facing.Down, autoStep: false);
    }

    /// <summary>Whether steps in tall grass can start wild battles (smoke tests turn it off while walking).</summary>
    public bool RandomEncounters { get; set; } = true;

    /// <summary>The battle in progress, if any; for smoke tests.</summary>
    public BattleScreen? CurrentBattle => _battle;

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
        // Leaving the overworld: remember the way out for an Escape Rope.
        if (_map.Key == _assets.LoadOverworld()?.Key && map.Key != _map.Key)
            OfflineSession.State.LastOutdoor = new Core.Player.PlayerLocation(_zone, _tile.X, _tile.Y, (int)Facing.Down);
        _zone = zone.Id;
        EnterMap(map);
        // Mats are several tiles wide; arrive in the middle.
        _tile = new Vector2I(dest.X + (Math.Max(1, dest.Width) - 1) / 2, dest.Y + (Math.Max(1, dest.Height) - 1) / 2);
        StreamChunks();
        UpdateHud();
        Arrive(_tile, dest.Facing, autoStep: dest.Kind is WarpKind.Door or WarpKind.Stairs || dest.Kind == WarpKind.Edge && IsBlocked(_tile));
        CreateTween().TweenProperty(_fade, "modulate", Colors.Transparent, FadeSeconds).SetDelay(0.05);
        SaveGame();
    }

    /// <summary>Moves the player straight to <paramref name="tile"/> in <paramref name="zone"/>'s map (debugging and smoke tests).</summary>
    public bool Teleport(int zone, Vector2I tile)
    {
        if (_moving || !_zones.TryGetValue(zone, out var info) || _assets.LoadMap(info.Map) is not { } map)
            return false;
        _zone = zone;
        EnterMap(map);
        _tile = tile;
        StreamChunks();
        UpdateHud();
        Arrive(_tile, Facing.Down, autoStep: false);
        return true;
    }

    /// <summary>NPCs of a zone as placed in its event data; for smoke tests.</summary>
    public IReadOnlyList<NpcInfo> ZoneNpcs(int zone) => _zones.TryGetValue(zone, out var z) ? z.Npcs : [];

    public IReadOnlyList<FurnitureInfo> ZoneFurniture(int zone) => _zones.TryGetValue(zone, out var z) ? z.Furniture ?? [] : [];

    static Vector2I FacingToDir(Facing f) => f switch
    {
        Facing.Up => Vector2I.Up,
        Facing.Left => Vector2I.Left,
        Facing.Right => Vector2I.Right,
        _ => Vector2I.Down,
    };

    static Facing DirToFacing(Vector2I d) =>
        d == Vector2I.Up ? Facing.Up : d == Vector2I.Left ? Facing.Left : d == Vector2I.Right ? Facing.Right : Facing.Down;

    void ShowFrame(int step) => _sprite.Frame = (int)DirToFacing(_dir) * CharacterSheet.Columns + CharacterSheet.Column(_running, step);

    public Vector2I PlayerTile => _tile;

    /// <summary>The zone the player is in (an interior's own zone, not its town's).</summary>
    public int CurrentZone => _zone;

    /// <summary>True while a step, warp or fade is in progress.</summary>
    public bool Busy => _moving;

    public bool IsWalkable(Vector2I t) => !IsBlocked(t) && NpcAt(t) is null;

    public bool IsTallGrass(Vector2I t) => (TileFlags(t) & 2) != 0;

    /// <summary>The player's feet height in world units; for smoke tests.</summary>
    public float PlayerHeight => _height;

    /// <summary>NPCs currently spawned around the player; for smoke tests.</summary>
    public IEnumerable<NpcActor> Npcs => _npcs.Values.SelectMany(l => l);

    /// <summary>Materials currently animated by the ROM's texture scroll animations; for smoke tests.</summary>
    public int AnimatedMaterials => _loaded.Keys.Sum(k => _animators.TryGetValue(k, out var a) ? a.Count : 0);

    /// <summary>Materials of loaded chunks whose texture is swapped frame by frame (flowers, sea foam); for smoke tests.</summary>
    public int SwappedMaterials => _loaded.Keys.Sum(k => _animators.TryGetValue(k, out var a) ? a.SwapCount : 0);

    /// <summary>True while a dialogue box is open.</summary>
    public bool Talking => _dialog.Visible;

    /// <summary>
    /// Talks to the NPC the player faces: it turns toward the player and a dialogue box opens. Trainers say their
    /// lines from the ROM (and battle if they haven't yet) and the Pokémon Center nurse heals the party; other NPCs'
    /// lines need the script engine, so for now the box says which script would run. Counters can be talked across.
    /// False when nobody is there.
    /// </summary>
    public bool Interact()
    {
        if (_moving || _dialog.Visible || _menu is not null)
            return false;
        var npc = NpcAt(_tile + _dir);
        if (npc is null && TrySurf())
            return true;
        if (npc is null && IsBlocked(_tile + _dir))
            npc = NpcAt(_tile + _dir * 2);   // across a counter
        if (npc is null)
            return _battles && CenterPcTiles(_zone).Contains(_tile + _dir) && OpenPc();
        npc.Facing = DirToFacing(-_dir);
        npc.Pause(2);
        ShowFrame(0);
        if (npc.Info.Script == NurseScript && _online)
        {
            // The server heals the party and answers with the nurse's words.
            InfiniteRoutes.Client.Online.OnlineSession.Game?.Send(new Core.Net.NpcAction(npc.Zone, npc.Info.Id));
            return true;
        }
        if (npc.Info.Script == NurseScript && _battles)
        {
            OfflineSession.RecordCenterVisit();
            ShowMessage("Welcome to the Pokémon Center!\nYour Pokémon have been restored to full health. We hope to see you again!");
            CheckQuests();
            return true;
        }
        if (_battles && npc.Info.Sprite == ClerkSprite && ZoneNpcs(_zone).Any(n => n.Script == NurseScript))
            return OpenMart();
        TalkTo(npc);
        return true;
    }

    /// <summary>
    /// The Pokémon Center PC's tiles: the terminal left of the nurse's counter and the counter end in front of it, so it
    /// can be used from in front of the counter. No zone event marks it (the room's only furniture is elsewhere), so
    /// it is placed from the nurse, who stands at the same spot in every Unova Center. Empty outside Centers.
    /// </summary>
    public IReadOnlyList<Vector2I> CenterPcTiles(int zone) =>
        ZoneNpcs(zone).FirstOrDefault(n => n.Script == NurseScript) is { } nurse
            ? [new(nurse.X - 3, nurse.Y - 1), new(nurse.X - 3, nurse.Y), new(nurse.X - 3, nurse.Y + 1)]
            : [];

    /// <summary>Boots up the Pokémon Center PC: the menu opens on its PC tab. Public so smoke tests can open it.</summary>
    public bool OpenPc()
    {
        if (_menu is not null || !_battles || _moving || _dialog.Visible || _battle is not null)
            return false;
        OpenMenu(GameMenu.Tab.Pc, GameMenu.Tab.Pc);
        return true;
    }

    /// <summary>Opens the Poké Mart counter (the clerks beside the PC in every Pokémon Center). Public so smoke tests can open it.</summary>
    public bool OpenMart()
    {
        if (_menu is not null || !_battles || _moving || _dialog.Visible || _battle is not null)
            return false;
        OpenMenu(GameMenu.Tab.Mart, GameMenu.Tab.Mart);
        return true;
    }

    /// <summary>The overworld sprite of a Poké Mart clerk; the two behind the right-hand counter in every Pokémon Center.</summary>
    const int ClerkSprite = 77;

    /// <summary>
    /// The shared script every Pokémon Center nurse runs: the nurse behind the ground-floor counter, at (7, 10) in every
    /// Unova Center. (Script 10100 is the Wi-Fi staff in the middle of the raised back counter.)
    /// </summary>
    const int NurseScript = 2100;

    void ShowMessage(string text)
    {
        _dialogText.Text = text;
        _dialog.Visible = true;
    }

    /// <summary>The X menu (party, Bag, badges, quests, mail, friends, market), or closes it if open. Public so smoke tests can open it.</summary>
    public void ToggleMenu(GameMenu.Tab tab = GameMenu.Tab.Party)
    {
        if (_menu is not null)
        {
            _menu.QueueFree();
            _menu = null;
            CheckQuests();
            return;
        }
        if (!_battles || _moving || _dialog.Visible || _battle is not null)
            return;
        OpenMenu(tab, null);
    }

    void OpenMenu(GameMenu.Tab tab, GameMenu.Tab? counter)
    {
        CheckQuests();
        _menu = new GameMenu(_assets, tab, counter, ZoneLocation);
        _menu.Closed += () =>
        {
            _menu = null;
            CheckQuests();
        };
        _menu.StartedOver += GoHome;
        _menu.SlotChanged += PlaceFromSave;
        _menu.UsedEscapeRope += UseEscapeRope;
        _menu.UsedRod += Fish;
        AddChild(_menu);
        MoveChild(_fade, -1);
    }

    public bool MenuOpen => _menu is not null;
    public GameMenu? Menu => _menu;

    /// <summary>Finishes quests that are done (their rewards go to the mail), toasts them and refreshes the tracker.</summary>
    void CheckQuests()
    {
        if (!_battles)
            return;
        var lines = OfflineSession.CheckQuests();
        if (lines.Count > 0)
            Toast(string.Join("\n", lines) + "\nRewards are in your Mail (X → Mail).");
        UpdateQuestHud();
        SaveGame();
    }

    void Toast(string text)
    {
        _toast.Text = text;
        _toastTween?.Kill();
        _toastTween = CreateTween();
        _toastTween.TweenProperty(_toast, "modulate", Colors.White, 0.25);
        _toastTween.TweenInterval(4);
        _toastTween.TweenProperty(_toast, "modulate", Colors.Transparent, 0.6);
    }

    void UpdateQuestHud()
    {
        var game = NpcGame;
        _questHud.Visible = game is not null;
        if (game is null)
            return;
        var p = game.Progress;
        var lines = new List<string>();
        if (game.Quests.Tracked is { } quest)
        {
            lines.Add($"◆ {quest.Info.Title}");
            for (var i = 0; i < quest.Info.Objectives.Count; i++)
            {
                var o = quest.Info.Objectives[i];
                var n = quest.Progress(i, p);
                lines.Add(o.Count > 1 ? $"   {o.Text}  {n}/{o.Count}" : $"   {(n >= o.Count ? "✓" : "○")} {o.Text}");
            }
        }
        else
            lines.Add("No active quests");
        lines.Add($"Badges {p.Badges.Count}/8   ·   ₽{p.Money:N0}" + (!_online && OfflineSession.Mail.Unread > 0 ? $"   ·   ✉ {OfflineSession.Mail.Unread} new" : ""));
        _questHud.Text = string.Join("\n", lines);
    }

    NpcActor? NpcAt(Vector2I t)
    {
        foreach (var list in _npcs.Values)
        foreach (var npc in list)
            if (npc.Tile == t)
                return npc;
        return null;
    }

    /// <summary>Idle behaviour: movement 2 looks around; 3 wanders inside its leash (or just turns without one).</summary>
    void ActNpc(NpcActor npc)
    {
        // Someone being talked to keeps facing the player.
        if (npc.Info.Movement is not (2 or 3) || (_dialog.Visible && _tile + _dir == npc.Tile))
            return;
        Vector2I[] dirs = [Vector2I.Up, Vector2I.Down, Vector2I.Left, Vector2I.Right];
        var dir = dirs[GD.Randi() % 4];
        npc.Facing = DirToFacing(dir);
        if (npc.Info.Movement != 3)
            return;
        var target = npc.Tile + dir;
        var off = target - npc.Home;
        if (Math.Abs(off.X) > npc.Info.RangeX || Math.Abs(off.Y) > npc.Info.RangeY
            || IsBlocked(target) || _warps.ContainsKey(target) || target == _tile || NpcAt(target) is not null)
            return;
        npc.StepTo(target, GroundHeight(target, npc.Position.Y));
    }

    void SpawnNpcs((int X, int Y) cell)
    {
        var list = new List<NpcActor>();
        foreach (var info in _mapNpcs.Where(n => CellOf(new Vector2I(n.X, n.Y)) == cell))
        {
            if (!_npcSheets.TryGetValue(info.Sprite, out var sheet))
            {
                var path = _assets.NpcSpritePath(info.Sprite);
                _npcSheets[info.Sprite] = sheet = File.Exists(path) && Image.LoadFromFile(path) is { } image ? ImageTexture.CreateFromImage(image) : null;
            }
            var npc = new NpcActor(info, sheet) { Position = TileCenter(new Vector2I(info.X, info.Y), info.Height), Zone = _npcZones.GetValueOrDefault(info, _zone) };
            _world.AddChild(npc);
            list.Add(npc);
        }
        _npcs[cell] = list;
        if (list.Count > 0)
        {
            PlaceNpcs(list);
            RefreshQuestMarks();
        }
    }

    /// <summary>Drops NPCs onto the ground once their chunk's collision is in the physics space.</summary>
    async void PlaceNpcs(List<NpcActor> list)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        if (!IsInstanceValid(this))
            return;
        foreach (var npc in list.Where(n => IsInstanceValid(n) && !n.Moving))
            npc.Position = TileCenter(npc.Tile, GroundHeight(npc.Tile, npc.Info.Height));
    }

    void DespawnNpcs((int, int) cell)
    {
        if (!_npcs.Remove(cell, out var list))
            return;
        foreach (var npc in list)
            npc.QueueFree();
    }

    public bool IsWarp(Vector2I t) => _warps.ContainsKey(t);

    /// <summary>Tiles of the current map's warps that lead to a zone; for smoke tests.</summary>
    public IReadOnlyDictionary<Vector2I, WarpInfo> Warps => _warps;

    /// <summary>Name of the location the tile belongs to ("" for scenery).</summary>
    public string LocationAt(Vector2I t) =>
        _cellsByPos.TryGetValue(CellOf(t), out var c) ? c.Zone >= 0 ? c.Location : ZoneLocation : "";

    string ZoneLocation => _zones.TryGetValue(_zone, out var z) ? z.Location : "";

    (int, int) CellOf(Vector2I t) => (Mathf.FloorToInt(t.X / (float)ChunkTiles), Mathf.FloorToInt(t.Y / (float)ChunkTiles));

    bool IsBlocked(Vector2I t) => (TileFlags(t) & 1) != 0;

    /// <summary>Exported tile flags (bit 0 blocked, 1 tall grass, 2 water); off-map tiles are blocked.</summary>
    byte TileFlags(Vector2I t)
    {
        var (cx, cy) = CellOf(t);
        if (!_cellsByPos.TryGetValue((cx, cy), out var cell))
            return 1;
        var key = GameAssets.TilesKey(cell);
        if (!_tiles.TryGetValue(key, out var tiles))
            _tiles[key] = tiles = _assets.CellTiles(cell);
        var lx = t.X - cx * ChunkTiles;
        var ly = t.Y - cy * ChunkTiles;
        var i = ly * ChunkTiles + lx;
        return i >= 0 && i < tiles.Length ? tiles[i] : (byte)1;
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
            DespawnNpcs(key);
        }
        foreach (var key in wanted.Where(k => !_loaded.ContainsKey(k)))
        {
            var node = LoadChunk(_cellsByPos[key]);
            if (node is null)
                continue;
            _loaded[key] = node;
            // A season change reloads chunks but keeps their NPCs.
            if (!_npcs.ContainsKey(key))
                SpawnNpcs(key);
        }
    }

    Node3D? LoadChunk(WorldCell cell)
    {
        if (cell.Chunk < 0)
        {
            // An Infinite Route's cell is built from the real tiles it was quilted from; a rail map's collision-only
            // cell has nothing to draw, but it can hold NPCs.
            var built = RouteTerrain.Build(_assets, cell, _season) ?? new Node3D();
            built.Position = new Vector3(cell.X * ChunkTiles + ChunkTiles / 2f, 0, cell.Y * ChunkTiles + ChunkTiles / 2f);
            _world.AddChild(built);
            foreach (var mesh in built.FindChildren("*", nameof(MeshInstance3D), owned: false).OfType<MeshInstance3D>())
            {
                ForcePixelArt(mesh);
                AddGroundCollision(mesh);
            }
            return built;
        }
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(_assets.ChunkModelPath(cell, _season), state) != Error.Ok)
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
            AddGroundCollision(mesh);
        }
        // Replaces any animator left from a chunk this cell held before (another map or season).
        _animators.Remove((cell.X, cell.Y));
        if (cell.Animation >= 0 || cell.Pattern >= 0)
        {
            IReadOnlyList<Extractor.Nitro3D.SrtAnimation> anims = [];
            if (cell.Animation >= 0 && !_animations.TryGetValue(cell.Animation, out anims!))
                _animations[cell.Animation] = anims = _assets.LoadAnimation(cell.Animation);
            IReadOnlyList<Extractor.Gen5.TexturePattern> patterns = [];
            if (cell.Pattern >= 0 && !_patterns.TryGetValue(cell.Pattern, out patterns!))
                _patterns[cell.Pattern] = patterns = _assets.LoadPatterns(cell.Pattern);
            var textures = MaterialTextures(state);
            if (TextureAnimator.Attach(scene, anims, patterns, m => textures.GetValueOrDefault(m), image => PatternFrame(cell.Pattern, image))
                is { } animator)
                _animators[(cell.X, cell.Y)] = animator;
        }
        return scene;
    }

    /// <summary>Each material of a loaded glTF → the ROM texture name its base colour image was decoded from.</summary>
    static Dictionary<Material, string> MaterialTextures(GltfState state)
    {
        var result = new Dictionary<Material, string>();
        var json = state.Json;
        if (!json.TryGetValue("materials", out var materials) || !json.TryGetValue("textures", out var textures)
            || !json.TryGetValue("images", out var images))
            return result;
        var loaded = state.GetMaterials();
        var mats = materials.AsGodotArray();
        for (var i = 0; i < mats.Count && i < loaded.Count; i++)
        {
            if (!mats[i].AsGodotDictionary().TryGetValue("pbrMetallicRoughness", out var pbr)
                || !pbr.AsGodotDictionary().TryGetValue("baseColorTexture", out var color))
                continue;
            var texture = textures.AsGodotArray()[color.AsGodotDictionary()["index"].AsInt32()].AsGodotDictionary();
            var uri = images.AsGodotArray()[texture["source"].AsInt32()].AsGodotDictionary()["uri"].AsString();
            // Chunk images are "{texture}__{palette}.png" (see Gen5WorldExtractor).
            var name = uri.GetFile();
            var split = name.IndexOf("__", StringComparison.Ordinal);
            result[loaded[i]] = split > 0 ? name[..split] : name.GetBaseName();
        }
        return result;
    }

    Texture2D? PatternFrame(int pattern, string image)
    {
        var path = _assets.PatternImagePath(pattern, image);
        if (!_patternFrames.TryGetValue(path, out var texture))
            _patternFrames[path] = texture = File.Exists(path) && Image.LoadFromFile(path) is { } img ? ImageTexture.CreateFromImage(img) : null;
        return texture;
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

    /// <summary>
    /// Collision for ground-height raycasts from the mesh's opaque surfaces only. Cut-out and blended
    /// surfaces (tall grass, tree canopies, petals, shadows) sit above the walkable ground, so standing on
    /// them would lift the player on top of the grass instead of into it.
    /// </summary>
    static void AddGroundCollision(MeshInstance3D mesh)
    {
        var faces = new List<Vector3>();
        for (var s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
        {
            if (mesh.GetActiveMaterial(s) is BaseMaterial3D { Transparency: not BaseMaterial3D.TransparencyEnum.Disabled })
                continue;
            var arrays = mesh.Mesh.SurfaceGetArrays(s);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            if (indices.Length > 0)
                foreach (var i in indices)
                    faces.Add(vertices[i]);
            else
                faces.AddRange(vertices);
        }
        if (faces.Count == 0)
            return;
        var body = new StaticBody3D();
        body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = faces.ToArray() } });
        mesh.AddChild(body);
    }

    /// <summary>How far up a single step can climb; a surface higher than this over the next tile is overhead (a bridge).</summary>
    const float StepUp = 2.5f;

    /// <summary>
    /// Ground height at a tile: the highest surface under its centre that's no more than a step above
    /// <paramref name="near"/> (the height you come from), so you walk under a bridge rather than up onto it.
    /// </summary>
    float GroundHeight(Vector2I t, float near = 0)
    {
        var space = _world.GetWorld3D().DirectSpaceState;
        var from = new Vector3(t.X + 0.5f, near + StepUp, t.Y + 0.5f);
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * 80));
        return hit.Count > 0 ? ((Vector3)hit["position"]).Y : near;
    }

    void UpdateCamera(bool snap, float delta = 0)
    {
        if (!_camera.IsInsideTree())
            return;   // this frame took us off the screen (kicked or disconnected)
        // Roughly the DS framing: about 16 tiles across, looking down at ~45°.
        var offset = new Vector3(0, 17, 17) * _zoom;
        var target = _player.Position + offset;
        _camera.Position = snap ? target : _camera.Position.Lerp(target, Mathf.Min(1, delta * 8));
        _camera.LookAt(_camera.Position - offset, Vector3.Up);
    }

    void UpdateHud()
    {
        var name = LocationAt(_tile) is { Length: > 0 } location ? location : "Unova";
        _hud.Text = $"{name}   ·   {_season}   ·   tile {_tile.X}, {_tile.Y}";
        if (_online && InfiniteRoutes.Client.Online.OnlineSession.Game?.Welcome is { } welcome)
            _hud.Text += $"\n{welcome.ServerName} · channel {(_channel > 0 ? _channel : welcome.Channel)} of {welcome.Channels} · {InfiniteRoutes.Client.Online.OnlineSession.Character?.Name}{AdminHud}";
        if (_battles)
            OfflineSession.State.Location = new Core.Player.PlayerLocation(_zone, _tile.X, _tile.Y, (int)DirToFacing(_dir));
        UpdateQuestHud();
    }
}
