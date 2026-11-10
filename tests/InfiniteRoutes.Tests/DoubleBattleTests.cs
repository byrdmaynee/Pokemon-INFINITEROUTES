using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Tests;

public class DoubleBattleTests
{
    static readonly BaseStats NoEvs = new(0, 0, 0, 0, 0, 0);

    static SpeciesInfo Species(int id, string name, BaseStats stats, int[] types) =>
        new(id, name, stats, types.Select(t => t.ToString()).ToList(), 45, NoEvs, 31, 20, 70, 3, [1], ["Overgrow"], null,
            64, 6, 81, 1, types);

    static MoveInfo Move(int id, string name, int type, MoveCategory category, int power, int target = 0, MoveKind kind = MoveKind.Damage,
        IReadOnlyList<StatChange>? changes = null) =>
        new(id, name, type, kind, category, power, 101, 35, 0, 1, 1, Ailment.None, 0, 0, 0, 0, 0, 0, target, changes ?? [], 0);

    static readonly SpeciesInfo Sturdy = Species(1, "Tank", new(200, 50, 120, 50, 120, 50), [TypeChart.Normal]);
    static readonly SpeciesInfo Fast = Species(2, "Dart", new(60, 90, 50, 90, 50, 130), [TypeChart.Normal]);
    static readonly MoveInfo Tackle = Move(33, "Tackle", TypeChart.Normal, MoveCategory.Physical, 40);
    static readonly MoveInfo Rockslide = Move(157, "Rock Slide", TypeChart.Rock, MoveCategory.Physical, 75, target: 5);
    static readonly MoveInfo Quake = Move(89, "Earthquake", TypeChart.Ground, MoveCategory.Physical, 100, target: 4);
    static readonly MoveInfo Growl = Move(45, "Growl", TypeChart.Normal, MoveCategory.Status, 0, target: 5, kind: MoveKind.StatChange,
        changes: [new StatChange(BattleStat.Attack, -1, 0)]);
    static readonly MoveInfo Struggle = Move(BattleData.Struggle, "Struggle", TypeChart.Normal, MoveCategory.Physical, 50);

    static BattleData Data() => new([Sturdy, Fast], [Tackle, Rockslide, Quake, Growl, Struggle],
        Enumerable.Range(0, 3).Select(i => (IReadOnlyList<LevelMove>)(i == 0 ? [] : [new(1, 33), new(1, 157), new(1, 89), new(1, 45)])).ToList());

    static TrainerInfo Trainer(int battleType) => new(1, "Tia", 1, "Twins", battleType, [], 0, 10, []);

    static List<Pokemon> Team(BattleData data, Random rng, int count, int species = 1, int level = 30) =>
        Enumerable.Range(0, count).Select(_ => Pokemon.Create(data, species, level, rng)).ToList();

    [Fact]
    public void Double_battle_puts_two_out_a_side_and_names_them()
    {
        var data = Data();
        var rng = new Random(1);
        var battle = new SingleBattle(data, Team(data, rng, 3), new Bag(), Team(data, rng, 2, 2), Trainer(1), rng);

        Assert.Equal((BattleFormat.Double, 2), (battle.Format, battle.Width));
        Assert.Equal([0, 1], battle.FoeField.Select(b => b.Slot));
        var start = battle.Start().OfType<MessageEvent>().Select(m => m.Text).ToList();
        Assert.Contains("Twins Tia sent out Dart and Dart!", start);
        Assert.Contains("Go! Tank and Tank!", start);
    }

    [Fact]
    public void Too_few_Pokémon_fall_back_to_a_single_battle()
    {
        var data = Data();
        var rng = new Random(1);
        var battle = new SingleBattle(data, Team(data, rng, 1), new Bag(), Team(data, rng, 2, 2), Trainer(2), rng);
        Assert.Equal((BattleFormat.Single, 1), (battle.Format, battle.Width));
    }

