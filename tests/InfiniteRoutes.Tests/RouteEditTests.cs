using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Extractor.Extraction;
using InfiniteRoutes.Server.Accounts;
using InfiniteRoutes.Server.Admin;
using Microsoft.Extensions.DependencyInjection;

namespace InfiniteRoutes.Tests;

/// <summary>The route editor's permission: Owners have it, other roles only when an Owner grants it.</summary>
public class RouteEditTests
{
    static string Route(int node, string name = "Misty Hollow")
    {
        var kinds = Enumerable.Repeat(TileKind.Tree, 32 * 32).ToArray();
        for (var x = 0; x < 32; x++)
            kinds[10 * 32 + x] = kinds[11 * 32 + x] = TileKind.Path;
        return RouteFiles.ToJson(RouteFile.From(node, name, 6, 32, 32, kinds));
    }

    static AdminAction Save(string json) => new("route-save", "", new Dictionary<string, string> { ["json"] = json });

    static async Task<TestClient> Join(TestServer server, AccountService accounts, string name, Role role, bool elevate = true)
    {
        var (token, character) = await server.NewPlayer(name);
        await accounts.SetRole(name, role);
        var client = await TestClient.Connect(server.GamePort);
        await client.Send(new Hello(Protocol.Version, token, character));
        await client.Expect<Welcome>();
        if (elevate && role != Role.Player)
        {
            await client.Send(new Elevate("password123", ""));
            Assert.True((await client.Expect<AdminReply>()).Ok);
        }
        return client;
    }

    [Fact]
    public async Task Only_owners_can_save_routes_until_a_role_is_given_the_permission()
    {
        await using var server = await TestServer.Start(channels: 1, extra: "[admin]\nrequire_2fa = false", rules: AdminChatTests.Rules());
        var accounts = server.App.Services.GetRequiredService<AccountService>();
        await using var owner = await Join(server, accounts, "Olga", Role.Owner);
        await using var admin = await Join(server, accounts, "Adam", Role.Admin);
        await using var player = await Join(server, accounts, "Pia", Role.Player);

        // No built-in role but Owner starts with it — not even Admin — and it's in the catalogue the Roles tab shows.
        Assert.DoesNotContain(Permissions.RouteEdit, Permissions.Defaults(Role.Admin));
        Assert.Contains(Permissions.Catalog, c => c.Key == Permissions.RouteEdit);
        Assert.Contains(Permissions.RouteEdit, (await owner.Expect<AdminStatus>(s => s.Elevated)).Permissions);

        // An Admin is refused, and nothing is saved or sent.
        await admin.Send(Save(Route(5)));
        var refused = await admin.Expect<AdminReply>();
        Assert.False(refused.Ok);
        Assert.Contains("can't do that", refused.Lines[0]);

        // The Owner saves: the answer comes back, and every player online is sent the route.
        await owner.Send(Save(Route(5)));
        var saved = await owner.Expect<AdminReply>(r => r.Topic == "route");
        Assert.True(saved.Ok, saved.Lines[0]);
        foreach (var client in new[] { owner, admin, player })
        {
            var change = await client.Expect<RouteChanged>();
            Assert.Equal(5, change.Node);
            Assert.Equal("Misty Hollow", RouteFiles.FromJson(change.File)!.Name);
        }

        // The Owner gives Admins the permission; now the Admin can save, and delete.
        await owner.Send(new AdminAction("role-perm", "Admin", new Dictionary<string, string> { ["permission"] = Permissions.RouteEdit, ["on"] = "yes" }));
        Assert.Contains(Permissions.RouteEdit, (await admin.Expect<AdminStatus>(s => s.Permissions.Contains(Permissions.RouteEdit))).Permissions);
        await admin.Send(Save(Route(6, "Second Route")));
        Assert.True((await admin.Expect<AdminReply>(r => r.Topic == "route")).Ok);
        Assert.Equal(6, (await player.Expect<RouteChanged>()).Node);
        await admin.Send(new AdminAction("route-delete", "5", new Dictionary<string, string>()));
        Assert.True((await admin.Expect<AdminReply>(r => r.Topic == "route")).Ok);
        Assert.Equal((5, ""), await player.Expect<RouteChanged>(c => c.Node == 5) is var gone ? (gone.Node, gone.File) : default);

        // Someone joining later is sent the routes that exist.
        await using var late = await Join(server, accounts, "Lena", Role.Player);
        var set = await late.Expect<RouteSet>();
        Assert.Equal(["Second Route"], set.Files.Select(f => RouteFiles.FromJson(f)!.Name));
    }

