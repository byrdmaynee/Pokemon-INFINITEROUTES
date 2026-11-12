using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;
using InfiniteRoutes.Server.Game;
using InfiniteRoutes.Server.World;
using Microsoft.Extensions.DependencyInjection;

namespace InfiniteRoutes.Tests;

/// <summary>
/// Battles online: the server rolls the encounter and plays every action; a client built the way the game builds it
/// replays each action with the server's seed and must end up exactly where the server is.
/// </summary>
public class BattleOnlineTests
{
    /// <summary>The test rules plus one encounter table (FakeWorld's zone 1 uses table 0): wild Snivy at Lv 2–3, every step in grass.</summary>
    static GameRules RulesWithGrass()
    {
        var rules = AdminChatTests.Rules();
        var slots = Enumerable.Repeat(new EncounterSlot(StarterKit.Species, 0, 2, 3), 12).ToList();
        var set = new EncounterSet(100, 0, 0, 0, 0, 0, 0, slots, [], [], [], [], [], []);
        return new GameRules(rules.Data, [], "white", [new EncounterTable(0, [set])]);
    }

    static async Task<(TestServer Server, TestClient Client, WorldService World, GameRules Rules)> Player(string name)
    {
        var rules = RulesWithGrass();
        var server = await TestServer.Start(channels: 1, rules: rules);
        var (token, character) = await server.NewPlayer(name);
        var client = await TestClient.Connect(server.GamePort);
        await client.Send(new Hello(Protocol.Version, token, character));
        await client.Expect<Welcome>();
        await client.Expect<StateSnapshot>();   // the game on joining
        return (server, client, server.App.Services.GetRequiredService<WorldService>(), rules);
    }

    /// <summary>The battle as the client builds it from what the server sent.</summary>
    static SingleBattle Mirror(GameRules rules, StateSnapshot snapshot, BattleStart start)
    {
        var state = SaveGame.FromJson(snapshot.Json)!.Restore(rules.Data, new QuestLog([]));
        var wild = Pokemon.FromSave(rules.Data, SaveGame.PokemonFromJson(start.Wild)!)!;
        var battle = new SingleBattle(rules.Data, state.Party, state.Bag, wild, new Random(start.Seed));
        battle.Start();
        return battle;
    }

    /// <summary>Sends an action, replays it on <paramref name="mirror"/> with the server's seed and checks both agree.</summary>
    static async Task Play(TestClient client, int battle, int seq, SingleBattle mirror, BattleInput input, Func<IReadOnlyList<BattleEvent>> replay)
    {
        await client.Send(input);
        var turn = await client.Expect<BattleTurn>(t => t.Seq == seq);
        Assert.True(turn.Accepted, turn.Reason);
        mirror.Reseed(turn.Seed);
        replay();
        Assert.Equal(turn.Digest, mirror.Digest());
    }

    [Fact]
    public async Task A_step_into_tall_grass_starts_a_battle_the_client_replays_exactly()
    {
        var (server, client, world, rules) = await Player("Grassy");
        await using var _s = server;
        await using var _c = client;

        // (3, 2) is tall grass in FakeWorld, and table 0 has a wild Pokémon on every step.
        await client.Send(new Step(1, 1, 0, false));
        Assert.True((await client.Expect<StepResult>(r => r.Seq == 1)).Accepted);
        var snapshot = await client.Expect<StateSnapshot>();
        var start = await client.Expect<BattleStart>();
        var mirror = Mirror(rules, snapshot, start);
        Assert.Equal(StarterKit.Species, mirror.Foe.Mon.Species.Id);
        Assert.InRange(mirror.Foe.Mon.Level, 2, 3);

        // No walking away mid-battle.
        await client.Send(new Step(2, 1, 0, false));
        Assert.False((await client.Expect<StepResult>(r => r.Seq == 2)).Accepted);

        var seq = 0;
        for (var i = 0; i < 80 && !mirror.Over; i++)
        {
            seq++;
            if (mirror.NeedsSwitch)
            {
                var next = Enumerable.Range(0, mirror.Party.Count).First(n => mirror.CanSendOut(n) && !mirror.IsOut(n));
                await Play(client, start.Battle, seq, mirror, new BattleInput(start.Battle, seq, "send-out", A: next), () => mirror.SendOut(next));
            }
            else
                await Play(client, start.Battle, seq, mirror, new BattleInput(start.Battle, seq, "turn", [new NetChoice("fight", 0)]),
                    () => mirror.Turn([new BattleChoice.Fight(0)]));
        }
        Assert.True(mirror.Over);
        Assert.Contains(mirror.Outcome, new[] { BattleOutcome.Won, BattleOutcome.Lost });

        // Nothing more can be done in it; then the client says it's shown the end.
        await client.Send(new BattleInput(start.Battle, ++seq, "turn", [new NetChoice("fight", 0)]));
        Assert.False((await client.Expect<BattleTurn>(t => t.Seq == seq)).Accepted);
        await client.Send(new BattleInput(start.Battle, seq, "end"));
        var over = await client.Expect<BattleOver>();
        Assert.Equal((int)mirror.Outcome, over.Outcome);
        var after = SaveGame.FromJson((await client.Expect<StateSnapshot>()).Json)!;
        if (mirror.Outcome == BattleOutcome.Won)
        {
            Assert.Equal(1, after.Progress.WildWins);
            Assert.Equal(mirror.Party[0].Hp, after.Party[0].Hp);
            Assert.Equal(mirror.Party[0].Exp, after.Party[0].Exp);
        }
        else
        {
            Assert.All(after.Party, p => Assert.Equal(Pokemon.FromSave(rules.Data, p)!.MaxHp, p.Hp));   // healed at home
            Assert.Contains(over.Lines, l => l.Contains("home"));
        }
        Assert.Null(world.BattleOf(world.Playing("GrassyC")!));
    }

