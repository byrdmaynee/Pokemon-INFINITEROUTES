using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;
using InfiniteRoutes.Server.World;
using Microsoft.Extensions.DependencyInjection;

namespace InfiniteRoutes.Tests;

/// <summary>Active Buddy: after 3 catches a party Pokémon can follow the player, and other players see it.</summary>
public class BuddyTests
{
    static GameState Game(out BattleData data)
    {
        var rules = AdminChatTests.Rules();
        data = rules.Data;
        var state = rules.NewGame();
        state.Party.Add(Pokemon.Create(data, StarterKit.Species, 7, new Random(3)));
        return state;
    }

    [Fact]
    public void A_buddy_needs_three_catches_and_follows_only_while_it_can()
    {
        var state = Game(out var data);
        state.Progress.Caught = 2;
        var (ok, message) = state.SetBuddy(1);
        Assert.False(ok);
        Assert.Contains("(2/3)", message);

        state.Progress.Caught = 3;
        Assert.True(state.SetBuddy(1).Ok);
        Assert.Same(state.Party[1], state.ActiveBuddy);

        // It hides while fainted and comes back when healed; it stops following when it leaves the party.
        state.Party[1].Hp = 0;
        Assert.Null(state.ActiveBuddy);
        Assert.False(state.SetBuddy(1).Ok);
        state.HealParty();
        Assert.NotNull(state.ActiveBuddy);
        Assert.True(state.Deposit(1).Ok);
        Assert.Null(state.ActiveBuddy);

        // Kept in the save by party position, after the lead changes too.
        state.Withdraw(0);
        Assert.True(state.SetBuddy(1).Ok);
        var buddy = state.Party[1];
        state.Party.RemoveAt(1);
        state.Party.Insert(0, buddy);
        var save = SaveGame.From(state, "white", DateTime.UtcNow);
        Assert.Equal(0, save.Buddy);
        Assert.Equal(buddy.Level, save.ActiveBuddy!.Level);
        var back = SaveGame.FromJson(save.ToJson())!.Restore(data, new QuestLog([]));
        Assert.Same(back.Party[0], back.ActiveBuddy);
        Assert.True(back.SetBuddy(-1).Ok);
        Assert.Null(back.ActiveBuddy);
    }

    [Fact]
    public async Task Other_players_see_a_buddy_appear_change_and_go()
    {
        await using var server = await TestServer.Start(channels: 1, rules: AdminChatTests.Rules());
        var world = server.App.Services.GetRequiredService<WorldService>();
        var (tokenA, charA) = await server.NewPlayer("Walker");
        var (tokenB, charB) = await server.NewPlayer("Watcher");
        await using var a = await TestClient.Connect(server.GamePort);
        await a.Send(new Hello(Protocol.Version, tokenA, charA));
        await a.Expect<Welcome>();

        // Not unlocked yet.
        await a.Send(new SetBuddy(0));
        Assert.Contains("Catch 3", (await a.Expect<Notice>()).Text);

        var me = world.Playing("WalkerC")!;
        world.EditState(me, s =>
        {
            s.Progress.Caught = 3;
            return null;
        });
        await a.Send(new SetBuddy(0));
        Assert.Contains("Active Buddy", (await a.Expect<Notice>()).Text);
        Assert.Equal(0, SaveGame.FromJson((await a.Expect<StateSnapshot>(s => SaveGame.FromJson(s.Json)!.Buddy == 0)).Json)!.Buddy);

        // Someone joining sees Walker with the buddy.
        await using var b = await TestClient.Connect(server.GamePort);
        await b.Send(new Hello(Protocol.Version, tokenB, charB));
        await b.Expect<Welcome>();
        var seen = (await b.Expect<PlayersAppeared>(p => p.Players.Any(x => x.Name == "WalkerC"))).Players.Single(x => x.Name == "WalkerC");
        Assert.Equal(StarterKit.Species, seen.Buddy);

        // Fainting puts it away for watchers; healing brings it back; putting it away hides it.
        world.EditState(me, s =>
        {
            s.Party[0].Hp = 0;
            return null;
        });
        Assert.Equal(0, (await b.Expect<BuddyChanged>(c => c.Player == seen.Id)).Species);
        world.EditState(me, s =>
        {
            s.HealParty();
            return null;
        });
        Assert.Equal(StarterKit.Species, (await b.Expect<BuddyChanged>(c => c.Species > 0)).Species);
        await a.Send(new SetBuddy(-1));
        Assert.Equal(0, (await b.Expect<BuddyChanged>(c => c.Species == 0)).Species);
    }
}
