using InfiniteRoutes.Core;

namespace InfiniteRoutes.Extractor;

public static class ExtractionCache
{
    /// <summary>Bump when any converter's output changes; old caches are then rebuilt.</summary>
    public const string ExtractorVersion = "0.1.0";

    /// <summary><c>cache/&lt;gameCode&gt;/&lt;sha1&gt;/&lt;extractorVersion&gt;/</c> — see BUILD_SPEC §2.3.</summary>
    public static string DirectoryFor(string gameCode, string sha1) =>
        Path.Combine(AppPaths.CacheDir, gameCode, sha1, ExtractorVersion);
}
