using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;
using InfiniteRoutes.Extractor.Gen5;
using InfiniteRoutes.Server.Game;
using Microsoft.Extensions.DependencyInjection;

namespace InfiniteRoutes.Tests;

/// <summary>NPCs: what their scripts say and do, gym leaders taken in order, gifts once, side quests from townspeople.</summary>
public class NpcTests
{
    static TrainerInfo Leader(int id, string name) =>
        new(id, name, 1, "Leader", 0, [], 0, 10, [new TrainerPokemon(StarterKit.Species, 0, 2, 0, 0, 0, null)]);

    /// <summary>Cilan (Trio Badge) and Lenora (Basic Badge).</summary>
    static readonly TrainerInfo[] Leaders = [Leader(0, "Cilan"), Leader(1, "Lenora")];

    static GameRules Rules() => new(AdminChatTests.Rules().Data, Leaders, "white");

    static GameState NewGame(GameRules rules, IEnumerable<QuestInfo>? quests = null)
    {
        var state = new GameState(new QuestLog(quests ?? []));
        state.Party.Add(Pokemon.Create(rules.Data, StarterKit.Species, 5, new Random(1)));
        return state;
    }

    // ---- The deal: battles, gates, gifts, quests ------------------------------------------------------------------

    [Fact]
    public void Gym_leaders_battle_in_order_and_only_until_beaten()
    {
        var rules = Rules();
        var game = NewGame(rules);
        var now = DateTime.Now;

        var lenora = game.Deal(new NpcDeal(1, 1, Trainer: 1), Leaders, rules.Data, now);
        Assert.Equal(NpcDealKind.Refused, lenora.Kind);
        Assert.Contains("Trio Badge", lenora.Text);

        var cilan = game.Deal(new NpcDeal(1, 0, Trainer: 0), Leaders, rules.Data, now);
        Assert.Equal(NpcDealKind.Battle, cilan.Kind);
        Assert.Equal("Cilan", cilan.Trainer!.Name);

        game.Progress.Defeated(Leaders[0], now);
        Assert.Equal(NpcDealKind.None, game.Deal(new NpcDeal(1, 0, Trainer: 0), Leaders, rules.Data, now).Kind);
        Assert.Equal(NpcDealKind.Battle, game.Deal(new NpcDeal(1, 1, Trainer: 1), Leaders, rules.Data, now).Kind);
    }

    [Fact]
    public void A_gift_is_handed_over_once_and_kept_in_saves()
    {
        var rules = Rules();
        var game = NewGame(rules);
        var deal = new NpcDeal(7, 3, GiftItem: ItemEffect.Potion, GiftCount: 2);

        var first = game.Deal(deal, Leaders, rules.Data, DateTime.Now);
        Assert.Equal(NpcDealKind.Gift, first.Kind);
        Assert.Equal(2, game.Bag.Count(ItemEffect.Potion));
        Assert.Equal(NpcDealKind.None, game.Deal(deal, Leaders, rules.Data, DateTime.Now).Kind);

        var loaded = SaveGame.FromJson(SaveGame.From(game, "white", DateTime.Now).ToJson())!.Restore(rules.Data, new QuestLog([]));
        Assert.Contains("7:3", loaded.Progress.Gifts);
        Assert.Equal(NpcDealKind.None, loaded.Deal(deal, Leaders, rules.Data, DateTime.Now).Kind);
    }

    static readonly QuestInfo Survey = new("side-survey", "Survey", "Catch two Snivy.", QuestCategory.Side,
        [new(ObjectiveKind.CatchSpecies, "Catch 2 Snivy", 2, StarterKit.Species)], new(Money: 700, Items: [(ItemEffect.GreatBall, 3)]),
        Giver: NpcDeal.KeyOf(5, 9), MinBadges: 1);

    static readonly QuestInfo Daily = new("daily-x", "Daily", "Win a wild battle.", QuestCategory.Daily,
        [new(ObjectiveKind.WinWild, "Win 1", 1)], new(Money: 100), Giver: NpcDeal.KeyOf(5, 10));

    [Fact]
    public void A_townsperson_hands_out_their_side_quest_once_the_player_has_the_badges_for_it()
    {
        var rules = Rules();
        var game = NewGame(rules, [Survey]);
        var giver = new NpcDeal(5, 9);
        var now = DateTime.Now;

        Assert.Null(game.Quests.Offer(giver.Key, game.Progress, now));   // needs a badge first
        Assert.Equal(NpcDealKind.None, game.Deal(giver, Leaders, rules.Data, now).Kind);
        game.Progress.Defeated(Leaders[0], now);
        Assert.Same(Survey, game.Quests.Offer(giver.Key, game.Progress, now));

        // A Snivy caught before taking the quest doesn't count.
        game.Progress.CaughtSpecies[StarterKit.Species] = 1;
        var taken = game.Deal(giver, Leaders, rules.Data, now);
        Assert.Equal(NpcDealKind.Quest, taken.Kind);
        Assert.Contains("Survey", taken.Text);
        Assert.Null(game.Quests.Offer(giver.Key, game.Progress, now));

        game.Progress.CaughtSpecies[StarterKit.Species] = 2;
        Assert.Empty(game.UpdateQuests(now));
        game.Progress.CaughtSpecies[StarterKit.Species] = 3;
        var done = Assert.Single(game.UpdateQuests(now));
        var money = game.Progress.Money;
        Assert.Contains("₽700", game.GrantReward(done, rules.Data));
        Assert.Equal(money + 700, game.Progress.Money);
        Assert.Equal(3, game.Bag.Count(ItemEffect.GreatBall));
        Assert.Null(game.Quests.Offer(giver.Key, game.Progress, now));   // done for good
    }

