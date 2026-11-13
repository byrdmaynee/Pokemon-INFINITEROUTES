using System.Text.Json;
using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Extractor.Extraction;

/// <summary>Read access to one finished extraction cache.</summary>
public sealed class GameAssets
{
    public string Directory { get; }
    public ExtractionStamp Stamp { get; }

    GameAssets(string directory, ExtractionStamp stamp)
    {
        Directory = directory;
        Stamp = stamp;
        _routeKit = new(() => (TerrainTheme.Areas(this), LoadZones().ToDictionary(z => z.Id), LoadEncounters()));
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<int, GeneratedRoute?> _routes = new();
    readonly Lazy<(IReadOnlyList<int> Areas, Dictionary<int, ZoneInfo> Zones, IReadOnlyList<EncounterTable> Tables)> _routeKit;
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<TerrainTheme?>> _themes = new();

    /// <summary>The folder hand-made routes are read from (a server's; <see cref="RouteFiles"/>), if any.</summary>
    public string? RoutesDirectory { get; set; }

    /// <summary>
    /// Where hand-made routes come from when not a folder: a client playing online is sent them by the server. Takes
    /// the place of <see cref="RoutesDirectory"/> while set.
    /// </summary>
    public Func<int, RouteFile?>? RouteSource { get; set; }

    /// <summary>
    /// Infinite Route zone <paramref name="zone"/> (<see cref="UnchartedRoutes"/>), built on first use: the hand-made
    /// one saved for its number if there is one (and it can be built), else a generated one. Null for any other zone.
    /// </summary>
    public GeneratedRoute? Route(int zone) => UnchartedRoutes.IsRoute(zone) ? _routes.GetOrAdd(zone, BuildRoute) : null;

    GeneratedRoute? BuildRoute(int zone)
    {
        var node = zone - UnchartedRoutes.ZoneBase;
        var kit = _routeKit.Value;
        var saved = RouteSource is { } source ? source(node) : RoutesDirectory is { } directory ? RouteFiles.Load(directory, node) : null;
        if (saved is { } file && Theme(file.Area) is { } look
            && UnchartedRoutes.FromFile(file, look, kit.Zones, kit.Tables, SourceTheme) is { } made)
            return made;
        return UnchartedRoutes.Generate(node, kit.Areas, Theme, kit.Zones, kit.Tables);
    }

    /// <summary>A route built straight from an editor's design, without saving it.</summary>
    public GeneratedRoute? PreviewRoute(RouteFile file) =>
        Theme(file.Area) is { } look ? UnchartedRoutes.FromFile(file, look, _routeKit.Value.Zones, _routeKit.Value.Tables, SourceTheme) : null;

    readonly System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<TerrainTheme?>> _sources = new();

    /// <summary>
    /// All of texture set <paramref name="area"/>'s overworld cells — towns and cities too — to copy pieces and objects
    /// from (<see cref="RouteStamp"/>); built on first use.
    /// </summary>
    public TerrainTheme? SourceTheme(int area) =>
        _sources.GetOrAdd(area, a => new Lazy<TerrainTheme?>(() => TerrainTheme.Build(this, a, everything: true))).Value;

    /// <summary>Every texture set of the overworld, named after its places.</summary>
    public IReadOnlyList<(int Area, string Name)> SourceAreas => _sourceAreas ??= TerrainTheme.AllAreas(this);
    IReadOnlyList<(int Area, string Name)>? _sourceAreas;

    /// <summary>Forgets the built routes, so saved changes are picked up the next time each is asked for.</summary>
    public void ForgetRoutes() => _routes.Clear();

    /// <summary>The Infinite Routes look drawn from texture set <paramref name="area"/>, built (from the models) on first use.</summary>
    public TerrainTheme? Theme(int area) => _themes.GetOrAdd(area, a => new Lazy<TerrainTheme?>(() => TerrainTheme.Build(this, a))).Value;

    /// <summary>The texture sets Infinite Routes can be drawn from.</summary>
    public IReadOnlyList<int> ThemeAreas => _routeKit.Value.Areas;

    public static GameAssets? Open(string directory) =>
        ExtractionCache.ReadStamp(directory) is { } stamp ? new GameAssets(directory, stamp) : null;

    public IReadOnlyList<SpeciesInfo> LoadSpecies() =>
        JsonSerializer.Deserialize<List<SpeciesInfo>>(File.ReadAllText(Path.Combine(Directory, "data", "species.json")), Json.Indented)
        ?? [];

    public IReadOnlyList<string> LoadText(string table) =>
        JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path.Combine(Directory, "text", table + ".json")), Json.Indented)
        ?? [];

    /// <summary>Every move, in id order starting at 1.</summary>
    public IReadOnlyList<MoveInfo> LoadMoves() => LoadData<List<MoveInfo>>("moves") ?? [];

    /// <summary>Level-up learnsets indexed by species id.</summary>
    public IReadOnlyList<IReadOnlyList<LevelMove>> LoadLearnsets() => LoadData<List<List<LevelMove>>>("learnsets") ?? [];

    /// <summary>Wild encounter tables indexed by <see cref="ZoneInfo.Encounters"/>.</summary>
    public IReadOnlyList<EncounterTable> LoadEncounters() => LoadData<List<EncounterTable>>("encounters") ?? [];

    /// <summary>Trainers indexed by id (<see cref="NpcInfo.TrainerId"/>).</summary>
    /// <summary>Every move's battle effect script (index = move id), each command <c>[opcode, args…]</c>.</summary>
    public IReadOnlyList<IReadOnlyList<int[]>> LoadMoveAnimations()
    {
        var path = Path.Combine(Directory, "anim", "moves.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<List<List<int[]>>>(File.ReadAllText(path), Json.Indented) ?? [] : [];
    }

    /// <summary>Particle file <paramref name="index"/> of the move effects, or null if missing.</summary>
    public Nitro3D.SpaFile? LoadParticles(int index)
    {
        var path = Path.Combine(Directory, "anim", "particles", $"{index}.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<Nitro3D.SpaFile>(File.ReadAllText(path), Json.Indented) : null;
    }

    public string ParticleImagePath(string image) => Path.Combine(Directory, "anim", "particles", image);

    /// <summary>A battle scenery model (backdrop "batt_bg01", platform "batt_stage01", …) as glTF, or null if missing.</summary>
    public string? BattleSceneryPath(string name) =>
        Path.Combine(Directory, "battle", name + ".gltf") is var path && File.Exists(path) ? path : null;

    /// <summary>The 80×80 battle sprite of trainer class <paramref name="trainerClass"/>.</summary>
    public string TrainerSpritePath(int trainerClass) => Path.Combine(Directory, "sprites", "trainers", $"{trainerClass:D3}.png");

    public IReadOnlyList<TrainerInfo> LoadTrainers() => LoadData<List<TrainerInfo>>("trainers") ?? [];

    /// <summary>Shop prices in ₽ indexed by item id (0 = can't be bought or sold).</summary>
    public IReadOnlyList<int> LoadItemPrices() => LoadData<List<int>>("item-prices") ?? [];

    T? LoadData<T>(string name)
    {
        var path = Path.Combine(Directory, "data", name + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json.Indented) : default;
    }

    /// <summary>An exported map by key (<see cref="Gen5WorldExtractor.OverworldKey"/> for Unova's overworld); null if missing.</summary>
    public WorldIndex? LoadMap(string key)
    {
        if (UnchartedRoutes.TryParseKey(key, out var node))
            return Route(UnchartedRoutes.ZoneOf(node))?.Map;
        var path = Path.Combine(Directory, "maps", key + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize<WorldIndex>(File.ReadAllText(path), Json.Indented) : null;
    }

    public WorldIndex? LoadOverworld() => LoadMap(Gen5WorldExtractor.OverworldKey);

    public IReadOnlyList<ZoneInfo> LoadZones()
    {
        var path = Path.Combine(Directory, "maps", "zones.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<List<ZoneInfo>>(File.ReadAllText(path), Json.Indented) ?? [] : [];
    }

    public string ChunkModelPath(WorldCell cell, Season season = Season.Spring) =>
        Path.Combine(Directory, "maps", "chunks", Gen5WorldExtractor.ChunkFileName(cell.Chunk, cell.AreaFor((int)season)) + ".gltf");

    /// <summary>A map texture animation (<see cref="WorldCell.Animation"/>); empty if missing.</summary>
    public IReadOnlyList<Nitro3D.SrtAnimation> LoadAnimation(int index)
    {
        var path = Path.Combine(Directory, "maps", "anims", $"{index}.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<List<Nitro3D.SrtAnimation>>(File.ReadAllText(path), Json.Indented) ?? [] : [];
    }

    /// <summary>A map's frame-swap texture animations (<see cref="WorldCell.Pattern"/>); empty if missing.</summary>
    public IReadOnlyList<Gen5.TexturePattern> LoadPatterns(int index)
    {
        var path = Path.Combine(Directory, "maps", "anims", $"pat{index}.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<List<Gen5.TexturePattern>>(File.ReadAllText(path), Json.Indented) ?? [] : [];
    }

    /// <summary>The PNG of one <see cref="Gen5.PatternFrame"/> of pattern file <paramref name="index"/>.</summary>
    public string PatternImagePath(int index, string image) => Path.Combine(Directory, "maps", "anims", $"pat{index}", image);

    public byte[] ChunkTiles(int chunk) => File.ReadAllBytes(Path.Combine(Directory, "maps", "chunks", $"{chunk:D3}.tiles"));

    /// <summary>A map cell's collision: its own grid when it has one (rail maps), else its chunk's.</summary>
    public byte[] CellTiles(WorldCell cell) =>
        UnchartedRoutes.TryParseCellKey(cell.Tiles, out var node, out var cx, out var cy)
        ? Route(UnchartedRoutes.ZoneOf(node))?.CellTiles(cx, cy) ?? Solid
        : cell.Tiles is { } name
        ? File.ReadAllBytes(Path.Combine(Directory, "maps", "chunks", name + ".tiles"))
        : ChunkTiles(cell.Chunk);

    static readonly byte[] Solid = Enumerable.Repeat((byte)1, 32 * 32).ToArray();

    /// <summary>Cache key for <see cref="CellTiles"/>.</summary>
    public static string TilesKey(WorldCell cell) => cell.Tiles ?? cell.Chunk.ToString();

    /// <summary>A <see cref="CharacterSheet"/> PNG; <paramref name="name"/> is "hero" or "heroine".</summary>
    public string OverworldSpritePath(string name) => Path.Combine(Directory, "sprites", "overworld", name + ".png");

    /// <summary>NPC sheet for an overworld sprite id (may not exist: some sprites are 3D models).</summary>
    public string NpcSpritePath(int sprite) => Path.Combine(Directory, "sprites", "overworld", "npc", $"{sprite}.png");

    /// <param name="view">front, back, front-shiny or back-shiny.</param>
    /// <summary>A Pokémon's 32×32 party icon (<see cref="Gen5Icons"/>).</summary>
    public string PartyIconPath(int speciesId) => Path.Combine(Directory, "sprites", "icons", $"{speciesId:D3}.png");

    /// <summary>An item's 32×32 Bag icon (<see cref="Gen5Icons"/>).</summary>
    public string ItemIconPath(int item) => Path.Combine(Directory, "sprites", "items", $"{item}.png");

    public string BattleSpritePath(int speciesId, string view) =>
        Path.Combine(Directory, "sprites", "battle", view, $"{speciesId:D3}.png");
}