    [Fact]
    public async Task Saving_needs_admin_mode_and_a_real_route()
    {
        await using var server = await TestServer.Start(channels: 1, extra: "[admin]\nrequire_2fa = false", rules: AdminChatTests.Rules());
        var accounts = server.App.Services.GetRequiredService<AccountService>();
        await using var owner = await Join(server, accounts, "Olga", Role.Owner, elevate: false);

        await owner.Send(Save(Route(1)));
        Assert.Contains("Admin Mode", (await owner.Expect<AdminReply>()).Lines[0]);

        await owner.Send(new Elevate("password123", ""));
        Assert.True((await owner.Expect<AdminReply>()).Ok);
        await owner.Send(Save("{ not a route"));
        var reply = await owner.Expect<AdminReply>(r => r.Topic == "route");
        Assert.False(reply.Ok);
    }
}

/// <summary>HM obstacles on hand-made routes: in the way until a player with enough badges (or staff) clears them, for themselves.</summary>
public class ObstacleTests
{
    [Fact]
    public async Task A_small_tree_blocks_until_cleared_and_only_for_whoever_cleared_it()
    {
        // A small tree right of the start (2, 2).
        var tree = new NpcInfo(UnchartedRoutes.ObstacleIdBase, UnchartedRoutes.CutSprite, 3, 2, 0, Facing.Down, 0, 0, 0, UnchartedRoutes.CutScript, 0);
        await using var server = await TestServer.Start(channels: 1, extra: "[admin]\nrequire_2fa = false", rules: AdminChatTests.Rules(),
            world: new FakeWorld { Obstacle = tree });
        var accounts = server.App.Services.GetRequiredService<AccountService>();

        async Task<TestClient> Join(string name, Role role, bool elevate)
        {
            var (token, character) = await server.NewPlayer(name);
            await accounts.SetRole(name, role);
            var client = await TestClient.Connect(server.GamePort);
            await client.Send(new Hello(Protocol.Version, token, character));
            await client.Expect<Welcome>();
            if (elevate)
            {
                await client.Send(new Elevate("password123", ""));
                Assert.True((await client.Expect<AdminReply>()).Ok);
            }
            return client;
        }
        await using var pia = await Join("Pia", Role.Player, elevate: false);
        await using var olga = await Join("Olga", Role.Owner, elevate: true);

        // Without a Gym Badge: told why, and still in the way.
        await pia.Send(new NpcAction(1, tree.Id));
        var refused = await pia.Expect<NpcResult>();
        Assert.Equal((int)InfiniteRoutes.Core.Player.NpcDealKind.None, refused.Kind);
        Assert.Contains("Gym Badge", refused.Text);
        await pia.Send(new Step(1, 1, 0, false));
        Assert.False((await pia.Expect<StepResult>(r => r.Seq == 1)).Accepted);

        // Staff in Admin Mode clear it — for themselves: they can walk on, Pia still can't.
        await olga.Send(new Step(1, 1, 0, false));
        Assert.False((await olga.Expect<StepResult>(r => r.Seq == 1)).Accepted);
        await olga.Send(new NpcAction(1, tree.Id));
        Assert.Equal((int)InfiniteRoutes.Core.Player.NpcDealKind.Cleared, (await olga.Expect<NpcResult>()).Kind);
        await Task.Delay(300);
        await olga.Send(new Step(2, 1, 0, false));
        Assert.True((await olga.Expect<StepResult>(r => r.Seq == 2)).Accepted);
        await Task.Delay(300);
        await pia.Send(new Step(2, 1, 0, false));
        Assert.False((await pia.Expect<StepResult>(r => r.Seq == 2)).Accepted);
    }
}

/// <summary>A trainer made for a route battles like the ROM's, with their own party and prize.</summary>
public class RouteTrainerTests
{
    [Fact]
    public async Task Talking_to_a_route_trainer_starts_a_battle_with_their_party()
    {
        var id = UnchartedRoutes.TrainerId(7, 0);
        var made = new InfiniteRoutes.Core.Data.TrainerInfo(id, "Ava", 4, "Lass", 0, [], 0, 0,
            [new InfiniteRoutes.Core.Data.TrainerPokemon(InfiniteRoutes.Core.Player.StarterKit.Species, 0, 3, 10, 0, 0, null)], Prize: 1200);
        var person = new NpcInfo(50, 12, 3, 3, 0, Facing.Up, 0, 0, 0, 0, 0, 0, new InfiniteRoutes.Extractor.Gen5.NpcTalk(["Let's battle!"], id));
        await using var server = await TestServer.Start(channels: 1, rules: AdminChatTests.Rules(), world: new FakeWorld { Made = made, MadePerson = person });
        var (token, character) = await server.NewPlayer("Pia");
        await using var pia = await TestClient.Connect(server.GamePort);
        await pia.Send(new Hello(Protocol.Version, token, character));
        await pia.Expect<Welcome>();

        await pia.Send(new NpcAction(1, person.Id));
        var start = await pia.Expect<BattleStart>();
        Assert.Equal(id, start.Trainer);
    }
}

