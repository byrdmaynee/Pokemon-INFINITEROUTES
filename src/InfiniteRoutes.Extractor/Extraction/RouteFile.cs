using System.Text.Json;

namespace InfiniteRoutes.Extractor.Extraction;

/// <summary>
/// Where an end of a hand-made route leads, instead of the default (the entrance back to the route it's reached from,
/// exit k on to route 3n+1+k): the end holding tile (<see cref="X"/>, <see cref="Y"/>) leads to warp
/// <see cref="Warp"/> of <see cref="Zone"/> — a real zone, or route n as zone 1,000,000 + n (warp 0 is its entrance,
/// 1… its exits).
/// </summary>
public sealed record RouteLink(int X, int Y, int Zone, int Warp = 0);

/// <summary>
/// A person on a hand-made route: an overworld sprite, the way they face (0 down, 1 up, 2 left, 3 right), how they
/// move (0 still, 2 looks around, 3 wanders) and what they say (pages split by blank lines). Then one of: talk only;
/// a trainer made for the route (<see cref="Battle"/>; they spot you from <see cref="Sight"/> tiles away, and what they
/// say is said before the battle); or an item they give on the first chat (<see cref="GiftItem"/> ×
/// <see cref="GiftCount"/>). (<see cref="Trainer"/>, a trainer of the ROM, is still read but the editor no longer sets it.)
/// </summary>
public sealed record RouteNpc(int X, int Y, int Sprite, int Facing = 0, int Movement = 0, string Text = "", int Trainer = -1,
    int Sight = 0, int GiftItem = 0, int GiftCount = 0, RouteTrainer? Battle = null);

/// <summary>
/// A trainer made for a hand-made route: a name, a trainer class of the ROM (its title, like "Youngster", and its
/// battle sprite), up to six Pokémon, the prize money for beating them, and what they say on losing and afterwards.
/// </summary>
public sealed record RouteTrainer(string Name, int Class, IReadOnlyList<RouteTrainerMon> Party, int Prize = 0, string Defeat = "",
    string After = "");

/// <summary>One of a route trainer's Pokémon: a species at a level, with the moves it knows at that level.</summary>
public sealed record RouteTrainerMon(int Species, int Level);

/// <summary>
/// A piece of Unova's real maps copied onto a hand-made route: the <see cref="W"/>×<see cref="H"/> tiles at
/// (<see cref="SourceX"/>, <see cref="SourceY"/>) of texture set <see cref="Area"/>'s maps (all its cells, towns too;
/// -1: the route's own look's routes, as first saved), placed with its top-left at (<see cref="X"/>, <see cref="Y"/>).
/// As it is — look and collision — or, with <see cref="ObjectsOnly"/>, just the placed models on it (signs, rocks,
/// houses, with their shadows) standing on the route's own ground, solid where they stand. How fences, ledges,
/// cliffs, bridges, ponds, flowers and buildings get onto a route. Later stamps cover earlier ones.
/// </summary>
public sealed record RouteStamp(int X, int Y, int W, int H, int SourceX, int SourceY, int Area = -1, bool ObjectsOnly = false);

/// <summary>Something in the way that an HM clears: "cut" (a small tree) or "strength" (a boulder).</summary>
public sealed record RouteObstacle(int X, int Y, string Kind);

/// <summary>A wild Pokémon of a hand-made route's tall grass: a species, a level range and how likely it is next to the others.</summary>
public sealed record RouteWild(int Species, int MinLevel, int MaxLevel, int Weight = 10);

