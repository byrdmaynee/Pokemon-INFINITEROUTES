using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Server.Accounts;
using InfiniteRoutes.Server.Config;
using InfiniteRoutes.Server.World;

namespace InfiniteRoutes.Server.Admin;

/// <summary>
/// Admin Mode and the admin console (BUILD_SPEC §11). Staff log in like players; <see cref="Elevate"/> needs the
/// password again and, unless the server turns it off, a code from their authenticator (2FA set up from the client's
/// Account screen). In Admin Mode they can run the commands their role allows. The server checks every command; each
/// one, and every elevation (including failed ones), goes in the append-only audit log.
/// </summary>
public sealed class AdminService(WorldService world, AccountService accounts, ServerConfig config, ILogger<AdminService> log)
{
    const int MaxAttempts = 5;
    static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(5);
    readonly Dictionary<long, List<DateTime>> _attempts = [];

    sealed record Command(string Verb, string? Permission, string Usage, string Help);

    static readonly Command[] Commands =
    [
        new("help", null, "help", "this list"),
        new("who", Permissions.Who, "who", "everyone online, where they are"),
        new("players", Permissions.Who, "players", "the player list for the admin panel"),
        new("mute", Permissions.Mute, "mute <player> <minutes> <reason>", "block a player's chat for a while (kept if they log out)"),
        new("unmute", Permissions.Mute, "unmute <player>", "let a muted player chat again"),
        new("spectate", Permissions.Spectate, "spectate <player>  |  spectate off", "watch a player (you follow them, invisible)"),
        new("inspect", Permissions.Inspect, "inspect <player>", "a player's account, character and game (read-only)"),
        new("kick", Permissions.Kick, "kick <player> <reason>", "disconnect a player (the reason is required and audited)"),
        new("announce", Permissions.Announce, "announce <text>", "a banner and chat line to everyone"),
        new("tp", Permissions.Teleport, "tp <player>  |  tp <zone> <x> <y>", "go to a player or a place"),
        new("summon", Permissions.Summon, "summon <player>", "bring a player to you"),
        new("invisible", Permissions.Invisible, "invisible [on|off]", "hidden from players (not from staff in Admin Mode)"),
        new("noclip", Permissions.Noclip, "noclip [on|off]", "walk through walls at any speed"),
        new("grant", Permissions.Roles, "grant <account> <role>", "set an account's role (below your own; Owner can do any)"),
        new("audit", Permissions.AuditRead, "audit [count]", "the latest audit log entries"),
        new("exit", null, "exit", "leave Admin Mode"),
    ];

    public async Task Handle(WorldPlayer p, Message message)
    {
        try
        {
            switch (message)
            {
                case Elevate e:
                    await Elevate(p, e);
                    break;
                case AdminCommand c:
                    await Run(p, c.Text);
                    break;
            }
        }
        catch (Exception e)
        {
            log.LogError(e, "Admin message from {Name} failed", p.Name);
            p.Link.Send(new AdminReply(false, ["That didn't work (the server logged the error)."]));
        }
    }

    Task Audit(WorldPlayer p, string action, string target, string details) =>
        accounts.Audit(p.AccountId, p.AccountName, p.Role.ToString(), action, target, details);

    async Task Elevate(WorldPlayer p, Elevate e)
    {
        if (p.Role == Role.Player)
        {
            p.Link.Send(new AdminReply(false, ["Your account has no staff role."]));
            return;
        }
        lock (_attempts)
        {
            var list = _attempts.TryGetValue(p.AccountId, out var l) ? l : _attempts[p.AccountId] = [];
            list.RemoveAll(t => DateTime.UtcNow - t > AttemptWindow);
            if (list.Count >= MaxAttempts)
            {
                p.Link.Send(new AdminReply(false, ["Too many tries. Wait a few minutes."]));
                return;
            }
            list.Add(DateTime.UtcNow);
        }
        var account = await accounts.ById(p.AccountId);
        if (config.Require2fa && account is not { TotpEnabled: true })
        {
            p.Link.Send(new AdminReply(false, ["Set up two-factor first: Play Online → Account → Two-factor (this server requires it for Admin Mode)."]));
            return;
        }
        var ok = await accounts.CheckPassword(p.AccountId, e.Password)
            && (!config.Require2fa && account is not { TotpEnabled: true } || await accounts.VerifyTotp(p.AccountId, e.Code));
        await Audit(p, ok ? "elevate" : "elevate-failed", p.Name, ok ? "entered Admin Mode" : "wrong password or code");
        if (!ok)
        {
            p.Link.Send(new AdminReply(false, ["Wrong password or code."]));
            return;
        }
        lock (_attempts)
            _attempts.Remove(p.AccountId);
        world.SetElevated(p, true);
        p.Link.Send(new AdminReply(true, [$"Admin Mode on ({p.Role}). Type /help for your commands. It ends after {config.ElevationMinutes} minutes without an admin action."]));
    }