    [Fact]
    public void Spread_moves_hit_every_adjacent_target_once_for_less()
    {
        var data = Data();
        var rng = new Random(3);
        var foes = Team(data, rng, 2);
        var battle = new SingleBattle(data, Team(data, rng, 2, 2), new Bag(), foes, Trainer(1), rng);
        battle.Start();
        // Slot 0: Rock Slide (both foes); slot 1: Earthquake (both foes and its partner).
        var events = battle.Turn([new BattleChoice.Fight(1), new BattleChoice.Fight(2)]).ToList();

        var anims = events.OfType<MoveAnimationEvent>().Where(a => a.User == BattleSide.Player).ToList();
        Assert.Equal(2, anims.Count);
        var slide = events.IndexOf(anims.First(a => a.Move == 157));
        var hits = events.Skip(slide).TakeWhile(e => e is not MoveAnimationEvent || e == events[slide]).OfType<HpEvent>()
            .Where(h => h.Side == BattleSide.Foe).Select(h => h.Slot).Distinct().ToList();
        Assert.Equal([0, 1], hits.Order());
        var quake = events.IndexOf(anims.First(a => a.Move == 89));
        var quakeHits = events.Skip(quake + 1).TakeWhile(e => e is not MoveAnimationEvent).OfType<HpEvent>().ToList();
        Assert.Contains(quakeHits, h => h is { Side: BattleSide.Player, Slot: 0 });   // the partner gets hit too
    }

    [Fact]
    public void A_fainted_target_passes_the_hit_to_its_partner_and_is_replaced_after_the_turn()
    {
        var data = Data();
        var rng = new Random(5);
        var foes = Team(data, rng, 3, 2, level: 5);
        foes[0].Hp = 1;
        var battle = new SingleBattle(data, Team(data, rng, 2, 2, level: 40), new Bag(), foes, Trainer(1), rng);
        battle.Start();
        // Both of the player's Pokémon aim Tackle at foe slot 0; the second hit goes to slot 1.
        var events = battle.Turn([new BattleChoice.Fight(0, BattleSide.Foe, 0), new BattleChoice.Fight(0, BattleSide.Foe, 0)]).ToList();

        Assert.Contains(new FaintEvent(BattleSide.Foe, 0), events);
        Assert.Contains(events, e => e is HpEvent { Side: BattleSide.Foe, Slot: 1 });
        Assert.Contains(events, e => e is SwitchEvent { Side: BattleSide.Foe, Slot: 0, PartyIndex: 2 });
        Assert.Same(foes[2], battle.FoeField[0].Mon);
    }

    [Fact]
    public void The_player_fills_a_fainted_slot_from_the_bench()
    {
        var data = Data();
        var rng = new Random(9);
        var mine = Team(data, rng, 3, 2, level: 5);
        mine[1].Hp = 1;
        var battle = new SingleBattle(data, mine, new Bag(), Team(data, rng, 2, 2, level: 60), Trainer(1), rng);
        battle.Start();
        for (var turn = 0; turn < 10 && !battle.NeedsSwitch && !battle.Over; turn++)
            battle.Turn([new BattleChoice.Fight(3), new BattleChoice.Fight(3)]);   // Growl while the foes attack

        Assert.True(battle.NeedsSwitch);
        var slot = battle.SlotsToFill[0];
        Assert.Empty(battle.SendOut(0));   // already out
        var events = battle.SendOut(2);
        Assert.Contains(new SwitchEvent(2, BattleSide.Player, slot), events);
        Assert.Same(mine[2], battle.PlayerField[slot].Mon);
    }