    [Fact]
    public void A_daily_quest_giver_offers_it_again_the_next_day()
    {
        var rules = Rules();
        var game = NewGame(rules, [Daily]);
        var today = new DateTime(2026, 11, 11, 10, 0, 0);
        var giver = new NpcDeal(5, 10);

        Assert.Equal(NpcDealKind.Quest, game.Deal(giver, Leaders, rules.Data, today).Kind);
        game.Progress.WildWins++;
        Assert.Single(game.UpdateQuests(today));
        Assert.Null(game.Quests.Offer(giver.Key, game.Progress, today));
        Assert.Empty(game.UpdateQuests(today.AddDays(1)));   // doesn't restart by itself
        Assert.Equal(NpcDealKind.Quest, game.Deal(giver, Leaders, rules.Data, today.AddDays(1)).Kind);
    }

    [Fact]
    public void The_side_quests_each_have_their_own_giver()
    {
        var side = SideQuests.Unova().ToList();
        Assert.All(side, q => Assert.Matches(@"^\d+:\d+$", q.Giver!));
        Assert.Equal(side.Count, side.Select(q => q.Giver).Distinct().Count());
        Assert.Equal(side.Count, side.Select(q => q.Id).Distinct().Count());
    }

    [Fact]
    public async Task Roles_get_beacons_and_only_Owners_wear_the_crown()
    {
        await using var server = await TestServer.Start(channels: 1);
        var roles = server.App.Services.GetRequiredService<InfiniteRoutes.Server.Admin.RoleService>();
        Assert.Equal(Beacons.Crown, roles.Get("Owner").Beacon);
        Assert.Equal(Beacons.Hammer, roles.Get("Moderator").Beacon);
        Assert.Equal("", roles.Get("Player").Beacon);

        Assert.NotNull(await roles.SetBeacon("Moderator", Beacons.Crown));
        Assert.NotNull(await roles.SetBeacon("Moderator", "rocket"));
        Assert.Null(await roles.SetBeacon("Moderator", "clover"));
        Assert.Equal("clover", roles.Get("Moderator").Beacon);
        Assert.Equal(22, Beacons.All.Count);
    }

    // ---- Scripts ----------------------------------------------------------------------------------------------------

    static byte[] Script(params int[] words) => words.SelectMany(w => new[] { (byte)w, (byte)(w >> 8) }).ToArray();

    /// <summary>One script: header (offset 0 → starts at byte 6), 0xFD13, then the commands.</summary>
    static (byte[] File, List<int> Offsets) File(params int[] commands)
    {
        var file = Script([2, 0, 0xFD13, .. commands]);
        return (file, Gen5Scripts.Offsets(file));
    }

    [Fact]
    public void A_script_gives_its_lines_and_what_it_says_once_done()
    {
        string[] text = ["Hello!", "Take this!", "How's it going?"];
        // If (already given) → skip to "How's it going?"; else say lines 0 and 1, give item 17 ×2 (std 10110), end.
        // Commands: 1F 00FF <u32 rel>; 3D 00 04 msg 0 0; ...
        int[] given = [0x3D, 0x0400, 2, 0, 0, 0x02];
        int[] first =
        [
            0x3D, 0x0400, 0, 0, 0,
            0x3D, 0x0400, 1, 0, 0,
            0x2A, 0x8000, 17, 0x2A, 0x8001, 2, 0x1C, Gen5Scripts.GiveItem,
            0x02,
        ];
        var skip = first.Length * 2;   // relative to the end of the If command (opcode, u8, u32)
        byte[] file = [.. Script(2, 0, 0xFD13), 0x1F, 0x00, 0xFF, (byte)skip, (byte)(skip >> 8), 0, 0, .. Script([.. first, .. given])];
        var offsets = Gen5Scripts.Offsets(file);

        var talk = Gen5Scripts.Talk(file, offsets, 1, text)!;
        Assert.Equal(["Hello!", "Take this!"], talk.Lines);
        Assert.Equal(17, talk.GiftItem);
        Assert.Equal(2, talk.GiftCount);
        Assert.Equal(["How's it going?"], talk.Later!);
        Assert.Equal(-1, talk.Trainer);
    }

