using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;
using InfiniteRoutes.Server.Data;
using InfiniteRoutes.Server.World;

namespace InfiniteRoutes.Server.Admin;

/// <summary>
/// The admin window's actions (BUILD_SPEC §11.3): finding players (online or not), their full details, roles, bans,
/// and edits to their game — money, items, healing, giving, editing and releasing Pokémon. Every game edit needs a
/// reason, works the same on online and offline players, is recorded before and after (so <see cref="Revert"/> can
/// undo it, warning when the game changed since) and goes in the audit log. Pokémon that fail <see cref="Legality"/>
/// need <see cref="Permissions.PokemonIllegal"/>; anything staff create or edit carries the admin-origin flag. Nobody
/// can act on an account whose role ranks at or above their own. Owners edit roles.
/// </summary>
public sealed partial class AdminService
{
    const int MaxMoney = 9_999_999;

    /// <summary>Who an action is about: the character (and its player when online), its account and role.</summary>
    sealed record Subject(long CharacterId, string Character, Account Account, RoleInfo Role, WorldPlayer? Online, Character? Row);

    async Task<Subject?> Find(string name)
    {
        if (world.Playing(name) is { } online && await accounts.ById(online.AccountId) is { } account)
            return new Subject(online.CharacterId, online.Name, account, RoleOf(online), online, null);
        if (await accounts.CharacterNamed(name) is { Account: { } owner } row)
            return new Subject(row.Id, row.Name, owner, roles.Get(owner.Role), null, row);
        // An account name finds its (first) character.
        if (await accounts.ByName(name) is { } byAccount && (await accounts.Characters(byAccount.Id)).FirstOrDefault() is { } first)
            return await Find(first.Name);
        return null;
    }

    static string Arg(AdminAction a, string key, string fallback = "") => a.Args.TryGetValue(key, out var v) ? v : fallback;