    async Task Run(WorldPlayer p, string text)
    {
        var parts = WorldService.Clean(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;
        void Reply(bool ok, params string[] lines) => p.Link.Send(new AdminReply(ok, lines));
        if (!p.Elevated)
        {
            Reply(false, p.Role == Role.Player ? "Your account has no staff role." : "Enter Admin Mode first (/admin).");
            return;
        }
        var verb = parts[0].ToLowerInvariant();
        var args = parts.Skip(1).ToArray();
        if (Commands.FirstOrDefault(c => c.Verb == verb) is not { } command)
        {
            Reply(false, $"Unknown command {verb}. Type /help.");
            return;
        }
        if (command.Permission is { } perm && !Permissions.Has(p.Role, perm))
        {
            Reply(false, $"Your role ({p.Role}) can't use {verb}.");
            await Audit(p, "denied", verb, string.Join(' ', args));
            return;
        }
        world.Touch(p);

        WorldPlayer? Target(string? name)
        {
            if (name is null)
            {
                Reply(false, $"Usage: {command.Usage}");
                return null;
            }
            var t = world.Find(name);
            if (t is null)
                Reply(false, $"{name} isn't online.");
            return t;
        }

        switch (verb)
        {
            case "help":
                Reply(true, [.. Commands.Where(c => c.Permission is null || Permissions.Has(p.Role, c.Permission)).Select(c => $"/{c.Usage} — {c.Help}")]);
                return;
            case "exit":
                world.SetElevated(p, false);
                await Audit(p, "exit", p.Name, "left Admin Mode");
                Reply(true, "Admin Mode off.");
                return;
            case "who":
            {
                var players = world.Players();
                Reply(true, [$"{players.Count} online:", .. players.Select(o =>
                    $"{o.Name} ({o.AccountName}{(o.Role > Role.Player ? ", " + o.Role : "")}) — channel {o.Channel}, zone {o.Zone} at {o.X},{o.Y}{(o.Elevated ? " [admin]" : "")}{(o.Elevated && o.Invisible ? " [invisible]" : "")}")]);
                await Audit(p, "who", "", $"{players.Count} online");
                return;
            }
            case "players":
                p.Link.Send(new AdminPlayers(world.AdminRows()));
                return;
            case "mute":
            {
                if (Target(args.FirstOrDefault()) is not { } t)
                    return;
                if (args.Length < 3 || !int.TryParse(args[1], out var minutes) || minutes is < 1 or > 60 * 24 * 30)
                {
                    Reply(false, "Usage: mute <player> <minutes 1–43200> <reason> (the reason is required).");
                    return;
                }
                if (t.Role >= p.Role && t != p)
                {
                    Reply(false, $"You can't mute a {t.Role}.");
                    return;
                }
                var reason = string.Join(' ', args.Skip(2));
                var until = DateTime.UtcNow.AddMinutes(minutes);
                await accounts.SetMute(t.AccountId, until, reason);
                world.Mute(t, until, reason);
                await Audit(p, "mute", t.Name, $"{minutes} min: {reason}");
                Reply(true, $"Muted {t.Name} for {minutes} minutes.");
                return;
            }
            case "unmute":
            {
                if (Target(args.FirstOrDefault()) is not { } t)
                    return;
                await accounts.SetMute(t.AccountId, null, null);
                world.Mute(t, null, null);
                await Audit(p, "unmute", t.Name, "");
                Reply(true, $"{t.Name} can chat again.");
                return;
            }
            case "spectate":
            {
                if (args.FirstOrDefault()?.ToLowerInvariant() == "off")
                {
                    world.Spectate(p, null);
                    await Audit(p, "spectate-off", "", "");
                    Reply(true, "Stopped spectating.");
                    return;
                }
                if (Target(args.FirstOrDefault()) is not { } t)
                    return;
                if (!world.Spectate(p, t))
                {
                    Reply(false, "You can't spectate yourself.");
                    return;
                }
                await Audit(p, "spectate", t.Name, "");
                Reply(true, $"Spectating {t.Name} (invisible). /spectate off to stop.");
                return;
            }
            case "inspect":
            {
                if (Target(args.FirstOrDefault()) is not { } t)
                    return;
                var lines = new List<string>
                {
                    $"{t.Name} — account {t.AccountName}, role {t.Role}",
                    $"Channel {t.Channel}, zone {t.Zone} at {t.X},{t.Y}{(t.Elevated ? ", in Admin Mode" : "")}",
                };
                if (t.State is { } s)
                {
                    lines.Add($"Money ₽{s.Progress.Money}, badges {s.Progress.Badges.Count}, caught {s.Progress.Caught}, play time {s.Progress.PlayTime:h\\:mm}");
                    lines.Add("Party: " + string.Join(", ", s.Party.Select(m => $"{m.Name} Lv{m.Level} {m.Hp}/{m.MaxHp}")));
                    lines.Add($"PC: {s.Box.Count} Pokémon; Bag: {s.Bag.Items.Sum(i => i.Count)} items in {s.Bag.Items.Count()} kinds");
                }
                if (t.MutedUntil is { } muted && muted > DateTime.UtcNow)
                    lines.Add($"Muted until {muted.ToLocalTime():HH:mm}: {t.MuteReason}");
                p.Link.Send(new AdminReply(true, lines, $"inspect:{t.Name}"));
                await Audit(p, "inspect", t.Name, "");
                return;
            }
            case "kick":
            {
                if (Target(args.FirstOrDefault()) is not { } t)
                    return;
                var reason = string.Join(' ', args.Skip(1));
                if (reason.Length == 0)
                {
                    Reply(false, "Give a reason: kick <player> <reason>.");
                    return;
                }
                if (t.Role >= p.Role && t != p)
                {
                    Reply(false, $"You can't kick a {t.Role}.");
                    return;
                }
                world.Kick(t, $"You were disconnected by staff: {reason}");
                await Audit(p, "kick", t.Name, reason);
                Reply(true, $"Kicked {t.Name}.");
                return;
            }
            case "announce":
            {
                var message = string.Join(' ', args);
                if (message.Length == 0)
                {
                    Reply(false, $"Usage: {command.Usage}");
                    return;
                }
                world.Announce("Announcement", message);
                await Audit(p, "announce", "everyone", message);
                Reply(true, "Announced.");
                return;
            }
            case "tp":
            {
                if (args.Length == 3 && int.TryParse(args[0], out var zone) && int.TryParse(args[1], out var x) && int.TryParse(args[2], out var y))
                {
                    var ok = world.Teleport(p, zone, x, y);
                    Reply(ok, ok ? $"Teleported to zone {zone} at {x},{y}." : $"There's no zone {zone}.");
                    await Audit(p, "teleport", $"zone {zone} {x},{y}", ok ? "" : "failed");
                    return;
                }
                if (Target(args.FirstOrDefault()) is not { } t)
                    return;
                world.TeleportTo(p, t);
                await Audit(p, "teleport", t.Name, "");
                Reply(true, $"Went to {t.Name}.");
                return;
            }
            case "summon":
            {
                if (Target(args.FirstOrDefault()) is not { } t)
                    return;
                world.TeleportTo(t, p);
                t.Link.Send(new Notice("A staff member brought you to them.", t.Channel));
                await Audit(p, "summon", t.Name, "");
                Reply(true, $"Brought {t.Name} here.");
                return;
            }
            case "invisible" or "noclip":
            {
                var on = args.FirstOrDefault()?.ToLowerInvariant() switch
                {
                    "on" => true,
                    "off" => false,
                    _ => verb == "invisible" ? !p.Invisible : !p.Noclip,
                };
                world.SetToggles(p, verb == "invisible" ? on : null, verb == "noclip" ? on : null);
                await Audit(p, verb, p.Name, on ? "on" : "off");
                Reply(true, $"{(verb == "invisible" ? "Invisible" : "Noclip")} {(on ? "on" : "off")}.");
                return;
            }
            case "grant":
            {
                if (args.Length != 2 || !Enum.TryParse<Role>(args[1], ignoreCase: true, out var role))
                {
                    Reply(false, $"Usage: {command.Usage} (roles: {string.Join(", ", Enum.GetNames<Role>())})");
                    return;
                }
                if (await accounts.ByName(args[0]) is not { } target)
                {
                    Reply(false, $"No account named {args[0]}.");
                    return;
                }
                var current = Permissions.Parse(target.Role);
                if (!Permissions.CanGrant(p.Role, current, role))
                {
                    Reply(false, $"A {p.Role} can't make a {current} into a {role}.");
                    await Audit(p, "grant-denied", target.Name, $"{current} → {role}");
                    return;
                }
                await accounts.SetRole(target.Name, role);
                await Audit(p, "grant", target.Name, $"{current} → {role}");
                // Takes effect for them now if they're online (out of Admin Mode if they were demoted).
                if (world.Players().FirstOrDefault(o => o.AccountId == target.Id) is { } online)
                {
                    online.Role = role;
                    world.SetElevated(online, online.Elevated && role > Role.Player && role >= current);
                }
                Reply(true, $"{target.Name} is now {role}.");
                return;
            }
            case "audit":
            {
                var count = args.Length > 0 && int.TryParse(args[0], out var n) ? Math.Clamp(n, 1, 100) : 15;
                var entries = await accounts.RecentAudit(count);
                Reply(true, [.. entries.Select(e => $"{e.Time:yyyy-MM-dd HH:mm:ss} {e.Actor} ({e.Role}) {e.Action} {e.Target} {e.Details}".TrimEnd())]);
                return;
            }
        }
    }
}