/// <summary>
/// A hand-made Infinite Route as the route editor saves it: which route number it is, its name, the theme it's drawn
/// in (a texture set), and what every tile of its playable area is — one string per row, one letter per tile
/// (<see cref="Letters"/>). Its ends are wherever walkable tiles reach the edge of the area; the entrance is the one
/// holding (<see cref="EntranceX"/>, <see cref="EntranceY"/>), or the first on the west edge.
/// <para>
/// Everything is placed in the playable area's tile coordinates; the cell of scenery round it is at −32…−1 and past
/// the far edges. <see cref="Outer"/>, when set, is the whole map's tiles (the area and that margin: Height + 64 rows
/// of Width + 64 letters), so the scenery can be painted too; otherwise it's forest, with the ground carrying on a
/// little past each end. Wild Pokémon: <see cref="Wild"/> with <see cref="WildRate"/> (percent per step in tall
/// grass) if set, else the encounter table of real zone <see cref="EncounterZone"/>, else the theme's route's.
/// </para>
/// </summary>
public sealed record RouteFile(int Node, string Name, int Area, int Width, int Height, IReadOnlyList<string> Rows,
    int EntranceX = -1, int EntranceY = -1, IReadOnlyList<string>? Outer = null, IReadOnlyList<RouteLink>? Links = null,
    IReadOnlyList<RouteNpc>? Npcs = null, IReadOnlyList<RouteStamp>? Stamps = null, IReadOnlyList<RouteObstacle>? Obstacles = null,
    IReadOnlyList<RouteWild>? Wild = null, int WildRate = 0, int EncounterZone = -1)
{
    /// <summary>The cell of scenery round the playable area, in tiles.</summary>
    public const int Margin = 32;

    /// <summary>A tile of the whole map (the area at 0…, the margin outside it), or null where <see cref="Outer"/> doesn't say.</summary>
    public TileKind? OuterAt(int x, int y) =>
        Outer is { } rows && y + Margin >= 0 && y + Margin < rows.Count && x + Margin >= 0 && x + Margin < rows[y + Margin].Length
            ? Kind(rows[y + Margin][x + Margin]) : null;

    /// <summary>T forest, . open ground, p path, g tall grass, w water.</summary>
    public const string Letters = "?.pgTw";

    public static char Letter(TileKind kind) => Letters[(int)kind];

    public static TileKind Kind(char letter) => Letters.IndexOf(letter) is var i and > 0 ? (TileKind)i : TileKind.Tree;

    public TileKind At(int x, int y) =>
        y >= 0 && y < Rows.Count && x >= 0 && x < Rows[y].Length ? Kind(Rows[y][x]) : TileKind.Tree;

    public TileKind[] Kinds()
    {
        var kinds = new TileKind[Width * Height];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                kinds[y * Width + x] = At(x, y);
        return kinds;
    }

    public static RouteFile From(int node, string name, int area, int width, int height, TileKind[] kinds, int entranceX = -1, int entranceY = -1) =>
        new(node, name, area, width, height,
            Enumerable.Range(0, height).Select(y => new string(Enumerable.Range(0, width).Select(x => Letter(kinds[y * width + x])).ToArray())).ToList(),
            entranceX, entranceY);
}

/// <summary>Hand-made routes on disk (a server's routes folder): one JSON file per route number.</summary>
public static class RouteFiles
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string ToJson(RouteFile file) => JsonSerializer.Serialize(file, Options);

    /// <summary>A route file from its JSON; null if it isn't one.</summary>
    public static RouteFile? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RouteFile>(json, Options) is { Width: > 0, Height: > 0, Rows: not null, Name: not null } file ? file : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string PathOf(string directory, int node) => Path.Combine(directory, $"route-{node:D4}.json");

    /// <summary>The saved route for <paramref name="node"/>; null if there's none or it can't be read.</summary>
    public static RouteFile? Load(string directory, int node)
    {
        var path = PathOf(directory, node);
        try
        {
            if (!File.Exists(path))
                return null;
            var file = JsonSerializer.Deserialize<RouteFile>(File.ReadAllText(path), Options);
            return file is { Width: > 0, Height: > 0, Rows: not null } ? file with { Node = node } : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string directory, RouteFile file)
    {
        Directory.CreateDirectory(directory);
        var path = PathOf(directory, file.Node);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Options));
        File.Move(temp, path, overwrite: true);
    }

    public static bool Delete(string directory, int node)
    {
        var path = PathOf(directory, node);
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    /// <summary>The saved routes' numbers and names, by number.</summary>
    public static IReadOnlyList<(int Node, string Name)> List(string directory)
    {
        if (!Directory.Exists(directory))
            return [];
        var result = new List<(int, string)>();
        foreach (var path in Directory.EnumerateFiles(directory, "route-*.json"))
            if (int.TryParse(Path.GetFileNameWithoutExtension(path).AsSpan("route-".Length), out var node) && Load(directory, node) is { } file)
                result.Add((node, file.Name));
        return result.OrderBy(r => r.Item1).ToList();
    }
}
