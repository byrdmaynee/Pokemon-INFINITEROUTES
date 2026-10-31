using System.Buffers.Binary;
using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Extractor.Gen5;

namespace InfiniteRoutes.Tests;

public class BattleTests
{
    static readonly BaseStats NoEvs = new(0, 0, 0, 0, 0, 0);

    static SpeciesInfo Species(int id, string name, BaseStats stats, int[] types, int baseExp = 64, int growth = 3) =>
        new(id, name, stats, types.Select(t => t.ToString()).ToList(), 45, NoEvs, 31, 20, 70, growth, [1], ["Overgrow"], null,
            baseExp, 6, 81, 1, types);

    static MoveInfo Move(int id, string name, int type, MoveCategory category, int power, int accuracy = 100, int pp = 35,
        MoveKind kind = MoveKind.Damage, IReadOnlyList<StatChange>? changes = null, Ailment ailment = Ailment.None,
        int ailmentChance = 0, int target = 0, int priority = 0) =>
        new(id, name, type, kind, category, power, accuracy, pp, priority, 1, 1, ailment, ailmentChance, 0, 0, 0, 0, 0, target,
            changes ?? [], 0);

    // Snivy-like grass starter vs a Patrat-like normal mon, with a tiny move pool.
    static readonly SpeciesInfo Grass = Species(495, "Snivy", new(45, 45, 55, 63, 45, 55), [TypeChart.Grass], 28);
    static readonly SpeciesInfo Normal = Species(504, "Patrat", new(45, 55, 39, 42, 35, 39), [TypeChart.Normal], 51, growth: 0);
    static readonly MoveInfo Tackle = Move(33, "Tackle", TypeChart.Normal, MoveCategory.Physical, 50);
    static readonly MoveInfo Leer = Move(43, "Leer", TypeChart.Normal, MoveCategory.Status, 0, kind: MoveKind.StatChange,
        changes: [new StatChange(BattleStat.Defense, -1, 0)]);
    static readonly MoveInfo VineWhip = Move(22, "Vine Whip", TypeChart.Grass, MoveCategory.Physical, 35, pp: 15);
    static readonly MoveInfo Struggle = Move(BattleData.Struggle, "Struggle", TypeChart.Normal, MoveCategory.Physical, 50, accuracy: 101, pp: 1);

    static BattleData Data() => new([Grass, Normal], [Tackle, Leer, VineWhip, Struggle],
        Enumerable.Range(0, 505).Select(i => (IReadOnlyList<LevelMove>)(i switch
        {
            495 => [new(1, 33), new(4, 43), new(7, 22)],
            504 => [new(1, 33), new(3, 43)],
            _ => [],
        })).ToList());

    [Fact]
    public void Move_record_decodes_type_power_and_stat_changes()
    {
        var r = new byte[Gen5Battle.MoveSize];
        r[0] = 9; r[1] = 6; r[2] = 2; r[3] = 40; r[4] = 100; r[5] = 25; r[6] = 0xFF; r[7] = 0x52;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(8), 4);
        r[0xA] = 10; r[0x12] = unchecked((byte)-33); r[0x15] = 5; r[0x18] = unchecked((byte)-1); r[0x1B] = 10; r[0x20] = 1;

        var move = Gen5Battle.DecodeMove(52, r, Enumerable.Range(0, 60).Select(i => $"move{i}").ToList());