/// <summary>Trainers who spot you challenge staff in Admin Mode too (unless they're invisible); items work from the Bag.</summary>
public class SpottedAndBagTests
{
    static async Task<(TestServer Server, TestClient Client)> Join(Role role, bool elevate, InfiniteRoutes.Server.World.IWorldData? world = null)
    {
        var server = await TestServer.Start(channels: 1, extra: "[admin]\nrequire_2fa = false", rules: AdminChatTests.Rules(), world: world);
        var accounts = server.App.Services.GetRequiredService<AccountService>();
        var (token, character) = await server.NewPlayer("Olga");
        await accounts.SetRole("Olga", role);
        var client = await TestClient.Connect(server.GamePort);
        await client.Send(new Hello(Protocol.Version, token, character));
        await client.Expect<Welcome>();
        if (elevate)
        {
            await client.Send(new Elevate("password123", ""));
            Assert.True((await client.Expect<AdminReply>()).Ok);
        }
        return (server, client);
    }

    [Fact]
    public async Task A_trainer_who_spots_staff_in_Admin_Mode_starts_the_battle()
    {
        var id = UnchartedRoutes.TrainerId(7, 0);
        var made = new InfiniteRoutes.Core.Data.TrainerInfo(id, "Ava", 4, "Lass", 0, [], 0, 0,
            [new InfiniteRoutes.Core.Data.TrainerPokemon(InfiniteRoutes.Core.Player.StarterKit.Species, 0, 3, 10, 0, 0, null)], Prize: 1200);
        // Two tiles right of the start (2, 2), facing left, seeing 3 tiles.
        var person = new NpcInfo(50, 12, 4, 2, 0, Facing.Left, 0, 0, 0, 0, 0, 3, new InfiniteRoutes.Extractor.Gen5.NpcTalk(["Let's battle!"], id));
        var (server, olga) = await Join(Role.Owner, elevate: true, new FakeWorld { Made = made, MadePerson = person });
        await using var _s = server;
        await using var _o = olga;

        await olga.Send(new NpcAction(1, person.Id, Spotted: true));
        Assert.Equal(id, (await olga.Expect<BattleStart>()).Trainer);
    }

    [Fact]
    public async Task Items_from_the_Bag_are_given_to_hold_and_medicine_with_no_effect_is_kept()
    {
        var (server, pia) = await Join(Role.Player, elevate: false);
        await using var _s = server;
        await using var _p = pia;
        var start = InfiniteRoutes.Core.Player.SaveGame.FromJson((await pia.Expect<StateSnapshot>()).Json)!;
        var potions = start.Bag.Single(b => b.Item == InfiniteRoutes.Core.Battle.ItemEffect.Potion).Count;

        // A Potion on a Pokémon at full health does nothing and isn't used up.
        await pia.Send(new UseItem(InfiniteRoutes.Core.Battle.ItemEffect.Potion, 0));
        Assert.Contains("won't have any effect", (await pia.Expect<Notice>()).Text);

        // Given to hold: it leaves the Bag and the lead holds it.
        await pia.Send(new UseItem(InfiniteRoutes.Core.Battle.ItemEffect.Potion, 0, Hold: true));
        Assert.Contains("to hold", (await pia.Expect<Notice>()).Text);
        var after = InfiniteRoutes.Core.Player.SaveGame.FromJson((await pia.Expect<StateSnapshot>()).Json)!;
        Assert.Equal(potions - 1, after.Bag.Single(b => b.Item == InfiniteRoutes.Core.Battle.ItemEffect.Potion).Count);
        Assert.Equal(InfiniteRoutes.Core.Battle.ItemEffect.Potion, after.Party[0].HeldItem);

        // Something it doesn't have.
        await pia.Send(new UseItem(InfiniteRoutes.Core.Battle.ItemEffect.Repel));
        Assert.Contains("You have no", (await pia.Expect<Notice>()).Text);
    }
}
