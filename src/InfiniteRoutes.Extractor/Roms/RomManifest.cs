using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfiniteRoutes.Extractor.Roms;

public sealed record KnownDump(string RegionCode, int Revision, string Sha1, string? Source = null);

public sealed record GameDefinition(
    string Id,
    string Name,
    int Generation,
    string BaseCode,
    IReadOnlyList<string> RegionCodes,
    string Profile,
    IReadOnlyList<string> RequiredFiles,
    IReadOnlyList<KnownDump> KnownDumps);

public sealed record RegionDefinition(
    string Id,
    string Name,
    IReadOnlyList<string> AnyOf,
    bool RequiredToPlay,
    bool InBaseGame);

/// <summary>Which games and dumps the client accepts, and which games unlock which regions.</summary>
public sealed record RomManifest(int Version, IReadOnlyList<RegionDefinition> Regions, IReadOnlyList<GameDefinition> Games)
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public GameDefinition? FindByBaseCode(string baseCode) =>
        Games.FirstOrDefault(g => g.BaseCode.Equals(baseCode, StringComparison.Ordinal));

    public GameDefinition? FindById(string id) =>
        Games.FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static RomManifest Parse(string json) =>
        JsonSerializer.Deserialize<RomManifest>(json, JsonOptions)
        ?? throw new InvalidDataException("ROM manifest is empty.");

    /// <summary>The manifest built into this assembly from <c>data/rom_manifest.json</c>.</summary>
    public static RomManifest LoadDefault()
    {
        using var stream = typeof(RomManifest).Assembly.GetManifestResourceStream("InfiniteRoutes.rom_manifest.json")
            ?? throw new InvalidOperationException("Embedded ROM manifest is missing from the build.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
