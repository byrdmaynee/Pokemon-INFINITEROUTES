using System.Text.RegularExpressions;
using InfiniteRoutes.Core.Config;
using Tomlyn.Model;

namespace InfiniteRoutes.Server.Config;

/// <summary>
/// The server's settings from <c>server.pkir</c> (BUILD_SPEC §4.5). <see cref="Load"/> checks everything and refuses
/// to start with a list of what's wrong. Values may reference secrets as <c>${ENV:NAME}</c> or <c>${FILE:path}</c>.
/// Relative paths are relative to the config file's folder.
/// </summary>
public sealed record ServerConfig
{
    public string Name { get; init; } = "Infinite Routes";
    public string BindAddress { get; init; } = "0.0.0.0";
    /// <summary>0 picks any free port (tests).</summary>
    public int GamePort { get; init; } = 7450;
    public int ApiPort { get; init; } = 7451;
    public string PublicAddress { get; init; } = "127.0.0.1";
    public int MaxPlayers { get; init; } = 200;
    /// <summary>Channels of the world, and the players each holds.</summary>
    public int Channels { get; init; } = 2;
    public int ChannelCapacity { get; init; } = 250;

    /// <summary>"self-signed" (made on first start and kept in <see cref="DataDir"/>) or "files" (<see cref="CertFile"/>).</summary>
    public string TlsMode { get; init; } = "self-signed";
    public string CertFile { get; init; } = "";
    public string CertPassword { get; init; } = "";

    /// <summary>"sqlite" (a file, nothing to install) or "postgres".</summary>
    public string DatabaseProvider { get; init; } = "sqlite";
    public string Sqlite { get; init; } = "./data/ir.db";
    public string Postgres { get; init; } = "";

    /// <summary>
    /// An extraction cache folder (the extractor's output) the server reads maps, collision and warps from; "auto" uses
    /// the newest Black/White extraction the game client made on this machine.
    /// </summary>
    public string DataPack { get; init; } = "auto";
    /// <summary>Where the server keeps its own files (the self-signed certificate, the SQLite database by default).</summary>
    public string DataDir { get; init; } = "./data";

    public int CharacterSlots { get; init; } = 1;
    public int SessionHours { get; init; } = 24;
    /// <summary>Seconds between writes of moved players' positions (map changes and logouts write at once).</summary>
    public int SaveIntervalSeconds { get; init; } = 45;
    /// <summary>Minutes without any input before the server disconnects a player.</summary>
    public int IdleMinutes { get; init; } = 25;

    /// <summary>Whether Admin Mode needs an authenticator code (BUILD_SPEC §11.1; only turn off on a private test server).</summary>
    public bool Require2fa { get; init; } = true;
    /// <summary>Minutes without an admin action before Admin Mode ends.</summary>
    public int ElevationMinutes { get; init; } = 30;
    /// <summary>An account made Owner when it exists and no Owner does yet (first start), then ignored.</summary>
    public string BootstrapOwner { get; init; } = "";
    /// <summary>Chat lines a player may send per <see cref="ChatWindowSeconds"/>.</summary>
    public int ChatLines { get; init; } = 6;
    public int ChatWindowSeconds { get; init; } = 10;

    /// <summary>The folder the config was read from (relative paths resolve against it).</summary>
    public string BaseDir { get; init; } = ".";

    public string Resolve(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(BaseDir, path));