    async Task Act(WorldPlayer p, AdminAction a)
    {
        void Reply(bool ok, params string[] lines) => p.Link.Send(new AdminReply(ok, lines));
        if (!p.Elevated)
        {
            Reply(false, "Enter Admin Mode first (/admin).");
            return;
        }
        var me = RoleOf(p);
        string? permission = a.Verb switch
        {
            "search" or "detail" => Permissions.Inspect,
            "set-role" => Permissions.Roles,
            "ban" or "unban" => Permissions.Ban,
            "money" or "give-item" or "take-item" => Permissions.Inventory,
            "heal" or "pokemon-give" or "pokemon-save" or "pokemon-release" => Permissions.PokemonEdit,
            "revert" => Permissions.Revert,
            "audit" => Permissions.AuditRead,
            "roles" => null,
            "role-create" or "role-delete" or "role-perm" or "role-rank" or "role-beacon" => null,
            _ => "unknown",
        };
        if (permission == "unknown")
        {
            Reply(false, $"Unknown action {a.Verb}.");
            return;
        }
        if (permission is not null && !me.Has(permission))
        {
            Reply(false, $"Your role ({me.Name}) can't do that.");
            await Audit(p, "denied", a.Verb, a.Target);
            return;
        }
        if (a.Verb.StartsWith("role-", StringComparison.Ordinal) && !me.IsOwner)
        {
            Reply(false, "Only Owners can change roles.");
            await Audit(p, "denied", a.Verb, a.Target);
            return;
        }
        world.Touch(p);
        var reason = WorldService.Clean(a.Reason);

        switch (a.Verb)
        {
            case "search":
            {
                var found = await accounts.SearchCharacters(a.Target);
                var online = world.AdminRows().ToDictionary(r => r.Name.ToLowerInvariant());
                p.Link.Send(new AdminPlayers([.. found.Select(c => online.TryGetValue(c.NameKey, out var row) ? row
                    : new AdminPlayerRow(0, c.Name, c.Account!.Name, roles.Get(c.Account.Role).Name, 0, new NetLocation(c.Zone, c.X, c.Y, c.Facing),
                        false, false, MutedText(c.Account.MutedUntil), Online: false))]));
                return;
            }
            case "detail":
                if (!await SendDetail(p, a.Target))
                    Reply(false, $"There's no character {a.Target}.");
                await Audit(p, "inspect", a.Target, "");
                return;
            case "set-role":
            {
                var (ok, message) = await GrantRole(p, a.Target, Arg(a, "role"));
                Reply(ok, message);
                await SendDetail(p, a.Target);
                return;
            }
            case "ban":
            {
                var (ok, message) = await Ban(p, a.Target, Arg(a, "minutes", "perm"), reason);
                Reply(ok, message);
                await SendDetail(p, a.Target);
                return;
            }
            case "unban":
            {
                var (ok, message) = await Ban(p, a.Target, "off", reason);
                Reply(ok, message);
                await SendDetail(p, a.Target);
                return;
            }
            case "money" or "give-item" or "take-item" or "heal" or "pokemon-give" or "pokemon-save" or "pokemon-release":
                await EditGame(p, a, reason);
                return;
            case "revert":
            {
                var (ok, lines) = await Revert(p, long.TryParse(Arg(a, "change"), out var id) ? id : 0, Arg(a, "force") == "yes");
                Reply(ok, [.. lines]);
                return;
            }
            case "audit":
            {
                var entries = await accounts.RecentAudit(int.TryParse(Arg(a, "count"), out var n) ? Math.Clamp(n, 1, 200) : 60);
                p.Link.Send(new AdminAudit([.. entries.Select(e => new AuditRow(e.Id, e.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    e.Actor, e.Role, e.Action, e.Target, e.Details, e.Change))]));
                return;
            }
            case "roles":
                await SendRoles(p);
                return;
            case "role-create":
            {
                var error = await roles.Create(a.Target, int.TryParse(Arg(a, "rank"), out var rank) ? rank : -1, Arg(a, "copy") is { Length: > 0 } c ? c : null);
                await RoleResult(p, error, "role-create", a.Target, $"rank {Arg(a, "rank")}{(Arg(a, "copy").Length > 0 ? ", copied from " + Arg(a, "copy") : "")}", $"Made the role {a.Target}.");
                return;
            }
            case "role-delete":
                await RoleResult(p, await roles.Delete(a.Target), "role-delete", a.Target, "", $"Deleted the role {a.Target}.");
                return;
            case "role-perm":
            {
                var on = Arg(a, "on") == "yes";
                var permissionName = Arg(a, "permission");
                await RoleResult(p, await roles.SetPermission(a.Target, permissionName, on), "role-permission", a.Target,
                    $"{(on ? "+" : "-")}{permissionName}", $"{a.Target} {(on ? "can now" : "can no longer")}: {permissionName}.");
                return;
            }
            case "role-beacon":
                var beacon = Arg(a, "beacon");
                await RoleResult(p, await roles.SetBeacon(a.Target, beacon), "role-beacon", a.Target, Core.Player.Beacons.NameOf(beacon),
                    $"{a.Target} now shows {(beacon.Length > 0 ? "the " + Core.Player.Beacons.NameOf(beacon).ToLowerInvariant() : "no beacon")}.");
                return;
            case "role-rank":
            {
                var rank = int.TryParse(Arg(a, "rank"), out var r) ? r : -1;
                await RoleResult(p, await roles.SetRank(a.Target, rank), "role-rank", a.Target, $"rank {rank}", $"{a.Target} is now rank {rank}.");
                return;
            }
        }
    }

    async Task RoleResult(WorldPlayer p, string? error, string action, string target, string details, string done)
    {
        if (error is null)
            await Audit(p, action, target, details);
        p.Link.Send(new AdminReply(error is null, [error ?? done]));
        await SendRoles(p);
    }

    async Task SendRoles(WorldPlayer p)
    {
        var counts = await roles.Counts();
        p.Link.Send(new AdminRoles(
            [.. roles.All().Select(r => new RoleRow(r.Name, r.Rank, r.Builtin, r.All, counts.GetValueOrDefault(r.Name.ToLowerInvariant()), r.Beacon))],
            [.. Permissions.Catalog.Select(c => new PermissionRow(c.Key, c.Description))]));
    }

