namespace InfiniteRoutes.Core;

/// <summary>
/// Per-user locations for config and caches. Set <c>IR_HOME</c> to keep everything under one
/// folder instead (portable installs, tests).
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "InfiniteRoutes";

    static string? HomeOverride => Environment.GetEnvironmentVariable("IR_HOME") is { Length: > 0 } h ? h : null;

    public static string ConfigDir => HomeOverride is { } h
        ? Path.Combine(h, "config")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName, "config");

    public static string CacheDir => HomeOverride is { } h
        ? Path.Combine(h, "cache")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName, "cache");

    /// <summary>Offline saves (roaming, like config: a cache clear must never lose them).</summary>
    public static string SaveDir => HomeOverride is { } h
        ? Path.Combine(h, "saves")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName, "saves");

    /// <summary>
    /// The game server's own folder when it's started without a server.pkir beside it (from an IDE, say): its config,
    /// database and certificate go here rather than into the source tree.
    /// </summary>
    public static string ServerDir => HomeOverride is { } h
        ? Path.Combine(h, "server")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName, "server");

    public static string RomLibraryFile => Path.Combine(ConfigDir, "roms.pkir");

    public static string ServersFile => Path.Combine(ConfigDir, "servers.pkir");
}