    [Fact]
    public void A_gym_leader_script_battles_and_says_its_badge_lines_after()
    {
        string[] text = ["Let's battle!", "You win the badge!"];
        int[] commands = [0x3D, 0x0400, 0, 0, 0, 0x3E, 0x85, 21, 0, 0, 0x3D, 0x0400, 1, 0, 0, 0x02];
        var (file, offsets) = File(commands);
        var talk = Gen5Scripts.Talk(file, offsets, 1, text)!;
        Assert.Equal(21, talk.Trainer);
        Assert.Equal(["Let's battle!"], talk.Lines);
        Assert.Equal(["You win the badge!"], talk.Win!);
    }

    // ---- Online ------------------------------------------------------------------------------------------------------

    static async Task<(TestServer Server, TestClient Client, GameRules Rules)> Player(string name)
    {
        var rules = Rules();
        var server = await TestServer.Start(channels: 1, rules: rules);
        var (token, character) = await server.NewPlayer(name);
        var client = await TestClient.Connect(server.GamePort);
        await client.Send(new Hello(Protocol.Version, token, character));
        await client.Expect<Welcome>();
        await client.Expect<StateSnapshot>();
        return (server, client, rules);
    }

    [Fact]
    public async Task Talking_to_a_gym_leader_online_starts_a_trainer_battle_the_client_rebuilds()
    {
        var (server, client, rules) = await Player("Challenger");
        await using var _s = server;
        await using var _c = client;

        // Lenora waits for the Trio Badge.
        await client.Send(new NpcAction(1, 1));
        var refused = await client.Expect<NpcResult>();
        Assert.Equal((int)NpcDealKind.Refused, refused.Kind);
        Assert.Contains("Trio Badge", refused.Text);

        await client.Send(new NpcAction(1, 0));
        var snapshot = await client.Expect<StateSnapshot>();
        var start = await client.Expect<BattleStart>();
        Assert.Equal(0, start.Trainer);

        var state = SaveGame.FromJson(snapshot.Json)!.Restore(rules.Data, new QuestLog([]));
        var rng = new Random(start.Seed);
        var mirror = new SingleBattle(rules.Data, state.Party, state.Bag, SingleBattle.TrainerParty(rules.Data, Leaders[0], rng), Leaders[0], rng);
        mirror.Start();
        var seq = 0;
        for (var i = 0; i < 80 && !mirror.Over; i++)
        {
            seq++;
            await client.Send(new BattleInput(start.Battle, seq, "turn", [new NetChoice("fight", 0)]));
            var turn = await client.Expect<BattleTurn>(t => t.Seq == seq);
            Assert.True(turn.Accepted, turn.Reason);
            mirror.Reseed(turn.Seed);
            if (mirror.NeedsSwitch)
                break;
            mirror.Turn([new BattleChoice.Fight(0)]);
            Assert.Equal(turn.Digest, mirror.Digest());
        }
        await client.Send(new BattleInput(start.Battle, seq + 1, "end"));
        var over = await client.Expect<BattleOver>();
        if (mirror.Outcome == BattleOutcome.Won)
            Assert.Contains(over.Lines, l => l.Contains("Trio Badge"));
    }

    [Fact]
    public async Task Gifts_online_come_once_and_only_within_reach()
    {
        var (server, client, _) = await Player("Gifted");
        await using var _s = server;
        await using var _c = client;

        await client.Send(new NpcAction(1, 3));   // at (15, 15)
        Assert.Equal((int)NpcDealKind.Refused, (await client.Expect<NpcResult>()).Kind);

        await client.Send(new NpcAction(1, 2));
        var state = SaveGame.FromJson((await client.Expect<StateSnapshot>()).Json)!;
        var gift = await client.Expect<NpcResult>();
        Assert.Equal((int)NpcDealKind.Gift, gift.Kind);
        Assert.Contains(state.Bag, i => i.Item == ItemEffect.Potion && i.Count >= 2);
        Assert.Contains("1:2", state.Progress.Gifts!);

        await client.Send(new NpcAction(1, 2));
        Assert.Equal((int)NpcDealKind.None, (await client.Expect<NpcResult>(r => r.Kind != (int)NpcDealKind.Gift)).Kind);
    }

    [Fact]
    public async Task The_nurse_heals_the_party_online()
    {
        var (server, client, rules) = await Player("Patient");
        await using var _s = server;
        await using var _c = client;
        var world = server.App.Services.GetRequiredService<InfiniteRoutes.Server.World.WorldService>();
        world.Players().Single().State!.Party[0].Hp = 1;

        await client.Send(new NpcAction(1, 4));
        var state = SaveGame.FromJson((await client.Expect<StateSnapshot>()).Json)!;
        Assert.Contains("restored", (await client.Expect<NpcResult>()).Text);
        Assert.Equal(Pokemon.FromSave(rules.Data, state.Party[0])!.MaxHp, state.Party[0].Hp);
        Assert.Equal(1, state.Progress.CenterVisits);
    }
}
