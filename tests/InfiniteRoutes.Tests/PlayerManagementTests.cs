using System.Net.Http.Json;
using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;
using InfiniteRoutes.Server.Accounts;
using InfiniteRoutes.Server.Admin;
using Microsoft.Extensions.DependencyInjection;

namespace InfiniteRoutes.Tests;

public class PlayerManagementTests
{
    /// <summary>A server with game data and no 2FA requirement, and a staff client of <paramref name="role"/> in Admin Mode.</summary>
    static async Task<(TestServer Server, TestClient Staff, AccountService Accounts)> Staff(Role role, string name = "Sage")
    {
        var server = await TestServer.Start(channels: 1, extra: "[admin]\nrequire_2fa = false", rules: AdminChatTests.Rules());
        var accounts = server.App.Services.GetRequiredService<AccountService>();
        var (token, character) = await server.NewPlayer(name);
        await accounts.SetRole(name, role);
        var staff = await TestClient.Connect(server.GamePort);
        await staff.Send(new Hello(Protocol.Version, token, character));
        await staff.Expect<Welcome>();
        await staff.Send(new Elevate("password123", ""));
        Assert.True((await staff.Expect<AdminReply>()).Ok);
        return (server, staff, accounts);
    }

    static AdminAction Act(string verb, string target, string reason = "", params (string, string)[] args) =>
        new(verb, target, args.ToDictionary(a => a.Item1, a => a.Item2), reason);

    static async Task<SaveGame> StateOf(TestClient staff, string character)
    {
        staff.Drain<AdminPlayerDetail>();   // edits send details too; ask for the current one
        await staff.Send(Act("detail", character));
        var detail = await staff.Expect<AdminPlayerDetail>(d => d.Character == character);
        return SaveGame.FromJson(detail.StateJson!)!;
    }

    [Fact]
    public async Task Owners_manage_roles_and_permission_changes_apply_at_once()
    {
        var (server, owner, _) = await Staff(Role.Owner, "Olga");
        await using var _s = server;
        await using var _o = owner;
        var accounts = server.App.Services.GetRequiredService<AccountService>();

        await owner.Send(Act("roles", ""));
        var list = await owner.Expect<AdminRoles>();
        Assert.Equal(["Player", "Helper", "Moderator", "GameMaster", "Admin", "Owner"], list.Roles.Select(r => r.Name));
        Assert.Contains(list.Catalog, c => c.Key == Permissions.PokemonIllegal);

        // A custom role between Helper and Moderator, copying Helper, plus one more permission.
        await owner.Send(Act("role-create", "Greeter", "", ("rank", "10")));
        Assert.Contains("already has rank 10", (await owner.Expect<AdminReply>()).Lines[0]);
        await owner.Send(Act("role-create", "Greeter", "", ("rank", "15"), ("copy", "Helper")));
        Assert.True((await owner.Expect<AdminReply>(r => r.Ok)).Ok);
        var greeter = (await owner.Expect<AdminRoles>(r => r.Roles.Any(x => x.Name == "Greeter"))).Roles.Single(r => r.Name == "Greeter");
        Assert.Equal((15, false), (greeter.Rank, greeter.Builtin));
        Assert.Equal([Permissions.Mute, Permissions.Who], greeter.Permissions);

        // Give someone the role while they're online in Admin Mode; adding a permission reaches them at once.
        var (token, character) = await server.NewPlayer("Gabe");
        await owner.Send(Act("set-role", "Gabe", "", ("role", "Greeter")));
        Assert.True((await owner.Expect<AdminReply>(r => r.Ok && r.Lines[0].Contains("Gabe"))).Ok);
        await using var gabe = await TestClient.Connect(server.GamePort);
        await gabe.Send(new Hello(Protocol.Version, token, character));
        await gabe.Expect<Welcome>();
        Assert.Equal("Greeter", (await gabe.Expect<AdminStatus>()).Role);
        await gabe.Send(new Elevate("password123", ""));
        await gabe.Expect<AdminReply>(r => r.Ok);
        await owner.Send(Act("role-perm", "Greeter", "", ("permission", Permissions.Teleport), ("on", "yes")));
        Assert.Contains(Permissions.Teleport, (await gabe.Expect<AdminStatus>(s => s.Permissions.Contains(Permissions.Teleport))).Permissions);
        await gabe.Send(new AdminCommand("tp 389 2 2"));
        Assert.True((await gabe.Expect<AdminReply>(r => r.Lines[0].Contains("eleport") || r.Lines[0].Contains("zone"))) is not null);

        // Non-owners can't edit roles; built-in roles and roles in use can't be deleted; Owner keeps everything.
        await gabe.Send(Act("role-perm", "Greeter", "", ("permission", Permissions.Ban), ("on", "yes")));
        Assert.Contains("Only Owners", (await gabe.Expect<AdminReply>(r => !r.Ok)).Lines[0]);
        await owner.Send(Act("role-delete", "Greeter"));
        Assert.Contains("account(s) have Greeter", (await owner.Expect<AdminReply>(r => !r.Ok)).Lines[0]);
        await owner.Send(Act("role-delete", "Moderator"));
        Assert.Contains("built in", (await owner.Expect<AdminReply>(r => !r.Ok)).Lines[0]);
        await owner.Send(Act("role-perm", "Owner", "", ("permission", Permissions.Ban), ("on", "no")));
        Assert.Contains("every permission", (await owner.Expect<AdminReply>(r => !r.Ok)).Lines[0]);
        await owner.Send(Act("role-rank", "Greeter", "", ("rank", "25")));
        Assert.True((await owner.Expect<AdminReply>(r => r.Lines[0].Contains("rank 25"))).Ok);

        // Taking the role away leaves Admin Mode.
        await owner.Send(Act("set-role", "Gabe", "", ("role", "Player")));
        Assert.False((await gabe.Expect<AdminStatus>(s => !s.Elevated)).Elevated);
        await owner.Send(Act("role-delete", "Greeter"));
        Assert.True((await owner.Expect<AdminReply>(r => r.Lines[0].Contains("Deleted"))).Ok);

        var log = await accounts.RecentAudit(50);
        Assert.Contains(log, e => e is { Action: "role-create", Target: "Greeter" });
        Assert.Contains(log, e => e is { Action: "role-permission", Details: "+world.teleport" });
    }

