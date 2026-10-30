using System.Text.Json;
using InfiniteRoutes.Core;

namespace InfiniteRoutes.Extractor;

public sealed record ExtractionStamp(string ExtractorVersion, string GameId, string GameCode, string Sha1, DateTime CompletedUtc);

public static class ExtractionCache
{
    /// <summary>Bump when any converter's output changes; old caches are then rebuilt.</summary>
    public const string ExtractorVersion = "0.3.3";

    const string StampFile = "extraction.json";

    /// <summary><c>cache/&lt;gameCode&gt;/&lt;sha1&gt;/&lt;extractorVersion&gt;/</c> — see BUILD_SPEC §2.3.</summary>
    public static string DirectoryFor(string gameCode, string sha1) =>
        Path.Combine(AppPaths.CacheDir, gameCode, sha1, ExtractorVersion);

    /// <summary>True when a finished extraction with the current extractor version is on disk.</summary>
    public static bool IsComplete(string directory) => ReadStamp(directory) is not null;

    public static ExtractionStamp? ReadStamp(string directory)
    {
        var path = Path.Combine(directory, StampFile);
        if (!File.Exists(path))
            return null;
        try
        {
            var stamp = JsonSerializer.Deserialize<ExtractionStamp>(File.ReadAllText(path), Json.Indented);
            return stamp?.ExtractorVersion == ExtractorVersion ? stamp : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void WriteStamp(string directory, ExtractionStamp stamp) =>
        File.WriteAllText(Path.Combine(directory, StampFile), JsonSerializer.Serialize(stamp, Json.Indented));
}

public static class Json
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