    /// <summary>Reads <paramref name="path"/> (all defaults when it doesn't exist).</summary>
    /// <exception cref="ServerConfigException">The file is broken; the message lists every problem.</exception>
    public static ServerConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        TomlTable root;
        try
        {
            root = Pkir.LoadOrEmpty(full);
        }
        catch (PkirException e)
        {
            throw new ServerConfigException([e.Message]);
        }
        return FromTable(root, Path.GetDirectoryName(full) ?? ".");
    }

    public static ServerConfig FromTable(TomlTable root, string baseDir)
    {
        var errors = new List<string>();
        var server = root.GetTable("server") ?? new TomlTable();
        var tls = root.GetTable("tls") ?? new TomlTable();
        var db = root.GetTable("database") ?? new TomlTable();
        var data = root.GetTable("data") ?? new TomlTable();
        var rules = root.GetTable("rules") ?? new TomlTable();
        var world = root.GetTable("world") ?? new TomlTable();
        var admin = root.GetTable("admin") ?? new TomlTable();
        var chat = root.GetTable("chat") ?? new TomlTable();
        var d = new ServerConfig();

        string Str(TomlTable t, string section, string key, string fallback)
        {
            if (!t.TryGetValue(key, out var v))
                return fallback;
            if (v is not string s)
            {
                errors.Add($"[{section}] {key} must be text.");
                return fallback;
            }
            return Secret(s, $"[{section}] {key}", errors);
        }
        int Int(TomlTable t, string section, string key, int fallback, int min, int max)
        {
            if (!t.TryGetValue(key, out var v))
                return fallback;
            if (v is not long l || l < min || l > max)
            {
                errors.Add($"[{section}] {key} must be a whole number from {min} to {max}.");
                return fallback;
            }
            return (int)l;
        }

        var config = new ServerConfig
        {
            BaseDir = baseDir,
            Name = Str(server, "server", "name", d.Name),
            BindAddress = Str(server, "server", "bind_address", d.BindAddress),
            GamePort = Int(server, "server", "game_port", d.GamePort, 0, 65535),
            ApiPort = Int(server, "server", "api_port", d.ApiPort, 0, 65535),
            PublicAddress = Str(server, "server", "public_address", d.PublicAddress),
            MaxPlayers = Int(server, "server", "max_players", d.MaxPlayers, 1, 100_000),
            TlsMode = Str(tls, "tls", "mode", d.TlsMode),
            CertFile = Str(tls, "tls", "cert_file", d.CertFile),
            CertPassword = Str(tls, "tls", "cert_password", d.CertPassword),
            DatabaseProvider = Str(db, "database", "provider", d.DatabaseProvider),
            Sqlite = Str(db, "database", "sqlite", d.Sqlite),
            Postgres = Str(db, "database", "postgres", d.Postgres),
            DataPack = Str(data, "data", "data_pack", d.DataPack),
            DataDir = Str(data, "data", "server_data", d.DataDir),
            CharacterSlots = Int(rules, "rules", "character_slots_free", d.CharacterSlots, 1, 10),
            Channels = Int(world, "world", "channels", d.Channels, 1, 64),
            ChannelCapacity = Int(world, "world", "channel_capacity", d.ChannelCapacity, 1, 10_000),
            SaveIntervalSeconds = Int(world, "world", "save_interval_seconds", d.SaveIntervalSeconds, 5, 600),
            IdleMinutes = Int(world, "world", "idle_minutes", d.IdleMinutes, 1, 1440),
            SessionHours = Int(server, "server", "session_hours", d.SessionHours, 1, 24 * 30),
            ElevationMinutes = Int(admin, "admin", "elevation_timeout_minutes", d.ElevationMinutes, 1, 24 * 60),
            BootstrapOwner = Str(admin, "admin", "bootstrap_owner", d.BootstrapOwner),
            ChatLines = Int(chat, "chat", "lines", d.ChatLines, 1, 100),
            ChatWindowSeconds = Int(chat, "chat", "window_seconds", d.ChatWindowSeconds, 1, 600),
        };
        if (admin.TryGetValue("require_2fa", out var r2))
        {
            if (r2 is bool b)
                config = config with { Require2fa = b };
            else
                errors.Add("[admin] require_2fa must be true or false.");
        }

        if (config.GamePort == config.ApiPort && config.GamePort != 0)
            errors.Add("[server] game_port and api_port must differ.");
        if (config.TlsMode is not ("self-signed" or "files"))
            errors.Add("[tls] mode must be \"self-signed\" or \"files\" (\"acme\" isn't supported yet).");
        if (config.TlsMode == "files" && !File.Exists(config.Resolve(config.CertFile)))
            errors.Add($"[tls] cert_file not found: {config.CertFile}");
        if (config.DatabaseProvider is not ("sqlite" or "postgres"))
            errors.Add("[database] provider must be \"sqlite\" or \"postgres\".");
        if (config.DatabaseProvider == "postgres" && string.IsNullOrWhiteSpace(config.Postgres))
            errors.Add("[database] postgres needs a connection string when provider = \"postgres\".");
        if (errors.Count > 0)
            throw new ServerConfigException(errors);
        return config;
    }

    static readonly Regex Reference = new(@"\$\{(ENV|FILE):([^}]+)\}", RegexOptions.Compiled);

    /// <summary>Replaces <c>${ENV:NAME}</c> and <c>${FILE:path}</c> with the variable or the file's (trimmed) contents.</summary>
    static string Secret(string value, string where, List<string> errors) =>
        Reference.Replace(value, m =>
        {
            var name = m.Groups[2].Value;
            if (m.Groups[1].Value == "ENV")
            {
                if (Environment.GetEnvironmentVariable(name) is { } env)
                    return env;
                errors.Add($"{where}: environment variable {name} isn't set.");
                return "";
            }
            if (File.Exists(name))
                return File.ReadAllText(name).Trim();
            errors.Add($"{where}: file {name} not found.");
            return "";
        });

    /// <summary>A starting server.pkir with every setting at its default.</summary>
    public const string Template = """
        # server.pkir — Infinite Routes server settings (TOML). Restart the server after changing it.
        [server]
        name           = "Infinite Routes — Home Test"
        bind_address   = "0.0.0.0"
        game_port      = 7450            # TCP+TLS game traffic
        api_port       = 7451            # HTTPS accounts API
        public_address = "127.0.0.1"     # what clients are told to connect to (LAN IP now, VPS IP/domain later)
        max_players    = 200
        session_hours  = 24

        [tls]
        mode      = "self-signed"        # self-signed | files
        cert_file = ""                   # a .pfx when mode = "files"
        cert_password = ""

        [database]
        provider = "sqlite"              # sqlite | postgres
        sqlite   = "./data/ir.db"
        postgres = ""                    # e.g. "Host=localhost;Database=ir;Username=ir;Password=${ENV:IR_DB_PASSWORD}"

        [data]
        data_pack   = "auto"             # "auto" = the Black/White extraction your game client made on this machine,
                                         # or an extraction cache folder from `ir-extract extract <rom>`
        server_data = "./data"

        [world]
        channels              = 2
        channel_capacity      = 250
        save_interval_seconds = 45
        idle_minutes          = 25

        [rules]
        character_slots_free = 1

        [chat]
        lines          = 6               # chat lines a player may send per window
        window_seconds = 10

        [admin]
        bootstrap_owner = ""             # account made Owner on start if no Owner exists (or: ir-server admin grant <account> owner)
        require_2fa     = true           # Admin Mode asks for an authenticator code
        elevation_timeout_minutes = 30
        """;
}

public sealed class ServerConfigException(IReadOnlyList<string> errors)
    : Exception("server.pkir has problems:\n  " + string.Join("\n  ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
