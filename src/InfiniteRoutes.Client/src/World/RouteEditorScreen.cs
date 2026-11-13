using Godot;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// The route editor: makes Infinite Routes by hand, drawn live with the chosen look's real tiles
/// (<see cref="RouteKit"/>) as they'll appear in the game.
/// <list type="bullet">
/// <item><b>Tiles</b>: paint what each tile is — path, open ground, tall grass, forest, water — on the 2×2-tile grid
/// the forest is made on; inside the white outline (the playable area) or outside it (scenery only: always solid).</item>
/// <item><b>Objects</b>: stamps — pieces of the look's real maps copied as they are (signs, fences, ledges, cliffs,
/// bridges, ponds, flowers, buildings), look and collision; and HM obstacles (a small tree to cut, a boulder to move).</item>
/// <item><b>People</b>: NPCs with a sprite, facing, movement and something to say; trainers and gift-givers.</item>
/// <item><b>Wild</b>: the route's wild Pokémon — a real route's, or its own list.</item>
/// <item><b>Ends</b>: walkable tiles that reach the outline are the route's ends: an entrance and up to three exits,
/// each leading by default along the route tree, or to any route or real place.</item>
/// </list>
/// It runs inside the online game, for roles with the route-editing permission: <paramref name="saved"/> are the
/// routes the server has, saving sends the route to the server (<paramref name="send"/>), which checks the permission,
/// stores it and sends it to every player.
/// </summary>
public partial class RouteEditorScreen(Func<IReadOnlyDictionary<int, RouteFile>> saved, Action<Message> send, Action close) : Control
{
    const int N = UnchartedRoutes.CellTiles;
    const int MaxUndo = 60;
    /// <summary>Where the look's real maps are laid out for picking stamps, far from the route.</summary>
    const int SourceX = 4000;

    enum Tool { Paint, Stamp, PickStamp, Person, Obstacle, Entrance }

    static readonly (TileKind Kind, string Label, string Key)[] Brushes =
    [
        (TileKind.Path, "Path", "1"), (TileKind.Ground, "Ground", "2"), (TileKind.Grass, "Tall grass", "3"),
        (TileKind.Tree, "Forest", "4"), (TileKind.Water, "Water", "5"),
    ];

    GameAssets _assets = null!;
    readonly List<TerrainTheme> _themes = [];
    readonly Season _season = (Season)((DateTime.Now.Month - 1) % 4);
    IReadOnlyList<Core.Data.SpeciesInfo> _species = [];
    IReadOnlyList<Core.Data.TrainerInfo> _trainers = [];
    List<ZoneInfo> _wildZones = [];

    // The route being edited. Tiles cover the whole map: the playable area and the cell of scenery round it.
    int _node;
    string _name = "";
    int _area;
    int _w = 4 * N, _h = 2 * N;
    TileKind[] _kinds = [];
    bool _outerPainted;
    int _entranceX = -1, _entranceY = -1;
    List<RouteLink> _links = [];
    List<RouteNpc> _npcs = [];
    List<RouteStamp> _stamps = [];
    List<RouteObstacle> _obstacles = [];
    List<RouteWild> _wild = [];
    int _wildRate = 10, _encounterZone = -1;
    bool _customWild;
    readonly List<RouteFile> _undo = [];
    bool _dirty;

    // What's drawn.
    GeneratedRoute? _route;
    readonly Dictionary<(int, int), Node3D> _cells = new();
    readonly HashSet<(int, int)> _stale = [];
    double _redrawIn = -1;
    SubViewport _view = null!;
    SubViewportContainer _container = null!;
    Camera3D _camera = null!;
    Node3D _world = null!, _marks = null!, _things = null!, _source = null!;
    MeshInstance3D _cursor = null!;
    Vector2 _focus, _sourceFocus;
    float _zoom = 2.2f;
    readonly Dictionary<int, Texture2D?> _sprites = new();
    bool _sourceLoaded;

    // Tools.
    Tool _tool = Tool.Paint;
    TileKind _brush = TileKind.Path;
    int _brushBlocks = 1;
    string _obstacleKind = "cut";
    /// <summary>What Place puts down: a piece of set <c>Area</c>'s maps, whole or just its objects.</summary>
    (int Area, int X, int Y, int W, int H, bool ObjectsOnly, string Label)? _clipboard;
    /// <summary>The texture set whose maps the Objects tab shows (and stamps are picked from).</summary>
    int _sourceArea = -1;
    bool _pickObjectsOnly;
    Vector2I? _pickFrom;
    int _selectedNpc = -1;
    bool _painting, _panning, _strokeSaved, _fillingFields;
    Vector2I _lastPainted = new(-999, -999);

    readonly List<Button> _brushButtons = [];
    SpinBox _number = null!, _cellsWide = null!, _cellsHigh = null!;
    LineEdit _nameBox = null!;
    OptionButton _themeBox = null!, _saved = null!, _sizeBox = null!;
    Label _status = null!, _hint = null!, _stampLabel = null!, _trainerName = null!, _giftName = null!;
    OptionButton _sourceBox = null!, _npcKind = null!, _trainerClass = null!;
    ItemList _objectList = null!;
    LineEdit _objectFilter = null!, _trainerNameBox = null!;
    TextEdit _trainerDefeat = null!, _trainerAfter = null!;
    SpinBox _trainerPrize = null!;
    VBoxContainer _partyRows = null!;
    Control _trainerFields = null!, _giftFields = null!;
    readonly List<(int Class, string Name)> _classes = [];
    // People.
    SpinBox _npcSprite = null!, _npcTrainer = null!, _npcSight = null!, _npcGift = null!, _npcGiftCount = null!;
    OptionButton _npcFacing = null!, _npcMovement = null!;
    TextEdit _npcText = null!;
    TextureRect _npcPreview = null!;
    Control _npcFields = null!;
    // Wild and ends.
    OptionButton _wildSource = null!;
    SpinBox _wildRateBox = null!;
    VBoxContainer _wildRows = null!, _endRows = null!;

