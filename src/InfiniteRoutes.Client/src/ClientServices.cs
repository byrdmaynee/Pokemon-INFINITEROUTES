using InfiniteRoutes.Core;
using InfiniteRoutes.Extractor.Roms;

namespace InfiniteRoutes.Client;

/// <summary>Process-wide client state. Touch only from the main thread.</summary>
public static class ClientServices
{
    public static RomManifest Manifest { get; private set; } = null!;
    public static RomValidator Validator { get; private set; } = null!;
    public static RomLibrary Library { get; private set; } = null!;

    /// <exception cref="Core.Config.PkirException">roms.pkir exists but can't be parsed.</exception>
    public static void Initialize()
    {
        Manifest = RomManifest.LoadDefault();
        Validator = new RomValidator(Manifest);
        Library = RomLibrary.Load(AppPaths.RomLibraryFile, Manifest);
    }

    public static void SaveLibrary() => Library.Save(AppPaths.RomLibraryFile);

    /// <summary>Moves a broken roms.pkir aside so the player can start over. Returns the backup path.</summary>
    public static string BackUpBrokenLibrary()
    {
        var backup = AppPaths.RomLibraryFile + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
        File.Move(AppPaths.RomLibraryFile, backup);
        return backup;
    }
}
