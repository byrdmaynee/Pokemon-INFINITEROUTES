using InfiniteRoutes.Core;
using InfiniteRoutes.Extractor;
using InfiniteRoutes.Extractor.Extraction;
using InfiniteRoutes.Extractor.Roms;

namespace InfiniteRoutes.Client;

/// <summary>Process-wide client state. Touch only from the main thread.</summary>
public static class ClientServices
{
    public static RomManifest Manifest { get; private set; } = null!;
    public static RomValidator Validator { get; private set; } = null!;
    public static RomLibrary Library { get; private set; } = null!;

    /// <summary>Extracted Black/White assets (battle system + Unova), or null until extracted.</summary>
    public static GameAssets? UnovaAssets { get; private set; }

    /// <summary>
    /// The walking Pokémon sheets from an extracted HeartGold or SoulSilver (<see cref="Gen4Followers"/>), or null
    /// when neither is in the ROM Library: Active Buddies then use battle sprites.
    /// </summary>
    public static string? FollowerSprites { get; private set; }

    /// <exception cref="Core.Config.PkirException">roms.pkir exists but can't be parsed.</exception>
    public static void Initialize()
    {
        Manifest = RomManifest.LoadDefault();
        Validator = new RomValidator(Manifest);
        Library = RomLibrary.Load(AppPaths.RomLibraryFile, Manifest);
        RefreshAssets();
    }

    public static void SaveLibrary() => Library.Save(AppPaths.RomLibraryFile);

    /// <summary>Moves a broken roms.pkir aside so the player can start over. Returns the backup path.</summary>
    public static string BackUpBrokenLibrary()
    {
        var backup = AppPaths.RomLibraryFile + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
        File.Move(AppPaths.RomLibraryFile, backup);
        return backup;
    }

    public static bool CanExtract(RomLibraryEntry e) =>
        e is { Status: RomStatus.Valid, Path: not null, Sha1: not null, GameCode: not null }
        && (Gen5Extractor.Supports(Manifest.FindById(e.GameId)!) || Gen4Followers.Supports(Manifest.FindById(e.GameId)!));

    public static string? CacheDirectory(RomLibraryEntry e) =>
        e is { GameCode: { } code, Sha1: { } sha } ? ExtractionCache.DirectoryFor(code, sha) : null;

    public static bool IsExtracted(RomLibraryEntry e) =>
        CacheDirectory(e) is { } dir && ExtractionCache.IsComplete(dir);

    /// <summary>Usable ROMs whose assets haven't been extracted yet (with the current extractor version).</summary>
    public static IEnumerable<RomLibraryEntry> PendingExtractions() =>
        Library.Entries.Where(e => CanExtract(e) && !IsExtracted(e));

    /// <summary>Runs on a worker thread. Captures everything it needs up front.</summary>
    public static Task<string> ExtractAsync(RomLibraryEntry e, IProgress<ExtractionProgress> progress)
    {
        var (path, game, code, sha) = (e.Path!, Manifest.FindById(e.GameId)!, e.GameCode!, e.Sha1!);
        return Task.Run(() => Gen4Followers.Supports(game)
            ? Gen4Followers.Extract(path, game, code, sha, progress)
            : Gen5Extractor.Extract(path, game, code, sha, progress));
    }

    public static void RefreshAssets()
    {
        FollowerSprites = null;
        foreach (var id in new[] { "heartgold", "soulsilver" })
            if (Library[id] is var e && RomStatusText.IsUsable(e.Status) && CacheDirectory(e) is { } fdir && ExtractionCache.IsComplete(fdir)
                && Path.Combine(fdir, "sprites", "followers") is var followers && Directory.Exists(followers))
            {
                FollowerSprites = followers;
                break;
            }
        UnovaAssets = null;
        foreach (var id in new[] { "black", "white" })
        {
            var e = Library[id];
            if (RomStatusText.IsUsable(e.Status) && CacheDirectory(e) is { } dir && GameAssets.Open(dir) is { } assets)
            {
                UnovaAssets = assets;
                return;
            }
        }
    }
}