    public override void _Ready()
    {
        if (ClientServices.UnovaAssets is not { } assets || assets.LoadOverworld() is null)
        {
            ShowProblem("No map data yet. Extract your Black/White ROM in Settings → ROM Library.");
            return;
        }
        foreach (var area in assets.ThemeAreas)
            if (assets.Theme(area) is { Usable: true } theme)
                _themes.Add(theme);
        if (_themes.Count == 0)
        {
            ShowProblem("None of this game's routes can be used as a look for new ones.");
            return;
        }
        _assets = assets;
        _area = _themes[0].Area;
        _species = assets.LoadSpecies();
        _trainers = assets.LoadTrainers();
        _classes.AddRange(_trainers.GroupBy(t => t.Class).Select(g => (g.Key, g.First().ClassName)).Where(c => c.ClassName.Length > 0)
            .OrderBy(c => c.ClassName, StringComparer.OrdinalIgnoreCase));
        _sourceArea = _area;
        _wildZones = assets.LoadZones().Where(z => z.Encounters >= 0 && z.Location.Length > 0).GroupBy(z => z.Location).Select(g => g.First())
            .OrderBy(z => z.Location, StringComparer.OrdinalIgnoreCase).ToList();

        var backdrop = new ColorRect { Color = new Color(0.07f, 0.08f, 0.1f), MouseFilter = MouseFilterEnum.Stop };
        AddChild(backdrop);
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _container = new SubViewportContainer { Stretch = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        _view = new SubViewport { OwnWorld3D = true, HandleInputLocally = false };
        _container.AddChild(_view);
        foreach (var node in new[] { _world = new Node3D(), _marks = new Node3D(), _things = new Node3D(), _source = new Node3D() })
            _view.AddChild(node);
        _camera = new Camera3D { Fov = 35, Far = 900 };
        _view.AddChild(_camera);
        _view.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.55f, 0.75f, 0.95f) },
        });
        _cursor = new MeshInstance3D { Mesh = new PlaneMesh { Size = Vector2.One }, MaterialOverride = Flat(new Color(1, 1, 1, 0.35f)) };
        _view.AddChild(_cursor);
        _container.GuiInput += OnViewInput;
        _container.MouseExited += () => _cursor.Visible = false;

        var layout = new HBoxContainer();
        layout.AddThemeConstantOverride("separation", 0);
        layout.AddChild(BuildPanel());
        layout.AddChild(_container);
        AddChild(layout);
        layout.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _sourceBox.Select(Math.Max(0, _sourceBox.GetItemIndex(_sourceArea)));
        FillObjects();
        Open(saved().Keys.Order().Select(k => (int?)k).FirstOrDefault() ?? 0);
    }

    void ShowProblem(string text)
    {
        var page = Ui.Page.Create("Route Editor", Ui.Ui.VBox(12, Ui.Ui.Text(text, 18, Ui.Ui.Warn, wrap: true), Ui.Ui.Button("Back to the game", close)));
        AddChild(page);
        page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    static StandardMaterial3D Flat(Color color) => new()
    {
        AlbedoColor = color, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true,
    };

    TerrainTheme Theme => _themes.First(t => t.Area == _area);
    float BaseHeight => Theme.BaseHeight;
    int FullW => _w + 2 * N;
    int FullH => _h + 2 * N;

    // ---------------------------------------------------------------- the panel

    Control BuildPanel()
    {
        _number = new SpinBox { MinValue = 0, MaxValue = 99999, Step = 1, CustomMinimumSize = new Vector2(110, 0) };
        _nameBox = new LineEdit { PlaceholderText = UnchartedRoutes.Unnamed, MaxLength = 20, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _nameBox.TextChanged += text => { _name = text; _dirty = true; };
        _saved = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _saved.ItemSelected += i => Open((int)_saved.GetItemId((int)i));
        _themeBox = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var theme in _themes)
            _themeBox.AddItem($"{theme.Name}'s look", theme.Area);
        _themeBox.ItemSelected += i =>
        {
            Remember();
            _area = (int)_themeBox.GetItemId((int)i);
            // Stamps saved as pieces of the old look's own maps go; ones taken from a named set stay.
            _stamps.RemoveAll(st => st.Area < 0);
            Redraw(everything: true);
        };
        _cellsWide = new SpinBox { MinValue = 1, MaxValue = 8, Step = 1 };
        _cellsHigh = new SpinBox { MinValue = 1, MaxValue = 6, Step = 1 };

        var tabs = new TabContainer { CustomMinimumSize = new Vector2(0, 330), SizeFlagsVertical = SizeFlags.ExpandFill };
        void Tab(string title, Control content)
        {
            var scroll = new ScrollContainer { Name = title, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            var inner = Ui.Ui.Margin(content, 8);
            inner.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            scroll.AddChild(inner);
            tabs.AddChild(scroll);
        }
        Tab("Tiles", TilesTab());
        Tab("Objects", ObjectsTab());
        Tab("People", PeopleTab());
        Tab("Wild", WildTab());
        Tab("Ends", EndsTab());
        tabs.TabChanged += tab => SetTool(tab switch { 0 => Tool.Paint, 2 => Tool.Person, 1 => _tool is Tool.Stamp or Tool.Obstacle or Tool.PickStamp ? _tool : Tool.Obstacle, _ => Tool.Paint });

        _status = Ui.Ui.Text("", 13, Ui.Ui.Muted, wrap: true);
        _hint = Ui.Ui.Text("", 12, Ui.Ui.Muted, wrap: true);
        var panel = Ui.Ui.VBox(6,
            Ui.Ui.Text("Route Editor", 22, Ui.Ui.Accent),
            Ui.Ui.HBox(6, Ui.Ui.Text("Route", 13, Ui.Ui.Muted), _number, Ui.Ui.Button("Open", () => Open((int)_number.Value)), _saved),
            Ui.Ui.HBox(6, Ui.Ui.Text("Name", 13, Ui.Ui.Muted), _nameBox),
            Ui.Ui.HBox(6, Ui.Ui.Text("Look", 13, Ui.Ui.Muted), _themeBox),
            Ui.Ui.HBox(6, Ui.Ui.Text("Cells", 13, Ui.Ui.Muted), _cellsWide, Ui.Ui.Text("×", 13, Ui.Ui.Muted), _cellsHigh,
                Ui.Ui.Button("Resize", () => Resize((int)_cellsWide.Value * N, (int)_cellsHigh.Value * N))),
            tabs,
            Ui.Ui.HBox(6, Ui.Ui.Button("Undo", Undo), Ui.Ui.Button("Clear", Clear), Ui.Ui.Button("Generate", () => Generate(Random.Shared.Next()))),
            Ui.Ui.HBox(6, Ui.Ui.Button("Save", () => Save(), 18), Ui.Ui.Button("Test walk", TestWalk, 18), Ui.Ui.Button("Back to the game", close)),
            _status, _hint);
        var margin = Ui.Ui.Margin(panel, 12);
        margin.CustomMinimumSize = new Vector2(400, 0);
        var back = new PanelContainer();
        back.AddChild(margin);
        return back;
    }

    Control TilesTab()
    {
        _sizeBox = new OptionButton();
        foreach (var blocks in new[] { 1, 2, 3, 4 })
            _sizeBox.AddItem($"{blocks * 2}×{blocks * 2} tiles", blocks);
        _sizeBox.ItemSelected += i => _brushBlocks = (int)_sizeBox.GetItemId((int)i);
        var brushes = new VBoxContainer();
        foreach (var (kind, label, key) in Brushes)
        {
            var button = Ui.Ui.Button($"{key}  {label}", () => { _brush = kind; SetTool(Tool.Paint); });
            button.ToggleMode = true;
            button.Alignment = HorizontalAlignment.Left;
            _brushButtons.Add(button);
            brushes.AddChild(button);
        }
        return Ui.Ui.VBox(6, brushes, Ui.Ui.HBox(6, Ui.Ui.Text("Brush size", 13, Ui.Ui.Muted), _sizeBox),
            Ui.Ui.Text("Inside the white outline is the playable area. Outside it is scenery: paint it as you like, it's always solid.", 12, Ui.Ui.Muted, wrap: true));
    }

    /// <summary>A model's name as a person would say it ("sign01a" → "Sign").</summary>
    static string ObjectLabel(string name) => name switch
    {
        _ when name.StartsWith("sign", StringComparison.Ordinal) => "Sign",
        _ when name.StartsWith("searock", StringComparison.Ordinal) => "Sea rock",
        _ when name.StartsWith("rock", StringComparison.Ordinal) => "Rock",
        _ when name.StartsWith("gate", StringComparison.Ordinal) => "Route gate",
        _ when name.StartsWith("plant", StringComparison.Ordinal) => "Plant",
        _ when name.Contains("house", StringComparison.Ordinal) => "House",
        _ when name.Contains("buil", StringComparison.Ordinal) => "Building",
        _ when name.StartsWith("kasa", StringComparison.Ordinal) => "Parasol",
        _ when name.StartsWith("isu", StringComparison.Ordinal) => "Bench",
        _ when name.StartsWith("jihan", StringComparison.Ordinal) => "Vending machine",
        _ when name.Contains("kira", StringComparison.Ordinal) => "Sparkle",
        _ => name,
    };

    Control ObjectsTab()
    {
        _stampLabel = Ui.Ui.Text("Nothing picked yet.", 13, Ui.Ui.Muted, wrap: true);
        _sourceBox = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var (area, name) in _assets.SourceAreas)
            _sourceBox.AddItem(name, area);
        _sourceBox.ItemSelected += i =>
        {
            _sourceArea = (int)_sourceBox.GetItemId((int)i);
            ClearSource();
            FillObjects();
            if (_tool == Tool.PickStamp)
                SetTool(Tool.PickStamp);
        };
        _objectFilter = new LineEdit { PlaceholderText = "Filter (sign, rock, gate…)", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _objectFilter.TextChanged += _ => FillObjects();
        _objectList = new ItemList { CustomMinimumSize = new Vector2(0, 150), SizeFlagsVertical = SizeFlags.ExpandFill };
        _objectList.ItemSelected += i =>
        {
            if (_assets.SourceTheme(_sourceArea) is not { } from || _objectList.GetItemMetadata((int)i).AsInt32() is var k && (k < 0 || k >= from.Objects.Count))
                return;
            var o = from.Objects[k];
            _clipboard = (_sourceArea, o.X, o.Y, o.W, o.H, true, ObjectLabel(o.Name));
            SetTool(Tool.Stamp);
        };
        var objectsOnly = new CheckBox { Text = "Only the objects on it (keep my ground)" };
        objectsOnly.Toggled += on => _pickObjectsOnly = on;
        return Ui.Ui.VBox(6,
            Ui.Ui.Text("Maps of", 13, Ui.Ui.Muted), _sourceBox,
            Ui.Ui.Text("Objects on these maps", 15, Ui.Ui.Accent),
            Ui.Ui.Text("Signs, rocks, gates, houses… found on Unova's real maps. Pick one, then click your route to place it on your own ground.", 12, Ui.Ui.Muted, wrap: true),
            _objectFilter, _objectList,
            Ui.Ui.Text("A piece of these maps", 15, Ui.Ui.Accent),
            Ui.Ui.Text("Drag a rectangle over anything — fences, ledges, cliffs, bridges, ponds, flowers — and place it, look and collision.", 12, Ui.Ui.Muted, wrap: true),
            objectsOnly,
            Ui.Ui.HBox(6, Ui.Ui.Button("Pick from the maps…", () => SetTool(Tool.PickStamp)), Ui.Ui.Button("Place", () => SetTool(Tool.Stamp))),
            _stampLabel,
            Ui.Ui.Text("HM obstacles", 15, Ui.Ui.Accent),
            Ui.Ui.HBox(6,
                Ui.Ui.Button("Small tree (Cut)", () => { _obstacleKind = "cut"; SetTool(Tool.Obstacle); }),
                Ui.Ui.Button("Boulder (Strength)", () => { _obstacleKind = "strength"; SetTool(Tool.Obstacle); })),
            Ui.Ui.Text("A small tree needs 1 Gym Badge to cut, a boulder 3 to move. Each is cleared for the player who clears it, until they leave the route.", 12, Ui.Ui.Muted, wrap: true),
            Ui.Ui.Text("Right-click removes the stamp or obstacle under the pointer.", 12, Ui.Ui.Muted, wrap: true));
    }

    /// <summary>The object list for the chosen set's maps, filtered.</summary>
    void FillObjects()
    {
        _objectList.Clear();
        if (_assets.SourceTheme(_sourceArea) is not { } from)
            return;
        var filter = _objectFilter.Text.Trim();
        for (var k = 0; k < from.Objects.Count; k++)
        {
            var o = from.Objects[k];
            var label = $"{ObjectLabel(o.Name)}  ({o.W}×{o.H})";
            if (filter.Length > 0 && !label.Contains(filter, StringComparison.OrdinalIgnoreCase) && !o.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var row = _objectList.AddItem(label);
            _objectList.SetItemMetadata(row, k);
            _objectList.SetItemTooltip(row, $"{o.Name} at {o.X}, {o.Y} of these maps");
        }
    }

    Control PeopleTab()
    {
        _npcSprite = new SpinBox { MinValue = 0, MaxValue = 9999, Step = 1 };
        _npcPreview = new TextureRect { CustomMinimumSize = new Vector2(64, 64), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = TextureFilterEnum.Nearest };
        _npcFacing = new OptionButton();
        foreach (var facing in new[] { "Down", "Up", "Left", "Right" })
            _npcFacing.AddItem(facing);
        _npcMovement = new OptionButton();
        _npcMovement.AddItem("Stands still", 0);
        _npcMovement.AddItem("Looks around", 2);
        _npcMovement.AddItem("Wanders", 3);
        _npcKind = new OptionButton();
        _npcKind.AddItem("Talks only", 0);
        _npcKind.AddItem("Battles (a trainer)", 1);
        _npcKind.AddItem("Gives an item (first chat)", 2);
        _npcText = new TextEdit { CustomMinimumSize = new Vector2(0, 80), WrapMode = TextEdit.LineWrappingMode.Boundary, PlaceholderText = "What they say (a trainer: before the battle). A blank line starts a new page." };
        _npcSight = new SpinBox { MinValue = 0, MaxValue = 8, Step = 1 };
        _npcGift = new SpinBox { MinValue = 1, MaxValue = 999, Step = 1 };
        _npcGiftCount = new SpinBox { MinValue = 1, MaxValue = 99, Step = 1 };
        _giftName = Ui.Ui.Text("", 12, Ui.Ui.Muted, wrap: true);
        _trainerName = Ui.Ui.Text("", 12, Ui.Ui.Muted, wrap: true);
        _trainerNameBox = new LineEdit { MaxLength = 12, PlaceholderText = "Name", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _trainerClass = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var (id, name) in _classes)
            _trainerClass.AddItem(name, id);
        _trainerPrize = new SpinBox { MinValue = 0, MaxValue = 999_999, Step = 10, Prefix = "₽", CustomMinimumSize = new Vector2(120, 0) };
        _trainerDefeat = new TextEdit { CustomMinimumSize = new Vector2(0, 54), WrapMode = TextEdit.LineWrappingMode.Boundary, PlaceholderText = "On losing the battle" };
        _trainerAfter = new TextEdit { CustomMinimumSize = new Vector2(0, 54), WrapMode = TextEdit.LineWrappingMode.Boundary, PlaceholderText = "When talked to afterwards" };
        _partyRows = new VBoxContainer();

        _npcSprite.ValueChanged += _ => NpcEdited();
        _npcFacing.ItemSelected += _ => NpcEdited();
        _npcMovement.ItemSelected += _ => NpcEdited();
        _npcKind.ItemSelected += _ => NpcEdited();
        _npcText.TextChanged += NpcEdited;
        _trainerNameBox.TextChanged += _ => NpcEdited();
        _trainerClass.ItemSelected += _ => NpcEdited();
        _trainerDefeat.TextChanged += NpcEdited;
        _trainerAfter.TextChanged += NpcEdited;
        foreach (var box in new[] { _npcSight, _npcGift, _npcGiftCount, _trainerPrize })
            box.ValueChanged += _ => NpcEdited();

        _trainerFields = Ui.Ui.VBox(5,
            Ui.Ui.HBox(6, Ui.Ui.Text("Trainer", 13, Ui.Ui.Muted), _trainerClass, _trainerNameBox),
            _trainerName,
            Ui.Ui.HBox(6, Ui.Ui.Text("Sees", 13, Ui.Ui.Muted), _npcSight, Ui.Ui.Text("tiles · prize", 13, Ui.Ui.Muted), _trainerPrize),
            Ui.Ui.Text("Their Pokémon (up to 6)", 13, Ui.Ui.Muted), _partyRows,
            _trainerDefeat, _trainerAfter);
        _giftFields = Ui.Ui.VBox(5,
            Ui.Ui.HBox(6, Ui.Ui.Text("Item", 13, Ui.Ui.Muted), _npcGift, Ui.Ui.Text("×", 13, Ui.Ui.Muted), _npcGiftCount),
            _giftName,
            Ui.Ui.Text("Given once per player, the first time they talk.", 12, Ui.Ui.Muted, wrap: true));
        _npcFields = Ui.Ui.VBox(6,
            Ui.Ui.HBox(6, Ui.Ui.Text("Sprite", 13, Ui.Ui.Muted), _npcSprite, _npcPreview),
            Ui.Ui.HBox(6, Ui.Ui.Text("Faces", 13, Ui.Ui.Muted), _npcFacing, _npcMovement),
            Ui.Ui.HBox(6, Ui.Ui.Text("Kind", 13, Ui.Ui.Muted), _npcKind),
            _npcText, _trainerFields, _giftFields,
            Ui.Ui.Button("Remove this person", () =>
            {
                if (_selectedNpc < 0 || _selectedNpc >= _npcs.Count)
                    return;
                Remember();
                _npcs.RemoveAt(_selectedNpc);
                SelectNpc(-1);
                DrawThings();
            }));
        return Ui.Ui.VBox(6,
            Ui.Ui.Text("Click a walkable tile to put a person there; click a person to change them; right-click removes.", 12, Ui.Ui.Muted, wrap: true),
            _npcFields);
    }

    Control WildTab()
    {
        _wildSource = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _wildSource.AddItem("The look's own route", -1);
        _wildSource.AddItem("This route's own list (below)", -2);
        foreach (var zone in _wildZones)
            _wildSource.AddItem(zone.Location, zone.Id);
        _wildSource.ItemSelected += i =>
        {
            Remember();
            var id = (int)_wildSource.GetItemId((int)i);
            (_customWild, _encounterZone) = (id == -2, id >= 0 ? id : -1);
            FillWild();
        };
        _wildRateBox = new SpinBox { MinValue = 1, MaxValue = 100, Step = 1, Suffix = "%" };
        _wildRateBox.ValueChanged += v =>
        {
            if (_fillingFields)
                return;
            _wildRate = (int)v;
            _dirty = true;
        };
        _wildRows = new VBoxContainer();
        return Ui.Ui.VBox(6,
            Ui.Ui.Text("Wild Pokémon in the tall grass", 13, Ui.Ui.Muted), _wildSource,
            Ui.Ui.HBox(6, Ui.Ui.Text("Chance per step", 13, Ui.Ui.Muted), _wildRateBox),
            _wildRows);
    }

    Control EndsTab()
    {
        _endRows = new VBoxContainer();
        return Ui.Ui.VBox(6,
            Ui.Ui.Text("Walkable tiles that reach the white outline are the route's ends. By default the entrance leads back to the route this one is reached from, and exit k on to route 3n+1+k.", 12, Ui.Ui.Muted, wrap: true),
            Ui.Ui.Button("Choose the entrance…", () => SetTool(Tool.Entrance)),
            _endRows);
    }

    // ---------------------------------------------------------------- the route

    RouteFile Current
    {
        get
        {
            var rows = new List<string>();
            for (var y = 0; y < _h; y++)
                rows.Add(new string(Enumerable.Range(0, _w).Select(x => RouteFile.Letter(At(x, y))).ToArray()));
            List<string>? outer = null;
            if (_outerPainted)
            {
                outer = [];
                for (var y = 0; y < FullH; y++)
                    outer.Add(new string(Enumerable.Range(0, FullW).Select(x => RouteFile.Letter(_kinds[y * FullW + x])).ToArray()));
            }
            return new RouteFile(_node, _name.Trim(), _area, _w, _h, rows, _entranceX, _entranceY, outer,
                _links.Count > 0 ? [.. _links] : null, _npcs.Count > 0 ? [.. _npcs] : null, _stamps.Count > 0 ? [.. _stamps] : null,
                _obstacles.Count > 0 ? [.. _obstacles] : null, _customWild && _wild.Count > 0 ? [.. _wild] : null,
                _customWild ? _wildRate : 0, _customWild ? -1 : _encounterZone);
        }
    }

    /// <summary>A tile by its place in the playable area (the scenery round it is at −32…−1 and past the far edges).</summary>
    TileKind At(int x, int y) => x >= -N && y >= -N && x < _w + N && y < _h + N ? _kinds[(y + N) * FullW + x + N] : TileKind.Tree;

    bool InArea(int x, int y) => x >= 0 && y >= 0 && x < _w && y < _h;

    void Load(RouteFile file)
    {
        (_name, _w, _h, _entranceX, _entranceY) = (file.Name, file.Width, file.Height, file.EntranceX, file.EntranceY);
        if (_themes.Any(t => t.Area == file.Area))
            _area = file.Area;
        _links = [.. file.Links ?? []];
        _npcs = [.. file.Npcs ?? []];
        _stamps = [.. file.Stamps ?? []];
        _obstacles = [.. file.Obstacles ?? []];
        _wild = [.. file.Wild ?? []];
        _customWild = file.Wild is { Count: > 0 } && file.WildRate > 0;
        _wildRate = file.WildRate > 0 ? file.WildRate : 10;
        _encounterZone = file.EncounterZone;
        _outerPainted = file.Outer is not null;
        _kinds = Enumerable.Repeat(TileKind.Tree, FullW * FullH).ToArray();
        for (var y = -N; y < _h + N; y++)
            for (var x = -N; x < _w + N; x++)
                _kinds[(y + N) * FullW + x + N] = InArea(x, y) ? file.At(x, y) : file.OuterAt(x, y) ?? TileKind.Tree;
        // The unpainted scenery starts as the game draws it (forest, the ground carrying on past each end).
        if (!_outerPainted && _assets.PreviewRoute(file) is { } built)
            for (var y = 0; y < FullH; y++)
                for (var x = 0; x < FullW; x++)
                    if (!InArea(x - N, y - N) && built.Source[y * FullW + x] is var s and >= 0 && built.Theme.Kind[s] is var kind and not TileKind.Other)
                        _kinds[y * FullW + x] = kind;
    }

    /// <summary>Opens the saved route with this number, or starts an empty one (all forest).</summary>
    public void Open(int node)
    {
        if (_dirty && _route is not null)
            Save();
        _node = node;
        _undo.Clear();
        Load(saved().TryGetValue(node, out var file) && file.Width % N == 0 && file.Height % N == 0
            ? file
            : new RouteFile(node, "", _area, 4 * N, 2 * N, Enumerable.Repeat(new string('T', 4 * N), 2 * N).ToList()));
        _dirty = false;
        _focus = new Vector2(N + _w / 2f, N + _h / 2f);
        AfterLoad();
    }

    /// <summary>Everything on screen from the loaded route.</summary>
    void AfterLoad()
    {
        _fillingFields = true;
        _number.SetValueNoSignal(_node);
        _nameBox.Text = _name;
        _themeBox.Select(_themeBox.GetItemIndex(_area));
        _cellsWide.SetValueNoSignal(_w / N);
        _cellsHigh.SetValueNoSignal(_h / N);
        _fillingFields = false;
        RefreshSaved();
        SelectNpc(-1);
        FillWild();
        SetTool(_tool is Tool.PickStamp ? Tool.Paint : _tool);
        Redraw(everything: true);
        UpdateCamera();
    }

    void RefreshSaved()
    {
        _saved.Clear();
        foreach (var (node, name) in saved().OrderBy(r => r.Key).Select(r => (r.Key, r.Value.Name)))
            _saved.AddItem($"{node}: {(name.Length > 0 ? name : UnchartedRoutes.Unnamed)}", node);
        var index = _saved.GetItemIndex(_node);
        if (index < 0)
        {
            _saved.AddItem($"{_node}: (not saved yet)", _node);
            index = _saved.ItemCount - 1;
        }
        _saved.Select(index);
    }

    /// <summary>Keeps the route as it is now for Undo; call before changing it.</summary>
    void Remember()
    {
        _undo.Add(Current);
        if (_undo.Count > MaxUndo)
            _undo.RemoveAt(0);
        _dirty = true;
    }

    public void Undo()
    {
        if (_undo.Count == 0)
            return;
        var file = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Load(file);
        _dirty = true;
        AfterLoad();
    }

    void Clear()
    {
        Remember();
        Array.Fill(_kinds, TileKind.Tree);
        _stamps.Clear();
        _npcs.Clear();
        _obstacles.Clear();
        _links.Clear();
        SelectNpc(-1);
        Redraw(everything: true);
    }

    /// <summary>Keeps what's there (from the area's top-left corner) at the new size.</summary>
    void Resize(int width, int height)
    {
        if (width == _w && height == _h)
            return;
        Remember();
        var old = Current;
        Load(old with
        {
            Width = width, Height = height, Outer = null,
            Rows = Enumerable.Range(0, height).Select(y => new string(Enumerable.Range(0, width).Select(x => RouteFile.Letter(old.At(x, y))).ToArray())).ToList(),
            Npcs = old.Npcs?.Where(n => n.X < width && n.Y < height).ToList(),
            Obstacles = old.Obstacles?.Where(o => o.X < width && o.Y < height).ToList(),
        });
        AfterLoad();
    }

    /// <summary>Fills the area with a generated layout (<see cref="RouteLayout"/>) to start from.</summary>
    public void Generate(int seed)
    {
        Remember();
        var design = RouteLayout.Draw(new Random(seed), FullW, FullH, (N, N, _w, _h), 1 + Math.Abs(seed) % 3,
            Theme.Kit.Has(TileKind.Grass), Theme.Kit.Has(TileKind.Water));
        for (var y = 0; y < FullH; y++)
            for (var x = 0; x < FullW; x++)
                _kinds[y * FullW + x] = design.At(x, y);
        _entranceX = _entranceY = -1;
        _outerPainted = false;
        Redraw(everything: true);
    }

    /// <summary>Paints the brush with its top-left block at tile (x, y) (area coordinates). True if anything changed.</summary>
    public bool Paint(int x, int y, TileKind kind, int blocks)
    {
        var changed = false;
        int bx = Mathf.FloorToInt(x / 2f) * 2, by = Mathf.FloorToInt(y / 2f) * 2;
        for (var ty = by; ty < by + blocks * 2; ty++)
            for (var tx = bx; tx < bx + blocks * 2; tx++)
                if (tx >= -N && ty >= -N && tx < _w + N && ty < _h + N && At(tx, ty) != kind)
                {
                    _kinds[(ty + N) * FullW + tx + N] = kind;
                    changed = true;
                    if (!InArea(tx, ty))
                        _outerPainted = true;
                }
        if (!changed)
            return false;
        _dirty = true;
        MarkStale(bx, by, blocks * 2, blocks * 2);
        return true;
    }

    /// <summary>The cells a changed rectangle (area coordinates) touches, and their neighbours' edges (a tile's look depends on the tiles round it).</summary>
    void MarkStale(int x, int y, int w, int h)
    {
        for (var cy = (y + N - 1) / N; cy <= (y + h + N) / N; cy++)
            for (var cx = (x + N - 1) / N; cx <= (x + w + N) / N; cx++)
                _stale.Add((cx, cy));
    }

    /// <summary>
    /// Sends the route to the server to save. The server answers (a toast: saved, or why not) and sends the saved
    /// route to everyone, this client included. False if there's nothing to send yet (no entrance).
    /// </summary>
    public bool Save()
    {
        if (_route is null)
        {
            Main.Instance.Toast("This route has no entrance yet: bring a path to the edge of the area.");
            return false;
        }
        send(new AdminAction("route-save", _node.ToString(), new Dictionary<string, string> { ["json"] = RouteFiles.ToJson(Current) }));
        _dirty = false;
        UpdateStatus();
        return true;
    }

    /// <summary>Saves, then goes to the route's entrance in the game.</summary>
    public void TestWalk()
    {
        if (!Save())
            return;
        send(new AdminCommand($"route {_node}"));
        close();
    }

    /// <summary>The server's routes changed (a save went through, here or from someone else).</summary>
    public void RoutesChanged() => RefreshSaved();

    public override void _ExitTree()
    {
        // Leaving with changes: save them rather than lose them.
        if (_dirty && _assets is not null && _route is not null)
            Save();
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>Builds the route from the design again and redraws the cells that changed (or all of them).</summary>
    void Redraw(bool everything = false)
    {
        _redrawIn = -1;
        var file = Current;
        _route = _assets.PreviewRoute(file);
        int cellsW = FullW / N, cellsH = FullH / N;
        if (everything)
        {
            foreach (var node in _cells.Values)
                node.QueueFree();
            _cells.Clear();
            _stale.Clear();
            for (var cy = 0; cy < cellsH; cy++)
                for (var cx = 0; cx < cellsW; cx++)
                    _stale.Add((cx, cy));
        }
        // With no ends the route can't be built: draw it as if a path came in at the corner, so there's something to see.
        var shown = _route ?? _assets.PreviewRoute(file with { Rows = file.Rows.Select((r, y) => y == 0 ? "p" + r[1..] : r).ToList() });
        foreach (var (cx, cy) in _stale)
        {
            if (_cells.Remove((cx, cy), out var old))
                old.QueueFree();
            if (shown is null || cx < 0 || cy < 0 || cx >= cellsW || cy >= cellsH)
                continue;
            var cell = RouteTerrain.Build(_assets, shown, cx, cy, _season);
            cell.Position = new Vector3(cx * N + N / 2f, 0, cy * N + N / 2f);
            PixelArt(cell);
            _world.AddChild(cell);
            _cells[(cx, cy)] = cell;
        }
        _stale.Clear();
        DrawMarks();
        DrawThings();
        UpdateStatus();
    }

    static void PixelArt(Node3D node)
    {
        foreach (var mesh in node.FindChildren("*", nameof(MeshInstance3D), owned: false).OfType<MeshInstance3D>())
            for (var s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
                if (mesh.Mesh.SurfaceGetMaterial(s) is BaseMaterial3D m)
                {
                    m.TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
                    m.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                }
    }

    void Slab(Node3D parent, float x, float z, float w, float d, Color color, float lift = 0.12f) => parent.AddChild(new MeshInstance3D
    {
        Mesh = new PlaneMesh { Size = new Vector2(w, d) },
        Position = new Vector3(x + w / 2, BaseHeight + lift, z + d / 2),
        MaterialOverride = Flat(color),
    });

    void Outline(Node3D parent, float x, float z, float w, float d, Color color, float t = 0.15f)
    {
        Slab(parent, x - t, z - t, w + 2 * t, t, color);
        Slab(parent, x - t, z + d, w + 2 * t, t, color);
        Slab(parent, x - t, z, t, d, color);
        Slab(parent, x + w, z, t, d, color);
    }

    /// <summary>The area's outline, and the ends: the entrance green, the exits pink.</summary>
    void DrawMarks()
    {
        foreach (var child in _marks.GetChildren())
            child.QueueFree();
        Outline(_marks, N, N, _w, _h, new Color(1, 1, 1, 0.6f));
        var ends = UnchartedRoutes.Ends(Current);
        for (var i = 0; i < ends.Count; i++)
        {
            var e = ends[i];
            Slab(_marks, N + e.X, N + e.Y, e.Vertical ? 1 : e.Along, e.Vertical ? e.Along : 1, i == 0 ? new Color(0.3f, 1, 0.4f, 0.6f) : new Color(1, 0.35f, 0.8f, 0.6f));
        }
    }

    Texture2D? Sprite(int id)
    {
        if (!_sprites.TryGetValue(id, out var texture))
        {
            var path = _assets.NpcSpritePath(id);
            _sprites[id] = texture = File.Exists(path) && Image.LoadFromFile(path) is { } image ? ImageTexture.CreateFromImage(image) : null;
        }
        return texture;
    }

    /// <summary>People, obstacles and stamp outlines.</summary>
    void DrawThings()
    {
        foreach (var child in _things.GetChildren())
            child.QueueFree();
        void Figure(int x, int y, int sprite, Color fallback)
        {
            if (Sprite(sprite) is { } sheet)
            {
                var frame = sheet.GetWidth() / CharacterSheet.Columns;
                _things.AddChild(new Sprite3D
                {
                    Texture = sheet, RegionEnabled = true, RegionRect = new Rect2(0, 0, frame, frame), PixelSize = 2f / frame,
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                    NoDepthTest = true, Position = new Vector3(N + x + 0.5f, BaseHeight + 1, N + y + 0.5f),
                });
            }
            else
                Slab(_things, N + x, N + y, 1, 1, fallback, 0.2f);
        }
        foreach (var stamp in _stamps)
            Outline(_things, N + stamp.X, N + stamp.Y, stamp.W, stamp.H, new Color(0.3f, 0.9f, 1, 0.7f), 0.08f);
        foreach (var o in _obstacles)
            Figure(o.X, o.Y, o.Kind == "strength" ? UnchartedRoutes.StrengthSprite : UnchartedRoutes.CutSprite, new Color(0.6f, 0.4f, 0.2f, 0.8f));
        for (var i = 0; i < _npcs.Count; i++)
        {
            Figure(_npcs[i].X, _npcs[i].Y, _npcs[i].Sprite, new Color(1, 0.6f, 0.2f, 0.8f));
            if (i == _selectedNpc)
                Outline(_things, N + _npcs[i].X, N + _npcs[i].Y, 1, 1, new Color(1, 0.9f, 0.2f, 0.9f), 0.1f);
        }
        if (_pickFrom is { } from && _tool == Tool.PickStamp)
            Slab(_things, SourceX + from.X, from.Y, 1, 1, new Color(1, 0.9f, 0.2f, 0.7f), 0.2f);
    }

    void UpdateStatus()
    {
        var file = Current;
        var ends = UnchartedRoutes.Ends(file);
        var lines = new List<string>();
        if (ends.Count == 0)
            lines.Add("No entrance yet: bring walkable tiles to the edge of the area (the white outline).");
        else
        {
            lines.Add($"Entrance (green) and {ends.Count - 1} exit{(ends.Count == 2 ? "" : "s")} (pink).");
            if (ends.Count == 1)
                lines.Add("No exit: bring the path to another part of the edge.");
            // Every exit should be reachable on foot from the entrance.
            if (_route is { } route)
            {
                int w = route.WidthTiles;
                var seen = new bool[route.Flags.Length];
                var queue = new Queue<int>([route.EntranceY * w + route.EntranceX]);
                seen[queue.Peek()] = true;
                while (queue.TryDequeue(out var i))
                    foreach (var j in new[] { i + 1, i - 1, i + w, i - w })
                        if (j >= 0 && j < seen.Length && !seen[j] && (route.Flags[j] & 5) == 0 && Math.Abs(j % w - i % w) <= 1)
                        {
                            seen[j] = true;
                            queue.Enqueue(j);
                        }
                var cut = route.Zone.Warps.Skip(1).Count(warp => !seen[warp.Y * w + warp.X]);
                if (cut > 0)
                    lines.Add($"{cut} exit{(cut == 1 ? "" : "s")} can't be reached from the entrance.");
                if (!route.Flags.Any(f => (f & 3) == 2))
                    lines.Add("No tall grass: no wild Pokémon here.");
            }
        }
        _status.Text = string.Join("\n", lines) + (_dirty ? "\nNot saved." : "");
        FillEnds(ends);
    }

    TerrainTheme Source => _assets.SourceTheme(_sourceArea) ?? Theme;

    void UpdateCamera()
    {
        Vector3 target;
        if (_tool == Tool.PickStamp)
        {
            var theme = Source;
            _sourceFocus = new Vector2(Mathf.Clamp(_sourceFocus.X, 0, theme.Width), Mathf.Clamp(_sourceFocus.Y, 0, theme.Height));
            target = new Vector3(SourceX + _sourceFocus.X, BaseHeight, _sourceFocus.Y);
        }
        else
        {
            _focus = new Vector2(Mathf.Clamp(_focus.X, 0, FullW), Mathf.Clamp(_focus.Y, 0, FullH));
            target = new Vector3(_focus.X, BaseHeight, _focus.Y);
        }
        _camera.Position = target + new Vector3(0, 17, 17) * _zoom;
        _camera.LookAt(target, Vector3.Up);
    }

    // ---------------------------------------------------------------- stamps: the look's real maps

    void ClearSource()
    {
        foreach (var child in _source.GetChildren())
            child.QueueFree();
        _sourceLoaded = false;
    }

    /// <summary>Lays out the look's real cells (as the game draws them) to pick stamps from.</summary>
    void LoadSource()
    {
        if (_sourceLoaded)
            return;
        _sourceLoaded = true;
        var theme = Source;
        foreach (var cell in theme.Cells)
        {
            var doc = new GltfDocument();
            var state = new GltfState();
            if (doc.AppendFromFile(_assets.ChunkModelPath(cell, _season), state) != Error.Ok || doc.GenerateScene(state) is not Node3D scene)
                continue;
            scene.Position = new Vector3(SourceX + cell.X * N - theme.OriginX + N / 2f, 0, cell.Y * N - theme.OriginY + N / 2f);
            PixelArt(scene);
            _source.AddChild(scene);
        }
        _sourceFocus = new Vector2(theme.Width / 2f, theme.Height / 2f);
    }

    // ---------------------------------------------------------------- tools and input

    void SetTool(Tool tool)
    {
        _tool = tool;
        _pickFrom = null;
        for (var i = 0; i < Brushes.Length; i++)
            _brushButtons[i].SetPressedNoSignal(tool == Tool.Paint && Brushes[i].Kind == _brush);
        if (tool == Tool.PickStamp)
            LoadSource();
        _hint.Text = tool switch
        {
            Tool.Paint => "Left drag: paint · Right drag: forest · 1–5: brush",
            Tool.PickStamp => "Unova's real maps (Objects tab: \"Maps of\"). Drag a rectangle over what you want to copy; it then goes back to your route to place it. Esc: cancel.",
            Tool.Stamp => _clipboard is null ? "Pick a piece of the real maps first." : "Click to place the stamp · Right-click a stamp to remove it",
            Tool.Person => "Click a tile to add a person, or a person to select · Right-click removes",
            Tool.Obstacle => $"Click to place a {(_obstacleKind == "cut" ? "small tree" : "boulder")} · Right-click removes",
            Tool.Entrance => "Click the opening on the edge that should be the entrance.",
            _ => "",
        } + "\nMiddle drag or WASD: move · Wheel: zoom · Ctrl+Z: undo · Ctrl+S: save · Esc: back to the game";
        _stampLabel.Text = _clipboard is { } c
            ? c.Label is "piece" or "objects" ? $"Picked a {c.W}×{c.H} {(c.ObjectsOnly ? "piece (its objects only)" : "piece")}." : $"Picked: {c.Label} ({c.W}×{c.H})."
            : "Nothing picked yet.";
        if (_camera is not null && _kinds.Length > 0)
            UpdateCamera();
    }

    /// <summary>The tile under a point of the view: of the area (may be outside it), or of the real maps while picking.</summary>
    Vector2I TileUnder(Vector2 position)
    {
        Vector3 origin = _camera.ProjectRayOrigin(position), dir = _camera.ProjectRayNormal(position);
        if (Mathf.Abs(dir.Y) < 1e-4f)
            return new Vector2I(-9999, -9999);
        var hit = origin + dir * ((BaseHeight - origin.Y) / dir.Y);
        return _tool == Tool.PickStamp
            ? new Vector2I(Mathf.FloorToInt(hit.X) - SourceX, Mathf.FloorToInt(hit.Z))
            : new Vector2I(Mathf.FloorToInt(hit.X) - N, Mathf.FloorToInt(hit.Z) - N);
    }

    void OnViewInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                _zoom = Mathf.Clamp(_zoom / 1.12f, 0.6f, 8);
                UpdateCamera();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                _zoom = Mathf.Clamp(_zoom * 1.12f, 0.6f, 8);
                UpdateCamera();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } middle:
                _panning = middle.Pressed;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right } button:
                var right = button.ButtonIndex == MouseButton.Right;
                var tile = TileUnder(button.Position);
                if (!button.Pressed)
                {
                    if (_tool == Tool.PickStamp && _pickFrom is { } from && !right)
                        FinishPick(from, tile);
                    if (_painting)
                        Redraw();
                    _painting = false;
                    break;
                }
                Click(tile, right);
                break;
            case InputEventMouseMotion motion:
                if (_panning)
                {
                    if (_tool == Tool.PickStamp)
                        _sourceFocus -= motion.Relative * 0.05f * _zoom;
                    else
                        _focus -= motion.Relative * 0.05f * _zoom;
                    UpdateCamera();
                }
                var under = TileUnder(motion.Position);
                MoveCursor(under);
                if (_painting)
                    Stroke(under, (motion.ButtonMask & MouseButtonMask.Right) != 0 ? TileKind.Tree : _brush);
                break;
        }
    }

    void MoveCursor(Vector2I tile)
    {
        _cursor.Visible = true;
        float x = tile.X, y = tile.Y, w = 1, h = 1;
        switch (_tool)
        {
            case Tool.Paint:
                (x, y, w, h) = (Mathf.FloorToInt(tile.X / 2f) * 2, Mathf.FloorToInt(tile.Y / 2f) * 2, _brushBlocks * 2, _brushBlocks * 2);
                break;
            case Tool.Stamp when _clipboard is { } c:
                (w, h) = (c.W, c.H);
                break;
            case Tool.PickStamp when _pickFrom is { } from:
                (x, y, w, h) = (Math.Min(from.X, tile.X), Math.Min(from.Y, tile.Y), Math.Abs(tile.X - from.X) + 1, Math.Abs(tile.Y - from.Y) + 1);
                break;
        }
        _cursor.Scale = new Vector3(w, 1, h);
        _cursor.Position = new Vector3((_tool == Tool.PickStamp ? SourceX : N) + x + w / 2f, BaseHeight + 0.15f, (_tool == Tool.PickStamp ? 0 : N) + y + h / 2f);
    }

    void Click(Vector2I tile, bool right)
    {
        switch (_tool)
        {
            case Tool.Paint:
                _painting = true;
                _strokeSaved = false;
                _lastPainted = new Vector2I(-9999, -9999);
                Stroke(tile, right ? TileKind.Tree : _brush);
                break;
            case Tool.PickStamp when !right:
                _pickFrom = tile;
                break;
            case Tool.Stamp or Tool.Obstacle when right:
                RemoveObjectAt(tile);
                break;
            case Tool.Stamp when _clipboard is { } c:
                Remember();
                _stamps.Add(new RouteStamp(tile.X, tile.Y, c.W, c.H, c.X, c.Y, c.Area, c.ObjectsOnly));
                MarkStale(tile.X, tile.Y, c.W, c.H);
                Redraw();
                break;
            case Tool.Obstacle when InArea(tile.X, tile.Y):
                Remember();
                _obstacles.RemoveAll(o => (o.X, o.Y) == (tile.X, tile.Y));
                _obstacles.Add(new RouteObstacle(tile.X, tile.Y, _obstacleKind));
                Redraw();
                break;
            case Tool.Person:
                var at = _npcs.FindIndex(n => (n.X, n.Y) == (tile.X, tile.Y));
                if (right)
                {
                    if (at >= 0)
                    {
                        Remember();
                        _npcs.RemoveAt(at);
                        SelectNpc(-1);
                    }
                }
                else if (at >= 0)
                    SelectNpc(at);
                else if (InArea(tile.X, tile.Y))
                {
                    Remember();
                    var like = _selectedNpc >= 0 && _selectedNpc < _npcs.Count ? _npcs[_selectedNpc] : new RouteNpc(0, 0, FirstSprite());
                    _npcs.Add(like with { X = tile.X, Y = tile.Y, Text = "", Trainer = -1, GiftItem = 0, GiftCount = 0 });
                    SelectNpc(_npcs.Count - 1);
                }
                DrawThings();
                break;
            case Tool.Entrance when !right:
                var ends = UnchartedRoutes.Ends(Current with { EntranceX = -1, EntranceY = -1 });
                if (ends.Count > 0)
                {
                    Remember();
                    var nearest = ends.MinBy(end => Math.Abs(end.X + (end.Vertical ? 0 : end.Along / 2) - tile.X) + Math.Abs(end.Y + (end.Vertical ? end.Along / 2 : 0) - tile.Y));
                    (_entranceX, _entranceY) = (nearest.X, nearest.Y);
                }
                SetTool(Tool.Paint);
                Redraw();
                break;
        }
    }

    int FirstSprite() => Enumerable.Range(0, 400).FirstOrDefault(id => File.Exists(_assets.NpcSpritePath(id)));

    void RemoveObjectAt(Vector2I tile)
    {
        var obstacle = _obstacles.FindIndex(o => (o.X, o.Y) == (tile.X, tile.Y));
        var stamp = _stamps.FindLastIndex(s => tile.X >= s.X && tile.Y >= s.Y && tile.X < s.X + s.W && tile.Y < s.Y + s.H);
        if (obstacle < 0 && stamp < 0)
            return;
        Remember();
        if (obstacle >= 0)
            _obstacles.RemoveAt(obstacle);
        else
        {
            var s = _stamps[stamp];
            _stamps.RemoveAt(stamp);
            MarkStale(s.X, s.Y, s.W, s.H);
        }
        Redraw();
    }

    /// <summary>A rectangle of the real maps was dragged out: it becomes the stamp to place.</summary>
    void FinishPick(Vector2I from, Vector2I to)
    {
        var theme = Source;
        int x = Math.Clamp(Math.Min(from.X, to.X), 0, theme.Width - 1), y = Math.Clamp(Math.Min(from.Y, to.Y), 0, theme.Height - 1);
        int w = Math.Min(Math.Min(64, theme.Width - x), Math.Abs(to.X - from.X) + 1), h = Math.Min(Math.Min(64, theme.Height - y), Math.Abs(to.Y - from.Y) + 1);
        _clipboard = (_sourceArea, x, y, w, h, _pickObjectsOnly, _pickObjectsOnly ? "objects" : "piece");
        SetTool(Tool.Stamp);
    }

    void Stroke(Vector2I tile, TileKind kind)
    {
        var block = new Vector2I(Mathf.FloorToInt(tile.X / 2f), Mathf.FloorToInt(tile.Y / 2f));
        if (block == _lastPainted || tile.X < -N || tile.Y < -N || tile.X >= _w + N || tile.Y >= _h + N)
            return;
        _lastPainted = block;
        var before = _strokeSaved ? null : Current;
        if (!Paint(tile.X, tile.Y, kind, _brushBlocks))
            return;
        if (before is not null)
        {
            // One undo step per stroke.
            _undo.Add(before);
            if (_undo.Count > MaxUndo)
                _undo.RemoveAt(0);
            _strokeSaved = true;
        }
        if (_redrawIn < 0)
            _redrawIn = 0.08;
    }

    public override void _Process(double delta)
    {
        if (_assets is null || _camera is null)
            return;
        if (_redrawIn >= 0 && (_redrawIn -= delta) < 0)
            Redraw();
        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit)
            return;
        var move = Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");
        if (Input.IsKeyPressed(Key.A)) move.X -= 1;
        if (Input.IsKeyPressed(Key.D)) move.X += 1;
        if (Input.IsKeyPressed(Key.W)) move.Y -= 1;
        if (Input.IsKeyPressed(Key.S) && !Input.IsKeyPressed(Key.Ctrl)) move.Y += 1;
        if (move == Vector2.Zero)
            return;
        if (_tool == Tool.PickStamp)
            _sourceFocus += move * (float)delta * 22 * _zoom;
        else
            _focus += move * (float)delta * 22 * _zoom;
        UpdateCamera();
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        if (key.Keycode is Key.Escape or Key.F10)
        {
            GetViewport().SetInputAsHandled();
            if (_tool == Tool.PickStamp && key.Keycode == Key.Escape)
                SetTool(Tool.Stamp);
            else
                close();
            return;
        }
        if (_assets is null)
            return;
        if (key.CtrlPressed && key.Keycode == Key.Z)
            Undo();
        else if (key.CtrlPressed && key.Keycode == Key.S)
            Save();
        else if (key.Keycode is >= Key.Key1 and <= Key.Key5)
        {
            _brush = Brushes[(int)(key.Keycode - Key.Key1)].Kind;
            SetTool(Tool.Paint);
        }
        else
            return;
        GetViewport().SetInputAsHandled();
    }

    // ---------------------------------------------------------------- people

    void SelectNpc(int index)
    {
        _selectedNpc = index >= 0 && index < _npcs.Count ? index : -1;
        _npcFields.Visible = _selectedNpc >= 0;
        if (_selectedNpc < 0)
            return;
        var npc = _npcs[_selectedNpc];
        _fillingFields = true;
        _npcSprite.Value = npc.Sprite;
        _npcFacing.Select(Math.Clamp(npc.Facing, 0, 3));
        _npcMovement.Select(Math.Max(0, _npcMovement.GetItemIndex(npc.Movement)));
        _npcKind.Select(npc.Battle is not null ? 1 : npc.GiftItem > 0 ? 2 : 0);
        _npcText.Text = npc.Text;
        _npcSight.Value = npc.Sight;
        _npcGift.Value = Math.Max(1, npc.GiftItem);
        _npcGiftCount.Value = Math.Max(1, npc.GiftCount);
        var battle = npc.Battle;
        _trainerNameBox.Text = battle?.Name ?? "";
        _trainerClass.Select(Math.Max(0, _trainerClass.GetItemIndex(battle?.Class ?? (_classes.Count > 0 ? _classes[0].Class : 0))));
        _trainerPrize.Value = battle?.Prize ?? 0;
        _trainerDefeat.Text = battle?.Defeat ?? "";
        _trainerAfter.Text = battle?.After ?? "";
        _fillingFields = false;
        ShowNpcExtras(npc);
        FillParty();
    }

    void ShowNpcExtras(RouteNpc npc)
    {
        if (Sprite(npc.Sprite) is { } sheet)
        {
            var frame = sheet.GetWidth() / CharacterSheet.Columns;
            _npcPreview.Texture = new AtlasTexture { Atlas = sheet, Region = new Rect2(0, 0, frame, frame) };
        }
        else
            _npcPreview.Texture = null;
        var kind = npc.Battle is not null ? 1 : npc.GiftItem > 0 ? 2 : 0;
        _trainerFields.Visible = kind == 1;
        _giftFields.Visible = kind == 2;
        _trainerName.Text = npc.Battle is { } b ? $"Shown as \"{_trainerClass.GetItemText(_trainerClass.Selected)} {b.Name}\"." : "";
        _giftName.Text = npc.GiftItem > 0 ? OfflineSession.Data?.ItemName(npc.GiftItem) ?? $"Item {npc.GiftItem}" : "";
    }

    /// <summary>A field of the selected person changed.</summary>
    void NpcEdited()
    {
        if (_fillingFields || _selectedNpc < 0 || _selectedNpc >= _npcs.Count)
            return;
        var old = _npcs[_selectedNpc];
        var kind = (int)_npcKind.GetItemId(_npcKind.Selected);
        var battle = kind == 1
            ? (old.Battle ?? new RouteTrainer("", 0, [new RouteTrainerMon(504, 5)])) with
            {
                Name = _trainerNameBox.Text.Trim(), Class = _classes.Count > 0 ? (int)_trainerClass.GetItemId(_trainerClass.Selected) : 0,
                Prize = (int)_trainerPrize.Value, Defeat = _trainerDefeat.Text, After = _trainerAfter.Text,
            }
            : null;
        var npc = old with
        {
            Sprite = (int)_npcSprite.Value, Facing = _npcFacing.Selected, Movement = (int)_npcMovement.GetItemId(_npcMovement.Selected),
            Text = _npcText.Text, Sight = kind == 1 ? (int)_npcSight.Value : 0, Trainer = -1,
            GiftItem = kind == 2 ? (int)_npcGift.Value : 0, GiftCount = kind == 2 ? (int)_npcGiftCount.Value : 0, Battle = battle,
        };
        if (npc == old)
            return;
        _npcs[_selectedNpc] = npc;
        _dirty = true;
        ShowNpcExtras(npc);
        if ((old.Battle is null) != (battle is null))
            FillParty();
        if (npc.Sprite != old.Sprite)
            DrawThings();
    }

    /// <summary>The selected trainer's Pokémon, a row each.</summary>
    void FillParty()
    {
        Ui.Ui.ClearChildren(_partyRows);
        if (_selectedNpc < 0 || _selectedNpc >= _npcs.Count || _npcs[_selectedNpc].Battle is not { } battle)
            return;
        void SetParty(List<RouteTrainerMon> party)
        {
            var npc = _npcs[_selectedNpc];
            _npcs[_selectedNpc] = npc with { Battle = npc.Battle! with { Party = party } };
            _dirty = true;
        }
        for (var i = 0; i < battle.Party.Count; i++)
        {
            var index = i;
            var mon = battle.Party[i];
            var name = Ui.Ui.Text(SpeciesName(mon.Species), 12, Ui.Ui.Muted);
            var species = new SpinBox { MinValue = 1, MaxValue = 649, Step = 1, Value = mon.Species, CustomMinimumSize = new Vector2(70, 0) };
            var level = new SpinBox { MinValue = 1, MaxValue = 100, Step = 1, Value = mon.Level, CustomMinimumSize = new Vector2(64, 0) };
            void Changed()
            {
                var party = _npcs[_selectedNpc].Battle!.Party.ToList();
                party[index] = new RouteTrainerMon((int)species.Value, (int)level.Value);
                SetParty(party);
                name.Text = SpeciesName((int)species.Value);
            }
            species.ValueChanged += _ => Changed();
            level.ValueChanged += _ => Changed();
            _partyRows.AddChild(Ui.Ui.HBox(4, Ui.Ui.Text("#", 12, Ui.Ui.Muted), species, name, Ui.Ui.Spacer(), Ui.Ui.Text("Lv", 12, Ui.Ui.Muted), level,
                Ui.Ui.Button("✕", () =>
                {
                    var party = _npcs[_selectedNpc].Battle!.Party.ToList();
                    if (party.Count <= 1)
                        return;
                    Remember();
                    party.RemoveAt(index);
                    SetParty(party);
                    FillParty();
                }, 12)));
        }
        if (battle.Party.Count < 6)
            _partyRows.AddChild(Ui.Ui.Button("Add a Pokémon", () =>
            {
                Remember();
                var party = _npcs[_selectedNpc].Battle!.Party.ToList();
                party.Add(party[^1]);
                SetParty(party);
                FillParty();
            }));
    }

    // ---------------------------------------------------------------- wild Pokémon

    void FillWild()
    {
        _fillingFields = true;
        _wildSource.Select(Math.Max(0, _wildSource.GetItemIndex(_customWild ? -2 : _encounterZone >= 0 ? _encounterZone : -1)));
        _wildRateBox.Value = _wildRate;
        _wildRateBox.Editable = _customWild;
        _fillingFields = false;
        Ui.Ui.ClearChildren(_wildRows);
        if (!_customWild)
        {
            _wildRows.AddChild(Ui.Ui.Text("Uses that route's wild Pokémon and levels.", 12, Ui.Ui.Muted, wrap: true));
            return;
        }
        for (var i = 0; i < _wild.Count; i++)
        {
            var index = i;
            var slot = _wild[i];
            var name = Ui.Ui.Text(SpeciesName(slot.Species), 12, Ui.Ui.Muted);
            SpinBox Box(int min, int max, int value, Func<RouteWild, int, RouteWild> set)
            {
                var box = new SpinBox { MinValue = min, MaxValue = max, Step = 1, Value = value, CustomMinimumSize = new Vector2(64, 0) };
                box.ValueChanged += v =>
                {
                    _wild[index] = set(_wild[index], (int)v);
                    name.Text = SpeciesName(_wild[index].Species);
                    _dirty = true;
                };
                return box;
            }
            _wildRows.AddChild(Ui.Ui.VBox(2,
                Ui.Ui.HBox(4, Ui.Ui.Text("#", 12, Ui.Ui.Muted), Box(1, 649, slot.Species, (s, v) => s with { Species = v }), name),
                Ui.Ui.HBox(4, Ui.Ui.Text("Lv", 12, Ui.Ui.Muted), Box(1, 100, slot.MinLevel, (s, v) => s with { MinLevel = v, MaxLevel = Math.Max(v, s.MaxLevel) }),
                    Ui.Ui.Text("–", 12, Ui.Ui.Muted), Box(1, 100, slot.MaxLevel, (s, v) => s with { MaxLevel = Math.Max(v, s.MinLevel) }),
                    Ui.Ui.Text("weight", 12, Ui.Ui.Muted), Box(1, 100, slot.Weight, (s, v) => s with { Weight = v }),
                    Ui.Ui.Button("✕", () => { Remember(); _wild.RemoveAt(index); FillWild(); }, 12))));
        }
        if (_wild.Count < 12)
            _wildRows.AddChild(Ui.Ui.Button("Add a Pokémon", () =>
            {
                Remember();
                _wild.Add(_wild.Count > 0 ? _wild[^1] : new RouteWild(504, 3, 5));
                FillWild();
            }));
    }

    string SpeciesName(int id) => _species.FirstOrDefault(s => s.Id == id)?.Name ?? "?";

    // ---------------------------------------------------------------- ends

    string _endsShown = "";

    /// <summary>A row per end: where it leads (the default, a route, or a real place).</summary>
    void FillEnds(List<(int X, int Y, int Along, bool Vertical, Facing Inward)> ends)
    {
        // Links whose end is gone go too.
        _links.RemoveAll(l => !ends.Any(e => Holds(e, l.X, l.Y)));
        var signature = string.Join(";", ends.Select(e => $"{e.X},{e.Y},{e.Along},{e.Vertical}")) + "|" + string.Join(";", _links) + "|" + _node;
        if (signature == _endsShown)
            return;
        _endsShown = signature;
        Ui.Ui.ClearChildren(_endRows);
        for (var i = 0; i < ends.Count; i++)
        {
            var end = ends[i];
            var link = _links.FirstOrDefault(l => Holds(end, l.X, l.Y));
            var side = end.Inward switch { Facing.Right => "west", Facing.Left => "east", Facing.Down => "north", _ => "south" };
            var usual = i == 0
                ? _node == 0 ? "Nuvema Town" : $"route {(_node - 1) / 3}"
                : $"route {UnchartedRoutes.Child(_node, i - 1)}";
            var kind = new OptionButton();
            kind.AddItem($"Default ({usual})", 0);
            kind.AddItem("A route", 1);
            kind.AddItem("A real place", 2);
            var isRoute = link is not null && UnchartedRoutes.IsRoute(link.Zone);
            kind.Select(link is null ? 0 : isRoute ? 1 : 2);
            var target = new SpinBox { MinValue = 0, MaxValue = 99999, Step = 1, Value = link is null ? 0 : isRoute ? link.Zone - UnchartedRoutes.ZoneBase : link.Zone, CustomMinimumSize = new Vector2(90, 0) };
            var warp = new SpinBox { MinValue = 0, MaxValue = 99, Step = 1, Value = link?.Warp ?? 0, CustomMinimumSize = new Vector2(64, 0) };
            var where = Ui.Ui.Text("", 12, Ui.Ui.Muted, wrap: true);
            var fields = Ui.Ui.HBox(4, Ui.Ui.Text(link is null || isRoute ? "Route" : "Zone", 12, Ui.Ui.Muted), target, Ui.Ui.Text(isRoute ? "end" : "warp", 12, Ui.Ui.Muted), warp);
            fields.Visible = link is not null;
            where.Text = link is null ? "" : isRoute
                ? link.Warp == 0 ? "Arrives at that route's entrance." : $"Arrives at that route's exit {link.Warp}."
                : "A real zone number and one of its warps (see /who or the HUD's tile for where you are).";
            void Apply()
            {
                Remember();
                _links.RemoveAll(l => Holds(end, l.X, l.Y));
                var choice = kind.GetSelectedId();
                if (choice != 0)
                    _links.Add(new RouteLink(end.X, end.Y, choice == 1 ? UnchartedRoutes.ZoneOf((int)target.Value) : (int)target.Value, (int)warp.Value));
                Redraw();
            }
            kind.ItemSelected += _ => Apply();
            target.ValueChanged += _ => Apply();
            warp.ValueChanged += _ => Apply();
            _endRows.AddChild(Ui.Ui.VBox(3, Ui.Ui.Text(i == 0 ? $"Entrance ({side} edge)" : $"Exit {i} ({side} edge)", 14, i == 0 ? Ui.Ui.Good : Ui.Ui.Accent), kind, fields, where));
        }
    }

    static bool Holds((int X, int Y, int Along, bool Vertical, Facing Inward) e, int x, int y) =>
        e.Vertical ? x == e.X && y >= e.Y && y < e.Y + e.Along : y == e.Y && x >= e.X && x < e.X + e.Along;

    // ---------------------------------------------------------------- for smoke tests

    public string StatusText => _status.Text;
    public int RouteNumber => _node;
    public (int Width, int Height) AreaSize => (_w, _h);
    public RouteFile FileForTest => Current;

    /// <summary>Paints and redraws at once; for smoke tests.</summary>
    public void PaintForTest(int x, int y, TileKind kind, int blocks = 1)
    {
        Remember();
        Paint(x, y, kind, blocks);
        Redraw();
    }

    /// <summary>Adds parts straight to the route; for smoke tests.</summary>
    public void AddForTest(RouteNpc? npc = null, RouteObstacle? obstacle = null, RouteStamp? stamp = null, RouteLink? link = null, RouteWild? wild = null)
    {
        Remember();
        if (npc is not null)
            _npcs.Add(npc);
        if (obstacle is not null)
            _obstacles.Add(obstacle);
        if (stamp is not null)
        {
            _stamps.Add(stamp);
            MarkStale(stamp.X, stamp.Y, stamp.W, stamp.H);
        }
        if (link is not null)
            _links.Add(link);
        if (wild is not null)
        {
            _customWild = true;
            _wild.Add(wild);
            FillWild();
        }
        Redraw();
    }

    public void LookAtForTest(int x, int y, float zoom)
    {
        _focus = new Vector2(N + x, N + y);
        _zoom = zoom;
        UpdateCamera();
    }

    /// <summary>Shows a set's real maps (the stamp picker); for smoke tests.</summary>
    public void ShowSourceForTest(int area = -1)
    {
        if (area >= 0)
        {
            _sourceArea = area;
            ClearSource();
        }
        SetTool(Tool.PickStamp);
    }

    /// <summary>The objects found on a set's maps; for smoke tests.</summary>
    public IReadOnlyList<RouteObject> ObjectsOf(int area) => _assets.SourceTheme(area)?.Objects ?? [];

    /// <summary>Selects person <paramref name="index"/> (the panel shows them); for smoke tests.</summary>
    public void SelectForTest(int index) => SelectNpc(index);
}
