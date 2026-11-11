using InfiniteRoutes.Extractor.Extraction;
using InfiniteRoutes.Server;
using InfiniteRoutes.Server.Config;
using InfiniteRoutes.Server.Net;
using InfiniteRoutes.Server.World;

const string Usage = """
    ir-server — Infinite Routes game server

    Usage:
      ir-server [run] [--config <server.pkir>]   Start the server (config: ./server.pkir if there is one,
                                                 else %LOCALAPPDATA%\InfiniteRoutes\server\server.pkir, made on first run)
      ir-server init [--config <server.pkir>]    Write a server.pkir with every setting at its default
      ir-server check [--config <server.pkir>]   Check the config and data pack, then exit
      ir-server admin grant <account> <role>     Set an account's role (Player, Helper, Moderator, GameMaster,
                                                 Admin, Owner) — how the first Owner is made
      ir-server admin staff                      List accounts with a staff role
      ir-server admin audit [count]              Show the latest audit log entries
    """;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "run";
var configIndex = Array.IndexOf(args, "--config");
string configPath;
if (configIndex >= 0 && configIndex + 1 < args.Length)
    configPath = args[configIndex + 1];
else if (File.Exists("server.pkir") || command == "init")
    configPath = "server.pkir";
else
{
    // Started without a config beside it (from an IDE, say): use the per-user server folder, making a config there.
    configPath = Path.Combine(InfiniteRoutes.Core.AppPaths.ServerDir, "server.pkir");
    if (!File.Exists(configPath))
    {
        InfiniteRoutes.Core.Config.Pkir.WriteAtomic(configPath, ServerConfig.Template.Replace("\n", Environment.NewLine) + Environment.NewLine);
        Console.WriteLine($"Made a default config at {configPath} (edit it to change ports, channels, the database…).");
    }
}

switch (command)
{
    case "init":
        if (File.Exists(configPath))
        {
            Console.Error.WriteLine($"{configPath} already exists; not overwriting it.");
            return 1;
        }
        InfiniteRoutes.Core.Config.Pkir.WriteAtomic(configPath, ServerConfig.Template.Replace("\n", Environment.NewLine) + Environment.NewLine);
        Console.WriteLine($"Wrote {Path.GetFullPath(configPath)}. Set [data] data_pack to an extraction cache folder, then run `ir-server`.");
        return 0;
    case "run" or "check" or "admin":
        break;
    default:
        Console.WriteLine(Usage);
        return command is "help" or "-h" or "--help" ? 0 : 1;
}

ServerConfig config;
try
{
    config = ServerConfig.Load(configPath);
}
catch (ServerConfigException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}
Console.WriteLine($"Config: {Path.GetFullPath(configPath)}");

if (command == "admin")
    return await AdminCli(config, args.SkipWhile(a => a != "admin").Skip(1).Where(a => a != "--config" && a != configPath).ToArray());

var auto = config.DataPack.Trim() is "" or "auto";
var dataPack = auto ? InfiniteRoutes.Extractor.ExtractionCache.FindLocal("black", "white") : config.Resolve(config.DataPack);
if (dataPack is null || GameAssets.Open(dataPack) is not { } assets)
{
    Console.Error.WriteLine(auto
        ? "[data] data_pack = \"auto\", but this machine has no Black/White extraction yet (for this extractor version)."
        : $"[data] data_pack: no extraction cache at {dataPack}.");
    Console.Error.WriteLine("Open the game client once with your Black or White ROM added (it extracts on launch), or run");
    Console.Error.WriteLine("`ir-extract extract <rom>` and point data_pack at the folder it reports.");
    return 2;
}
var world = new AssetWorldData(assets);
Console.WriteLine($"Data pack {assets.Stamp.GameId} (extractor {assets.Stamp.ExtractorVersion}); new characters start in zone {world.Start.Zone} at ({world.Start.X}, {world.Start.Y}).");
if (command == "check")
{
    Console.WriteLine("Config and data pack are fine.");
    return 0;
}

var rules = InfiniteRoutes.Server.Game.GameRules.From(assets);
if (rules is null)
    Console.WriteLine("The data pack has no battle data (extracted before 0.6.0): characters get no party or Bag.");
var app = ServerHost.Build(config, world, rules: rules);
var cert = app.Services.GetRequiredService<System.Security.Cryptography.X509Certificates.X509Certificate2>();
Console.WriteLine($"{config.Name}: accounts API https://{config.PublicAddress}:{config.ApiPort}, game port {config.GamePort}.");
Console.WriteLine($"TLS certificate fingerprint (players are asked to trust it): {Certificates.Fingerprint(cert)}");
await app.RunAsync();
return 0;

// The admin commands that work without the server running (they open the database directly).
static async Task<int> AdminCli(ServerConfig config, string[] args)
{
    var factory = new ServiceCollection()
        .AddDbContextFactory<InfiniteRoutes.Server.Data.IrDb>(o => InfiniteRoutes.Server.Data.IrDb.Configure(o, config))
        .BuildServiceProvider()
        .GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<InfiniteRoutes.Server.Data.IrDb>>();
    using (var db = factory.CreateDbContext())
        InfiniteRoutes.Server.Data.Schema.Ensure(db);
    var accounts = new InfiniteRoutes.Server.Accounts.AccountService(factory, config, new InfiniteRoutes.Server.Accounts.StartPoint(0, 0, 0, 0));
    switch (args.FirstOrDefault())
    {
        case "grant" when args.Length == 3:
            if (!Enum.TryParse<InfiniteRoutes.Server.Admin.Role>(args[2], ignoreCase: true, out var role))
            {
                Console.Error.WriteLine($"No role {args[2]}. Roles: {string.Join(", ", Enum.GetNames<InfiniteRoutes.Server.Admin.Role>())}.");
                return 1;
            }
            if (await accounts.SetRole(args[1], role) is { } error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            var account = await accounts.ByName(args[1]);
            await accounts.Audit(account!.Id, "server console", "Server", "grant", account.Name, $"→ {role}");
            Console.WriteLine($"{account.Name} is now {role}.{(role > InfiniteRoutes.Server.Admin.Role.Player && !account.TotpEnabled && config.Require2fa ? " They need to set up two-factor (Play Online → Account) before Admin Mode." : "")}");
            return 0;
        case "staff":
            foreach (var (name, r, twoFactor) in await accounts.Staff())
                Console.WriteLine($"{name,-16} {r,-11} two-factor {(twoFactor ? "on" : "off")}");
            return 0;
        case "audit":
            foreach (var e in await accounts.RecentAudit(args.Length > 1 && int.TryParse(args[1], out var n) ? n : 30))
                Console.WriteLine($"{e.Time:yyyy-MM-dd HH:mm:ss}  {e.Actor} ({e.Role})  {e.Action}  {e.Target}  {e.Details}");
            return 0;
        default:
            Console.Error.WriteLine("Usage: ir-server admin grant <account> <role> | admin staff | admin audit [count]");
            return 1;
    }
}