    static string? MutedText(DateTime? until) => until is { } m && m > DateTime.UtcNow ? m.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : null;

    static string? BannedText(DateTime? until) =>
        until is { } b && b > DateTime.UtcNow ? b == DateTime.MaxValue ? "permanently" : b.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : null;

    /// <summary>Sends the player editor everything about a character; false when there's none by that name.</summary>
    async Task<bool> SendDetail(WorldPlayer p, string name)
    {
        if (await Find(name) is not { } who)
            return false;
        var row = who.Row ?? await accounts.CharacterNamed(who.Character);
        var location = who.Online?.Location ?? new NetLocation(row?.Zone ?? 0, row?.X ?? 0, row?.Y ?? 0, row?.Facing ?? 0);
        var state = who.Online is { } online ? world.StateJsonOf(online) : row?.StateJson;
        p.Link.Send(new AdminPlayerDetail(who.Character, who.Account.Name, who.Role.Name, who.Online is not null, location,
            MutedText(who.Online?.MutedUntil ?? who.Account.MutedUntil), BannedText(who.Account.BannedUntil), who.Account.BanReason,
            state, who.Online is not null ? "now" : row?.LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? ""));
        return true;
    }

    /// <summary>Gives the account behind <paramref name="name"/> (a character or account name) the role <paramref name="roleName"/>.</summary>
    async Task<(bool, string)> GrantRole(WorldPlayer p, string name, string roleName)
    {
        if (roles.Find(roleName) is not { } role)
            return (false, $"There's no role {roleName} (roles: {string.Join(", ", roles.All().Select(r => r.Name))}).");
        var account = await accounts.ByName(name) ?? (await Find(name))?.Account;
        if (account is null)
            return (false, $"No account or character named {name}.");
        var current = roles.Get(account.Role);
        if (!Permissions.CanGrant(RoleOf(p), current, role))
        {
            await Audit(p, "grant-denied", account.Name, $"{current.Name} → {role.Name}");
            return (false, $"A {RoleOf(p).Name} can't make a {current.Name} into a {role.Name}.");
        }
        await accounts.SetRole(account.Name, role.Name);
        await Audit(p, "grant", account.Name, $"{current.Name} → {role.Name}");
        // Takes effect at once for them if they're online (out of Admin Mode if they're no longer staff).
        foreach (var online in world.Players().Where(o => o.AccountId == account.Id))
            world.SetRole(online, role.Name);
        return (true, $"{account.Name} is now {role.Name}.");
    }

    /// <summary>Bans (minutes, or "perm") or, with "off", unbans the account behind a character.</summary>
    async Task<(bool, string)> Ban(WorldPlayer p, string name, string length, string reason)
    {
        if (await Find(name) is not { } who)
            return (false, $"There's no character {name}.");
        if (who.Role.Rank >= RoleOf(p).Rank && who.Account.Id != p.AccountId)
            return (false, $"You can't ban a {who.Role.Name}.");
        if (length == "off")
        {
            await accounts.SetBan(who.Account.Id, null, null);
            await Audit(p, "unban", who.Account.Name, "");
            return (true, $"{who.Account.Name} isn't banned any more.");
        }
        if (reason.Length == 0)
            return (false, "A ban needs a reason.");
        DateTime until;
        if (length.Equals("perm", StringComparison.OrdinalIgnoreCase))
            until = DateTime.MaxValue;
        else if (int.TryParse(length, out var minutes) && minutes is > 0 and <= 60 * 24 * 365)
            until = DateTime.UtcNow.AddMinutes(minutes);
        else
            return (false, "Ban for a number of minutes (up to a year) or \"perm\".");
        await accounts.SetBan(who.Account.Id, until, reason);
        await Audit(p, "ban", who.Account.Name, $"{(until == DateTime.MaxValue ? "permanent" : length + " min")}: {reason}");
        foreach (var online in world.Players().Where(o => o.AccountId == who.Account.Id))
            world.Kick(online, $"You were banned{(until == DateTime.MaxValue ? "" : $" until {until.ToLocalTime():yyyy-MM-dd HH:mm}")}: {reason}");
        return (true, $"Banned {who.Account.Name}{(until == DateTime.MaxValue ? " permanently" : $" for {length} minutes")}.");
    }

