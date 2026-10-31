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

    /// <summary>Every move, in id order starting at 1.</summary>
    public IReadOnlyList<MoveInfo> LoadMoves() => LoadData<List<MoveInfo>>("moves") ?? [];

    /// <summary>Level-up learnsets indexed by species id.</summary>
    public IReadOnlyList<IReadOnlyList<LevelMove>> LoadLearnsets() => LoadData<List<List<LevelMove>>>("learnsets") ?? [];

    /// <summary>Wild encounter tables indexed by <see cref="ZoneInfo.Encounters"/>.</summary>
    public IReadOnlyList<EncounterTable> LoadEncounters() => LoadData<List<EncounterTable>>("encounters") ?? [];

    T? LoadData<T>(string name)
    {
        var path = Path.Combine(Directory, "data", name + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json.Indented) : default;
    }

    /// <summary>An exported map by key (<see cref="Gen5WorldExtractor.OverworldKey"/> for Unova's overworld); null if missing.</summary>
    public WorldIndex? LoadMap(string key)
    {
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

    public byte[] ChunkTiles(int chunk) => File.ReadAllBytes(Path.Combine(Directory, "maps", "chunks", $"{chunk:D3}.tiles"));

    /// <summary>A <see cref="CharacterSheet"/> PNG; <paramref name="name"/> is "hero" or "heroine".</summary>
    public string OverworldSpritePath(string name) => Path.Combine(Directory, "sprites", "overworld", name + ".png");

    /// <summary>NPC sheet for an overworld sprite id (may not exist: some sprites are 3D models).</summary>
    public string NpcSpritePath(int sprite) => Path.Combine(Directory, "sprites", "overworld", "npc", $"{sprite}.png");

    /// <param name="view">front, back, front-shiny or back-shiny.</param>
    public string BattleSpritePath(int speciesId, string view) =>
        Path.Combine(Directory, "sprites", "battle", view, $"{speciesId:D3}.png");
}
