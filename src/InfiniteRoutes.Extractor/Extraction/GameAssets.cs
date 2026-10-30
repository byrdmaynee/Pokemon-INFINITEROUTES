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
    }

    public static GameAssets? Open(string directory) =>
        ExtractionCache.ReadStamp(directory) is { } stamp ? new GameAssets(directory, stamp) : null;

    public IReadOnlyList<SpeciesInfo> LoadSpecies() =>
        JsonSerializer.Deserialize<List<SpeciesInfo>>(File.ReadAllText(Path.Combine(Directory, "data", "species.json")), Json.Indented)
        ?? [];

    public IReadOnlyList<string> LoadText(string table) =>
        JsonSerializer.Deserialize<List<string>>(File.ReadAllText(Path.Combine(Directory, "text", table + ".json")), Json.Indented)
        ?? [];

    public WorldIndex? LoadOverworld()
    {
        var path = Path.Combine(Directory, "maps", "overworld.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<WorldIndex>(File.ReadAllText(path), Json.Indented) : null;
    }

    public string ChunkModelPath(int chunk) => Path.Combine(Directory, "maps", "chunks", $"{chunk:D3}.gltf");

    public byte[] ChunkTiles(int chunk) => File.ReadAllBytes(Path.Combine(Directory, "maps", "chunks", $"{chunk:D3}.tiles"));

    /// <summary>A <see cref="CharacterSheet"/> PNG; <paramref name="name"/> is "hero" or "heroine".</summary>
    public string OverworldSpritePath(string name) => Path.Combine(Directory, "sprites", "overworld", name + ".png");

    /// <param name="view">front, back, front-shiny or back-shiny.</param>
    public string BattleSpritePath(int speciesId, string view) =>
        Path.Combine(Directory, "sprites", "battle", view, $"{speciesId:D3}.png");
}