    /// <summary>Money, items, healing and Pokémon: applied to the character's game wherever it is, recorded and audited.</summary>
    async Task EditGame(WorldPlayer p, AdminAction a, string reason)
    {
        void Reply(bool ok, params string[] lines) => p.Link.Send(new AdminReply(ok, lines));
        if (rules is null)
        {
            Reply(false, "This server keeps no game state (its data pack has no battle data).");
            return;
        }
        if (reason.Length == 0)
        {
            Reply(false, "Changes to a player's game need a reason (it goes in the audit log).");
            return;
        }
        if (await Find(a.Target) is not { } who)
        {
            Reply(false, $"There's no character {a.Target}.");
            return;
        }
        if (who.Role.Rank >= RoleOf(p).Rank && who.Account.Id != p.AccountId)
        {
            Reply(false, $"You can't change a {who.Role.Name}'s game.");
            return;
        }
        var data = rules.Data;
        string details = "";
        string? Apply(GameState s)
        {
            int Int(string key, int fallback) => int.TryParse(Arg(a, key), out var v) ? v : fallback;
            switch (a.Verb)
            {
                case "money":
                {
                    var amount = Int("amount", -1);
                    if (amount is < 0 or > MaxMoney)
                        return $"Money is from 0 to {MaxMoney:N0}.";
                    details = $"money ₽{s.Progress.Money:N0} → ₽{amount:N0}";
                    s.Progress.Money = amount;
                    return null;
                }
                case "give-item" or "take-item":
                {
                    var item = Int("item", -1);
                    var count = Int("count", 1);
                    if (item <= 0 || data.ItemName(item).StartsWith("Item ", StringComparison.Ordinal) && item > 0 && !s.Bag.Items.Any(i => i.Item == item) && a.Verb == "take-item")
                        return $"There's no item #{item}.";
                    if (count is < 1 or > 999)
                        return "Give or take 1 to 999 at a time.";
                    if (a.Verb == "give-item")
                    {
                        s.Bag.Add(item, count);
                        details = $"gave {data.ItemName(item)} ×{count}";
                    }
                    else
                    {
                        var has = s.Bag.Count(item);
                        if (has == 0)
                            return $"They have no {data.ItemName(item)}.";
                        var taken = Math.Min(has, count);
                        for (var i = 0; i < taken; i++)
                            s.Bag.Take(item);
                        details = $"took {data.ItemName(item)} ×{taken}";
                    }
                    return null;
                }
                case "heal":
                    foreach (var mon in s.Party)
                    {
                        mon.Hp = mon.MaxHp;
                        mon.Status = Core.Data.Ailment.None;
                        mon.SleepTurns = 0;
                        foreach (var m in mon.Moves)
                            m.Pp = m.MaxPp;
                    }
                    details = "healed the party";
                    return null;
                case "pokemon-give":
                {
                    var species = Int("species", 0);
                    var level = Int("level", 5);
                    if (!data.HasSpecies(species))
                        return $"There's no species #{species}.";
                    if (level is < 1 or > 100)
                        return "The level must be from 1 to 100.";
                    var mon = Pokemon.Create(data, species, level, new Random());
                    mon.AdminOrigin = true;
                    var where = s.Party.Count < GameState.PartySize ? "party" : "PC";
                    if (where == "party")
                        s.Party.Add(mon);
                    else if (s.Box.Count < GameState.BoxCapacity)
                        s.Box.Add(mon);
                    else
                        return "Their party and PC are full.";
                    details = $"gave {mon.Name} Lv{level} ({where})";
                    return null;
                }
                case "pokemon-save" or "pokemon-release":
                {
                    var box = Arg(a, "where") == "box";
                    var list = box ? s.Box : s.Party;
                    var index = Int("index", -1);
                    if (index < 0 || index >= list.Count)
                        return "That Pokémon isn't there any more.";
                    if (a.Verb == "pokemon-release")
                    {
                        if (!box && s.Party.Count == 1)
                            return "That's their only Pokémon in the party.";
                        details = $"released {list[index].Name} Lv{list[index].Level} ({(box ? "PC" : "party")} {index + 1})";
                        list.RemoveAt(index);
                        return null;
                    }
                    PokemonSave? save;
                    try
                    {
                        save = SaveGame.PokemonFromJson(Arg(a, "pokemon"));
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        save = null;
                    }
                    if (save is null)
                        return "That Pokémon couldn't be read.";
                    if (data.HasSpecies(save.Species))
                    {
                        // The level decides the EXP (the editor only sets the level).
                        var growth = data.Species(save.Species).GrowthRate;
                        var level = Math.Clamp(save.Level, 1, 100);
                        if (save.Exp < ExpCurve.ForLevel(growth, level) || level < 100 && save.Exp >= ExpCurve.ForLevel(growth, level + 1))
                            save = save with { Exp = ExpCurve.ForLevel(growth, level) };
                    }
                    var issues = Legality.Check(save, data);
                    if (issues.Count > 0 && !RoleOf(p).Has(Permissions.PokemonIllegal))
                        return "Not saved — this Pokémon couldn't be obtained normally:\n" + string.Join("\n", issues);
                    if (Pokemon.FromSave(data, save) is not { } edited)
                        return "That Pokémon couldn't be rebuilt.";
                    edited.AdminOrigin = true;
                    var old = list[index];
                    list[index] = edited;
                    details = $"edited {old.Name} Lv{old.Level} → {edited.Name} Lv{edited.Level}"
                        + (issues.Count > 0 ? $" (saved although: {string.Join("; ", issues)})" : "");
                    return null;
                }
            }
            return "Unknown change.";
        }

        string? before, after, error;
        if (who.Online is { } online)
            (before, after, error) = world.EditState(online, Apply);
        else
        {
            var state = rules.Load(who.Row!.StateJson);
            before = rules.Json(state);
            error = Apply(state);
            after = error is null ? rules.Json(state) : null;
            if (after is not null)
                await accounts.SaveState(who.CharacterId, after);
        }
        if (error is not null)
        {
            Reply(false, [.. error.Split('\n')]);
            return;
        }
        var change = await accounts.RecordChange(who.CharacterId, who.Character, p.AccountName, before!, after!);
        await Audit(p, a.Verb, who.Character, $"{details} — {reason}", change);
        Reply(true, $"{who.Character}: {details}. (change {change}; revert from the Audit tab)");
        await SendDetail(p, who.Character);
    }