    [Fact]
    public async Task Bans_refuse_logins_and_disconnect()
    {
        var (server, mod, _) = await Staff(Role.Moderator, "Mona");
        await using var _s = server;
        await using var _m = mod;
        var (token, character) = await server.NewPlayer("Troll");
        await using var troll = await TestClient.Connect(server.GamePort);
        await troll.Send(new Hello(Protocol.Version, token, character));
        await troll.Expect<Welcome>();

        await mod.Send(Act("ban", "Troll", "", ("minutes", "60")));
        Assert.Contains("reason", (await mod.Expect<AdminReply>(r => !r.Ok)).Lines[0]);
        await mod.Send(Act("ban", "Troll", "griefing", ("minutes", "60")));
        Assert.True((await mod.Expect<AdminReply>(r => r.Ok)).Ok);
        Assert.Contains("banned", (await troll.Expect<Notice>(n => n.Text.Contains("banned"))).Text);
        var login = await server.Http.PostAsJsonAsync("/api/login", new { name = "Troll", password = "password123" });
        Assert.Equal(403, (int)login.StatusCode);
        Assert.Contains("griefing", await login.Content.ReadAsStringAsync());
        var detail = await mod.Expect<AdminPlayerDetail>(d => d.BannedUntil is not null);
        Assert.Equal("griefing", detail.BanReason);

        // A moderator can't ban staff at their rank or above.
        await server.NewPlayer("Max");
        await server.App.Services.GetRequiredService<AccountService>().SetRole("Max", Role.Admin);
        await mod.Send(Act("ban", "MaxC", "no", ("minutes", "5")));
        Assert.Contains("can't ban", (await mod.Expect<AdminReply>(r => !r.Ok)).Lines[0]);

        await mod.Send(Act("unban", "TrollC"));
        Assert.True((await mod.Expect<AdminReply>(r => r.Ok)).Ok);
        Assert.True((await server.Http.PostAsJsonAsync("/api/login", new { name = "Troll", password = "password123" })).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Game_edits_need_a_reason_check_legality_and_can_be_reverted()
    {
        var (server, gm, accounts) = await Staff(Role.GameMaster, "Gil");
        await using var _s = server;
        await using var _g = gm;
        var (token, character) = await server.NewPlayer("Nate");
        await using var nate = await TestClient.Connect(server.GamePort);
        await nate.Send(new Hello(Protocol.Version, token, character));
        await nate.Expect<Welcome>();
        await nate.Expect<StateSnapshot>();

        // Money: a reason is needed; the player's client gets the new state.
        await gm.Send(Act("money", "NateC", "", ("amount", "5000")));
        Assert.Contains("reason", (await gm.Expect<AdminReply>(r => !r.Ok)).Lines[0]);
        await gm.Send(Act("money", "NateC", "event prize", ("amount", "5000")));
        Assert.True((await gm.Expect<AdminReply>(r => r.Ok)).Ok);
        Assert.Equal(5000, SaveGame.FromJson((await nate.Expect<StateSnapshot>()).Json)!.Progress.Money);

        // Items and a Pokémon given (flagged as admin-made).
        await gm.Send(Act("give-item", "NateC", "event prize", ("item", ItemEffect.Potion.ToString()), ("count", "3")));
        await gm.Expect<AdminReply>(r => r.Ok);
        await gm.Send(Act("pokemon-give", "NateC", "event prize", ("species", StarterKit.Species.ToString()), ("level", "20")));
        await gm.Expect<AdminReply>(r => r.Ok);
        var state = await StateOf(gm, "NateC");
        Assert.Equal(8, state.Bag.Single(i => i.Item == ItemEffect.Potion).Count);
        Assert.Equal([false, true], state.Party.Select(m => m.AdminOrigin));

        // An edit with an unlearnable move is blocked for a Game Master…
        var edited = state.Party[1] with { Level = 30, Moves = [new MoveSave(99, 10)] };
        await gm.Send(Act("pokemon-save", "NateC", "fix", ("where", "party"), ("index", "1"), ("pokemon", SaveGame.PokemonJson(edited))));
        var blocked = await gm.Expect<AdminReply>(r => !r.Ok);
        Assert.Contains(blocked.Lines, l => l.Contains("No move #99") || l.Contains("There's no move #99"));
        // …a legal one is saved (level 30, EXP to match).
        await gm.Send(Act("pokemon-save", "NateC", "fix", ("where", "party"), ("index", "1"), ("pokemon", SaveGame.PokemonJson(state.Party[1] with { Level = 30 }))));
        Assert.True((await gm.Expect<AdminReply>(r => r.Ok)).Ok);
        Assert.Equal(30, (await StateOf(gm, "NateC")).Party[1].Level);

        // Revert the level change: the player hasn't played since, so it just goes back.
        var levelChange = (await accounts.RecentAudit(10)).First(e => e.Action == "pokemon-save").Change;
        Assert.True(levelChange > 0);
        await gm.Send(Act("revert", "", "", ("change", levelChange.ToString())));
        Assert.Contains("can't do that", (await gm.Expect<AdminReply>(r => !r.Ok)).Lines[0]);   // reverting is for Admins
        // Promote Gil (as an in-game grant does: the account and the live session).
        await accounts.SetRole("Gil", Role.Admin);
        var world = server.App.Services.GetRequiredService<InfiniteRoutes.Server.World.WorldService>();
        world.SetRole(world.Playing("GilC")!, "Admin");
        await gm.Send(new AdminCommand("revert " + levelChange));
        Assert.True((await gm.Expect<AdminReply>(r => r.Lines[0].StartsWith("Reverted"))).Ok);
        Assert.Equal(20, (await StateOf(gm, "NateC")).Party[1].Level);

        // Reverting the money after more changes warns first, then goes ahead with force.
        var moneyChange = (await accounts.RecentAudit(20)).Last(e => e.Action == "money").Change;
        await gm.Send(Act("revert", "", "", ("change", moneyChange.ToString())));
        Assert.Contains("changed since", (await gm.Expect<AdminReply>(r => !r.Ok)).Lines[0]);
        await gm.Send(Act("revert", "", "", ("change", moneyChange.ToString()), ("force", "yes")));
        Assert.True((await gm.Expect<AdminReply>(r => r.Ok)).Ok);
        var after = await StateOf(gm, "NateC");
        Assert.Equal((3000, 1), (after.Progress.Money, after.Party.Count));

        // As an Admin, an illegal edit can be saved — with a reason, and noted in the log.
        await nate.DisposeAsync();
        await Task.Delay(300);
        await gm.Send(Act("pokemon-give", "NateC", "test", ("species", StarterKit.Species.ToString()), ("level", "5")));
        await gm.Expect<AdminReply>(r => r.Ok);
        var offline = await StateOf(gm, "NateC");
        await gm.Send(Act("pokemon-save", "NateC", "special event", ("where", "party"), ("index", "1"),
            ("pokemon", SaveGame.PokemonJson(offline.Party[1] with { AbilitySlot = 2 }))));
        Assert.True((await gm.Expect<AdminReply>(r => r.Ok)).Ok);
        Assert.Contains(await accounts.RecentAudit(5), e => e.Details.Contains("saved although") && e.Details.Contains("Hidden abilities"));

        // Releasing the last party Pokémon is refused; releasing the second works (offline too).
        await gm.Send(Act("pokemon-release", "NateC", "cleanup", ("where", "party"), ("index", "1")));
        await gm.Expect<AdminReply>(r => r.Ok);
        await gm.Send(Act("pokemon-release", "NateC", "cleanup", ("where", "party"), ("index", "0")));
        Assert.Contains("only Pokémon", (await gm.Expect<AdminReply>(r => !r.Ok)).Lines[0]);
    }

    [Fact]
    public async Task Search_finds_offline_players_and_audit_rows_carry_their_change()
    {
        var (server, admin, _) = await Staff(Role.Admin, "Ada");
        await using var _s = server;
        await using var _a = admin;
        await server.NewPlayer("Bianca");
        await admin.Send(Act("search", "bian"));
        var rows = (await admin.Expect<AdminPlayers>()).Players;
        Assert.Contains(rows, r => r is { Name: "BiancaC", Account: "Bianca", Online: false });
        await admin.Send(Act("heal", "BiancaC", "test"));
        await admin.Expect<AdminReply>(r => r.Ok);
        await admin.Send(Act("audit", "", "", ("count", "5")));
        var audit = await admin.Expect<AdminAudit>();
        Assert.Contains(audit.Entries, e => e is { Action: "heal", Target: "BiancaC", Change: > 0 });
    }
}