    [Fact]
    public async Task A_catch_joins_the_party_with_its_nickname_and_is_saved()
    {
        var (server, client, world, rules) = await Player("Catcher");
        await using var _s = server;
        await using var _c = client;
        var me = world.Playing("CatcherC")!;
        world.EditState(me, s =>
        {
            s.Bag.Add(ItemEffect.MasterBall);
            return null;
        });
        await client.Expect<StateSnapshot>();
        Assert.True(world.StartWildBattle(me, Pokemon.Create(rules.Data, StarterKit.Species, 4, new Random(7))));
        var mirror = Mirror(rules, await client.Expect<StateSnapshot>(), await client.Expect<BattleStart>());
        var id = world.BattleOf(me)!.Id;

        await Play(client, id, 1, mirror, new BattleInput(id, 1, "turn", [new NetChoice("item", ItemEffect.MasterBall, -1)]),
            () => mirror.Turn([new BattleChoice.Item(ItemEffect.MasterBall, -1)]));
        Assert.Equal(BattleOutcome.Caught, mirror.Outcome);
        await client.Send(new BattleInput(id, 1, "nickname", Text: "Leafy\u0007"));
        await client.Send(new BattleInput(id, 1, "end"));
        Assert.Equal((int)BattleOutcome.Caught, (await client.Expect<BattleOver>()).Outcome);
        var after = SaveGame.FromJson((await client.Expect<StateSnapshot>()).Json)!;
        Assert.Equal(2, after.Party.Count);
        Assert.Equal("Leafy", after.Party[1].Nickname);
        Assert.Equal(1, after.Progress.Caught);
        Assert.Equal(0, after.Bag.Where(b => b.Item == ItemEffect.MasterBall).Sum(b => b.Count));
    }

    [Fact]
    public async Task Bad_actions_are_refused_and_change_nothing()
    {
        var (server, client, world, rules) = await Player("Tricky");
        await using var _s = server;
        await using var _c = client;
        var me = world.Playing("TrickyC")!;
        Assert.True(world.StartWildBattle(me, Pokemon.Create(rules.Data, StarterKit.Species, 3, new Random(1))));
        var mirror = Mirror(rules, await client.Expect<StateSnapshot>(), await client.Expect<BattleStart>());
        var id = world.BattleOf(me)!.Id;
        var before = mirror.Digest();

        // A move slot it doesn't have, an item it doesn't own, switching to nobody, a battle that isn't theirs.
        BattleInput[] bad =
        [
            new(id, 1, "turn", [new NetChoice("fight", 3)]),
            new(id, 2, "turn", [new NetChoice("item", ItemEffect.MasterBall, -1)]),
            new(id, 3, "turn", [new NetChoice("switch", 4)]),
            new(id, 4, "send-out", A: 0),
            new(id, 5, "turn", []),
            new(id + 99, 6, "turn", [new NetChoice("run")]),
        ];
        foreach (var input in bad)
        {
            await client.Send(input);
            Assert.False((await client.Expect<BattleTurn>(t => t.Seq == input.Seq)).Accepted);
        }
        // An old sequence number is ignored; the battle is where it was, and a real move still works.
        await Play(client, id, 7, mirror, new BattleInput(id, 7, "turn", [new NetChoice("fight", 0)]), () => mirror.Turn([new BattleChoice.Fight(0)]));
        Assert.NotEqual(before, mirror.Digest());
    }

    [Fact]
    public async Task Leaving_mid_battle_keeps_what_happened()
    {
        var (server, client, world, rules) = await Player("Quitter");
        await using var _s = server;
        var me = world.Playing("QuitterC")!;
        Assert.True(world.StartWildBattle(me, Pokemon.Create(rules.Data, StarterKit.Species, 3, new Random(2))));
        var mirror = Mirror(rules, await client.Expect<StateSnapshot>(), await client.Expect<BattleStart>());
        var id = world.BattleOf(me)!.Id;
        await Play(client, id, 1, mirror, new BattleInput(id, 1, "turn", [new NetChoice("fight", 0)]), () => mirror.Turn([new BattleChoice.Fight(0)]));
        await client.DisposeAsync();

        var accounts = server.App.Services.GetRequiredService<InfiniteRoutes.Server.Accounts.AccountService>();
        SaveGame? saved = null;
        for (var i = 0; i < 50 && saved?.Party[0].Moves[0].Pp != mirror.Party[0].Moves[0].Pp; i++)
        {
            await Task.Delay(100);
            saved = await accounts.CharacterNamed("QuitterC") is { StateJson: { } json } ? SaveGame.FromJson(json) : null;
        }
        Assert.Equal(mirror.Party[0].Moves[0].Pp, saved!.Party[0].Moves[0].Pp);   // the Tackle used is gone from its PP
        Assert.Equal(mirror.Party[0].Hp, saved.Party[0].Hp);
    }
}