    [Fact]
    public void Triple_battle_ends_cannot_reach_each_other()
    {
        var data = Data();
        var rng = new Random(2);
        var battle = new SingleBattle(data, Team(data, rng, 3), new Bag(), Team(data, rng, 3), Trainer(2), rng);
        Assert.Equal(3, battle.Width);
        var left = battle.PlayerField[0];
        // The player's slot 0 faces the foe's slot 2; the foe's slot 0 is across at the far end.
        Assert.Equal([1, 2], battle.Targets(left, Rockslide, BattleSide.Foe, 0).Select(t => t.Slot).Order());
        Assert.Equal(1, battle.Targets(left, Tackle, BattleSide.Foe, 0).Single().Slot);
        Assert.Equal(3, battle.Targets(left, Quake, BattleSide.Foe, 0).Count);   // two foes and the middle partner
        Assert.Equal(5, battle.Targets(battle.PlayerField[1], Quake, BattleSide.Foe, 0).Count);
        Assert.True(battle.NeedsTarget(left, Tackle));
        Assert.False(battle.NeedsTarget(left, Rockslide));
    }

    [Fact]
    public void Wild_double_battles_need_one_left_to_throw_a_ball()
    {
        var data = Data();
        var rng = new Random(4);
        var bag = new Bag();
        bag.Add(ItemEffect.PokeBall, 5);
        var battle = new SingleBattle(data, Team(data, rng, 2), bag, Pokemon.Create(data, 2, 5, rng), Pokemon.Create(data, 2, 5, rng), rng);
        Assert.Equal("Wild Dart and Dart appeared!", ((MessageEvent)battle.Start()[0]).Text);
        var refused = battle.Turn([new BattleChoice.Item(ItemEffect.PokeBall), new BattleChoice.Fight(0)]);
        Assert.Equal("There are two Pokémon out! You can't aim a Poké Ball!", ((MessageEvent)Assert.Single(refused)).Text);
        Assert.Equal(5, bag.Count(ItemEffect.PokeBall));
    }

    [Fact]
    public void Double_battles_play_out_to_the_end()
    {
        var data = Data();
        for (var seed = 0; seed < 20; seed++)
        {
            var rng = new Random(seed);
            var mine = Team(data, rng, 4, 2, level: 20 + seed % 5);
            var battle = new SingleBattle(data, mine, new Bag(), Team(data, rng, 4, 2, level: 22), Trainer(seed % 2 == 0 ? 1 : 2), rng);
            battle.Start();
            for (var turn = 0; turn < 200 && !battle.Over; turn++)
            {
                if (battle.NeedsSwitch)
                {
                    var next = Enumerable.Range(0, mine.Count).First(i => !mine[i].Fainted && !battle.IsOut(i));
                    battle.SendOut(next);
                    continue;
                }
                battle.Turn(battle.PlayerField.Select(b => (BattleChoice?)new BattleChoice.Fight(rng.Next(4), BattleSide.Foe, rng.Next(battle.Width))).ToList());
            }
            Assert.True(battle.Over, $"seed {seed}");
            Assert.True(battle.Outcome is BattleOutcome.Won or BattleOutcome.Lost);
            Assert.True(battle.Outcome == BattleOutcome.Won ? battle.FoeParty.All(p => p.Fainted) : mine.All(p => p.Fainted));
        }
    }

    [Fact]
    public void Rotation_is_free_once_a_turn_among_the_first_three()
    {
        var data = Data();
        var rng = new Random(6);
        var mine = Team(data, rng, 4, 2);
        var battle = new SingleBattle(data, mine, new Bag(), Team(data, rng, 3), Trainer(3), rng);
        battle.Start();
        Assert.Equal((BattleFormat.Rotation, 1, 3), (battle.Format, battle.Width, battle.RotationTeam.Count));
        Assert.Empty(battle.Rotate(3));   // not one of the three
        var rotated = battle.Rotate(1);
        Assert.Contains(new SwitchEvent(1, BattleSide.Player, 0), rotated);
        Assert.False(battle.CanRotate);
        Assert.DoesNotContain(battle.Fight(0), e => e is MessageEvent { Text: var t } && t.Contains("come back"));
        Assert.True(battle.CanRotate || battle.Over || battle.NeedsSwitch);
    }
}