    /// <summary>
    /// Puts a character's game back to before change <paramref name="id"/>. If the game changed since, it says so and
    /// only goes ahead with <paramref name="force"/> (which also undoes those later changes).
    /// </summary>
    async Task<(bool, IReadOnlyList<string>)> Revert(WorldPlayer p, long id, bool force)
    {
        if (rules is null || await accounts.Change(id) is not { } change)
            return (false, [$"There's no change {id}."]);
        if (change.Reverted)
            return (false, [$"Change {id} was already reverted."]);
        var online = world.Players().FirstOrDefault(o => o.CharacterId == change.CharacterId);
        var current = online is not null ? world.StateJsonOf(online) : (await accounts.CharacterNamed(change.Character))?.StateJson;
        if (current is null)
            return (false, [$"{change.Character} has no game to revert."]);
        if (SaveGame.Comparable(current) != SaveGame.Comparable(change.After) && !force)
            return (false, [$"{change.Character}'s game changed since change {id} (they played, or another edit came after).",
                "Reverting now also undoes everything since then. Revert with force to go ahead."]);
        var state = rules.Load(change.Before);
        if (online is not null)
        {
            if (world.ReplaceState(online, state) is { } error)
                return (false, [error]);
        }
        else
            await accounts.SaveState(change.CharacterId, rules.Json(state));
        await accounts.MarkReverted(id);
        var undo = await accounts.RecordChange(change.CharacterId, change.Character, p.AccountName, current, rules.Json(state));
        await Audit(p, "revert", change.Character, $"reverted change {id}{(force ? " (forced)" : "")}", undo);
        return (true, [$"Reverted change {id} on {change.Character}. (This revert is change {undo}.)"]);
    }
}