        Assert.Equal("move52", move.Name);
        Assert.Equal((9, MoveKind.DamageLowerFoe, MoveCategory.Special, 40, 100, 25, -1), (move.Type, move.Kind, move.Category, move.Power, move.Accuracy, move.Pp, move.Priority));
        Assert.Equal((2, 5), (move.MinHits, move.MaxHits));
        Assert.Equal((Ailment.Burn, 10, -33), (move.Ailment, move.AilmentChance, move.Recoil));
        Assert.Equal([new StatChange(BattleStat.Speed, -1, 10)], move.StatChanges);
        Assert.True(move.MakesContact);
        Assert.Throws<InvalidDataException>(() => Gen5Battle.DecodeMove(1, new byte[10], []));
    }

    [Fact]
    public void Learnset_stops_at_the_terminator()
    {
        var r = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(r, 33);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), 43);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(6), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(8), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(12), 22);

        Assert.Equal([new LevelMove(1, 33), new LevelMove(4, 43)], Gen5Battle.DecodeLearnset(r));
    }

    [Fact]
    public void Encounter_file_has_one_or_four_seasons()
    {
        var set = new byte[Gen5Battle.EncounterSetSize];
        set[0] = 10; set[3] = 5;
        BinaryPrimitives.WriteUInt16LittleEndian(set.AsSpan(8), 504);
        set[10] = 2; set[11] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(set.AsSpan(12), 550 | (1 << 11));   // Basculin, Blue-Striped form
        set[14] = 9; set[15] = 3;   // max below min: clamped up

        var single = Gen5Battle.DecodeEncounters(7, set);
        Assert.Single(single.Seasons);
        var s = single.Seasons[0];
        Assert.Equal((10, 5), (s.GrassRate, s.SurfRate));
        Assert.Equal((12, 12, 12, 5, 5, 5, 5), (s.Grass.Count, s.DoubleGrass.Count, s.ShakingGrass.Count, s.Surf.Count, s.SurfSpots.Count, s.Fish.Count, s.FishSpots.Count));
        Assert.Equal(new EncounterSlot(504, 0, 2, 4), s.Grass[0]);
        Assert.Equal(new EncounterSlot(550, 1, 9, 9), s.Grass[1]);

        var seasonal = Gen5Battle.DecodeEncounters(8, [.. set, .. set, .. set, .. set]);
        Assert.Equal(4, seasonal.Seasons.Count);
        Assert.Same(seasonal.Seasons[2], seasonal.ForSeason(2));
        Assert.Same(single.Seasons[0], single.ForSeason(3));
        Assert.Throws<InvalidDataException>(() => Gen5Battle.DecodeEncounters(0, new byte[100]));
    }

    [Fact]
    public void Encounter_pick_follows_slot_weights_and_skips_empty_slots()
    {
        var slots = Enumerable.Range(0, 12).Select(i => new EncounterSlot(i < 2 ? 504 + i : 0, 0, 2, 4)).ToList();
        var rng = new Random(1);
        var picks = Enumerable.Range(0, 1000).Select(_ => EncounterSet.Pick(slots, EncounterSet.GrassWeights, rng)!.Species).ToList();
        Assert.All(picks, p => Assert.InRange(p, 504, 505));
        Assert.InRange(picks.Count(p => p == 504), 400, 600);
        Assert.Null(EncounterSet.Pick(slots.Select(s => s with { Species = 0 }).ToList(), EncounterSet.GrassWeights, rng));
    }

    [Fact]
    public void Type_chart_matches_generation_five()
    {
        Assert.Equal(2, TypeChart.Single(TypeChart.Fire, TypeChart.Grass));
        Assert.Equal(0.5, TypeChart.Single(TypeChart.Water, TypeChart.Grass));
        Assert.Equal(0, TypeChart.Single(TypeChart.Normal, TypeChart.Ghost));
        Assert.Equal(0, TypeChart.Single(TypeChart.Ground, TypeChart.Flying));
        Assert.Equal(0, TypeChart.Single(TypeChart.Psychic, TypeChart.Dark));
        Assert.Equal(0.5, TypeChart.Single(TypeChart.Ghost, TypeChart.Steel));
        Assert.Equal(0.5, TypeChart.Single(TypeChart.Dark, TypeChart.Steel));
        Assert.Equal(4, TypeChart.Against(TypeChart.Ice, [TypeChart.Ground, TypeChart.Flying]));
        Assert.Equal(0.25, TypeChart.Against(TypeChart.Fire, [TypeChart.Water, TypeChart.Rock]));
        // Every row is 17 entries and each type hits itself normally or worse (except Dragon and Ghost).
        for (var t = 0; t < TypeChart.Count; t++)
            Assert.Equal(t is TypeChart.Dragon or TypeChart.Ghost ? 2 : t is TypeChart.Normal or TypeChart.Fighting or TypeChart.Flying or TypeChart.Ground or TypeChart.Rock or TypeChart.Bug ? 1 : 0.5,
                TypeChart.Single(t, t));
    }

    [Fact]
    public void Stats_and_experience_follow_the_formulas()
    {
        Assert.Equal(1_000_000, ExpCurve.ForLevel(0, 100));
        Assert.Equal(1_059_860, ExpCurve.ForLevel(3, 100));
        Assert.Equal(135, ExpCurve.ForLevel(3, 5));
        Assert.Equal(800_000, ExpCurve.ForLevel(4, 100));
        Assert.Equal(1_250_000, ExpCurve.ForLevel(5, 100));
        Assert.Equal(600_000, ExpCurve.ForLevel(1, 100));
        Assert.Equal(1_640_000, ExpCurve.ForLevel(2, 100));
        Assert.Equal(0, ExpCurve.ForLevel(3, 1));

        var data = Data();
        var mon = Pokemon.Create(data, 495, 5, new Random(3));
        Array.Fill(mon.Ivs, 31);
        // HP: (2×45 + 31)×5/100 + 5 + 10 = 21.
        Assert.Equal(21, mon.MaxHp);
        var neutral = (2 * 63 + 31) * 5 / 100 + 5;
        Assert.InRange(mon.StatValue(Stat.Speed), neutral * 9 / 10, neutral * 11 / 10);
        // Learned by level 5: Tackle and Leer, not Vine Whip (level 7).
        Assert.Equal([33, 43], mon.Moves.Select(m => m.Move.Id));
    }

    [Fact]
    public void Level_up_learns_new_moves_and_keeps_damage_taken()
    {
        var data = Data();
        var mon = Pokemon.Create(data, 495, 5, new Random(4));
        mon.Hp -= 5;
        var levels = mon.GainExp(ExpCurve.ForLevel(3, 7) - mon.Exp, data);
        Assert.Equal([6, 7], levels.Select(l => l.Level));
        Assert.Equal(7, mon.Level);
        Assert.Equal(mon.MaxHp - 5, mon.Hp);
        Assert.Equal(22, Assert.Single(levels[1].Learned).Id);
        Assert.Equal(3, mon.Moves.Count);
    }

    [Fact]
    public void Battle_plays_out_until_one_side_faints()
    {
        var data = Data();
        var rng = new Random(11);
        var player = Pokemon.Create(data, 495, 9, rng);
        var wild = Pokemon.Create(data, 504, 3, rng);
        var battle = new WildBattle(data, player, wild, rng);
        Assert.Equal("A wild Patrat appeared!", ((MessageEvent)battle.Start()[0]).Text);

        var events = new List<BattleEvent>();
        for (var turn = 0; turn < 30 && !battle.Over; turn++)
            events.AddRange(battle.Fight(2));   // Vine Whip
        Assert.Equal(BattleOutcome.Won, battle.Outcome);
        Assert.Contains(events, e => e is FaintEvent { Side: BattleSide.Foe });
        Assert.Contains(events, e => e is ExpEvent { Gained: > 0 });
        Assert.True(wild.Fainted);
        Assert.True(player.Moves[2].Pp < player.Moves[2].MaxPp);
        // Every HP event is in range and the last one for the foe is 0.
        Assert.All(events.OfType<HpEvent>(), e => Assert.InRange(e.Hp, 0, e.MaxHp));
        Assert.Equal(0, events.OfType<HpEvent>().Last(e => e.Side == BattleSide.Foe).Hp);
        Assert.Empty(battle.Fight(0));
    }

    [Fact]
    public void Super_effective_hits_say_so_and_status_moves_lower_stats()
    {
        var fire = Species(4, "Charmander", new(39, 52, 43, 65, 60, 50), [TypeChart.Fire]);
        var ember = Move(52, "Ember", TypeChart.Fire, MoveCategory.Special, 40, pp: 25);
        var data = new BattleData([fire, Grass], [ember, Leer, Struggle],
            Enumerable.Range(0, 496).Select(i => (IReadOnlyList<LevelMove>)(i switch
            {
                4 => [new(1, 52)],
                495 => [new(1, 43)],
                _ => [],
            })).ToList());
        var rng = new Random(5);
        var battle = new WildBattle(data, Pokemon.Create(data, 4, 20, rng), Pokemon.Create(data, 495, 20, rng), rng);
        var events = battle.Fight(0);
        Assert.Contains(events, e => e is MessageEvent { Text: "It's super effective!" });
        Assert.Contains(events, e => e is MessageEvent { Text: "Charmander's Defense fell!" });
        Assert.Equal(-1, battle.Player.Stage(BattleStat.Defense));
    }

    [Fact]
    public void Running_from_a_slower_pokemon_always_works()
    {
        var data = Data();
        var rng = new Random(2);
        var player = Pokemon.Create(data, 495, 30, rng);
        var battle = new WildBattle(data, player, Pokemon.Create(data, 504, 2, rng), rng);
        Assert.Contains(battle.Run(), e => e is MessageEvent { Text: "Got away safely!" });
        Assert.Equal(BattleOutcome.Escaped, battle.Outcome);
    }

    [Fact]
    public void Struggle_when_out_of_pp_and_losing_ends_the_battle()
    {
        var data = Data();
        var rng = new Random(9);
        var player = Pokemon.Create(data, 504, 2, rng);
        var wild = Pokemon.Create(data, 495, 40, rng);
        foreach (var slot in player.Moves)
            slot.Pp = 0;
        var battle = new WildBattle(data, player, wild, rng);
        Assert.True(battle.MustStruggle);
        var events = new List<BattleEvent>();
        for (var turn = 0; turn < 20 && !battle.Over; turn++)
            events.AddRange(battle.Fight(0));
        Assert.Contains(events, e => e is MessageEvent { Text: "Patrat used Struggle!" } || e is MessageEvent { Text: "You have no more Pokémon that can fight! You blacked out!" });
        Assert.Equal(BattleOutcome.Lost, battle.Outcome);
    }

    [Fact]
    public void Master_ball_always_catches_and_weak_pokemon_are_easier()
    {
        var data = Data();
        var rng = new Random(3);
        var bag = new Bag();
        bag.Add(ItemEffect.MasterBall);
        var battle = new WildBattle(data, [Pokemon.Create(data, 495, 5, rng)], bag, Pokemon.Create(data, 504, 3, rng), rng);
        var events = battle.UseItem(ItemEffect.MasterBall);
        Assert.Equal(new CatchEvent(ItemEffect.MasterBall, 3, true), events.OfType<CatchEvent>().Single());
        Assert.Equal(BattleOutcome.Caught, battle.Outcome);
        Assert.Equal(0, bag.Count(ItemEffect.MasterBall));

        // Catch rate 45 at full HP vs 1 HP and asleep, 2000 throws each.
        int Catches(Action<Pokemon> prepare)
        {
            var caught = 0;
            for (var i = 0; i < 2000; i++)
            {
                var b = new Bag();
                b.Add(ItemEffect.PokeBall);
                var wild = Pokemon.Create(data, 504, 3, rng);
                prepare(wild);
                if (new WildBattle(data, [Pokemon.Create(data, 495, 50, rng)], b, wild, rng).UseItem(ItemEffect.PokeBall).OfType<CatchEvent>().Single().Caught)
                    caught++;
            }
            return caught;
        }
        var full = Catches(_ => { });
        var weak = Catches(w => { w.Hp = 1; w.Status = Ailment.Sleep; });
        Assert.InRange(full, 1, 1000);
        Assert.True(weak > full * 2, $"{weak} vs {full}");
    }

    [Fact]
    public void Medicine_heals_and_is_kept_when_useless()
    {
        var data = Data();
        var rng = new Random(4);
        var bag = new Bag();
        bag.Add(ItemEffect.Potion, 2);
        var mon = Pokemon.Create(data, 495, 10, rng);
        var battle = new WildBattle(data, [mon], bag, Pokemon.Create(data, 504, 2, rng), rng);

        Assert.Equal([new MessageEvent("It won't have any effect.")], battle.UseItem(ItemEffect.Potion, 0));
        Assert.Equal(2, bag.Count(ItemEffect.Potion));

        mon.Hp = 1;
        var events = battle.UseItem(ItemEffect.Potion, 0);
        Assert.Contains(events, e => e is HpEvent { Side: BattleSide.Player, Hp: > 1 });
        Assert.Equal(1, bag.Count(ItemEffect.Potion));
        Assert.Equal("Snivy's HP was restored by 20 point(s).", ItemUse.Apply(new ItemEffect.Heal(20), new Func<Pokemon>(() => { var m = Pokemon.Create(data, 495, 50, rng); m.Hp = 1; return m; })()));
        var fainted = Pokemon.Create(data, 495, 10, rng);
        fainted.Hp = 0;
        Assert.Null(ItemUse.Apply(ItemEffect.For(ItemEffect.Potion)!, fainted));
        Assert.Equal("Snivy was revived!", ItemUse.Apply(ItemEffect.For(ItemEffect.ReviveItem)!, fainted));
        Assert.Equal(fainted.MaxHp / 2, fainted.Hp);
    }

    [Fact]
    public void Fainting_with_others_left_asks_for_a_switch_and_exp_is_shared()
    {
        var data = Data();
        var rng = new Random(6);
        var weak = Pokemon.Create(data, 504, 2, rng);
        var strong = Pokemon.Create(data, 495, 30, rng);
        var wild = Pokemon.Create(data, 495, 12, rng);
        var battle = new WildBattle(data, [weak, strong], new Bag(), wild, rng);

        for (var turn = 0; turn < 30 && !battle.NeedsSwitch; turn++)
            battle.Fight(0);
        Assert.True(battle.NeedsSwitch);
        Assert.False(battle.Over);
        Assert.Empty(battle.Fight(0));
        Assert.Empty(battle.SendOut(0));   // fainted

        var sent = battle.SendOut(1);
        Assert.Equal(new SwitchEvent(1), sent.OfType<SwitchEvent>().Single());
        Assert.Same(strong, battle.Player.Mon);
        var events = new List<BattleEvent>();
        for (var turn = 0; turn < 30 && !battle.Over; turn++)
            events.AddRange(battle.Fight(0));
        Assert.Equal(BattleOutcome.Won, battle.Outcome);
        // The fainted Patrat gets nothing; Snivy alone earns it.
        Assert.Equal(1, Assert.Single(events.OfType<ExpEvent>()).PartyIndex);
    }

    [Fact]
    public void Switching_costs_a_turn_and_both_share_the_exp()
    {
        var data = Data();
        var rng = new Random(8);
        var a = Pokemon.Create(data, 495, 40, rng);
        var b = Pokemon.Create(data, 495, 40, rng);
        var battle = new WildBattle(data, [a, b], new Bag(), Pokemon.Create(data, 504, 2, rng), rng);
        var events = battle.Switch(1);
        Assert.Contains(events, e => e is MessageEvent { Text: "Snivy, come back!" });
        Assert.Contains(events, e => e is MessageEvent { Text: "The wild Patrat used Tackle!" } || e is MessageEvent { Text: "The wild Patrat used Leer!" });
        Assert.Equal(1, battle.ActiveIndex);
        Assert.Empty(battle.Switch(1));

        var rest = new List<BattleEvent>();
        for (var turn = 0; turn < 10 && !battle.Over; turn++)
            rest.AddRange(battle.Fight(0));
        Assert.Equal([0, 1], rest.OfType<ExpEvent>().Select(e => e.PartyIndex).Order());
    }

    [Fact]
    public void Bag_keeps_order_and_drops_empty_items()
    {
        var bag = new Bag();
        bag.Add(ItemEffect.Potion, 2);
        bag.Add(ItemEffect.PokeBall, 5);
        bag.Add(ItemEffect.Potion);
        Assert.Equal([(ItemEffect.Potion, 3), (ItemEffect.PokeBall, 5)], bag.Items);
        Assert.Equal([(ItemEffect.PokeBall, 5)], bag.Balls);
        for (var i = 0; i < 3; i++)
            Assert.True(bag.Take(ItemEffect.Potion));
        Assert.False(bag.Take(ItemEffect.Potion));
        Assert.Empty(bag.Medicine);
    }

    [Fact]
    public void Exp_yield_scales_with_level_difference()
    {
        var data = Data();
        var rng = new Random(1);
        var wild = Pokemon.Create(data, 504, 5, rng);
        Assert.True(WildBattle.ExpYield(wild, Pokemon.Create(data, 495, 2, rng)) > WildBattle.ExpYield(wild, Pokemon.Create(data, 495, 20, rng)));
    }
}
