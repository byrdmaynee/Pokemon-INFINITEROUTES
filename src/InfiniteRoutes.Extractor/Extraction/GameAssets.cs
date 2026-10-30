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

    /// <param name="view">front, back, front-shiny or back-shiny.</param>
    public string BattleSpritePath(int speciesId, string view) =>
        Path.Combine(Directory, "sprites", "battle", view, $"{speciesId:D3}.png");
}
