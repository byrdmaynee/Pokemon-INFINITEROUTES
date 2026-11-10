using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Tests;

public class MoveEffectTests
{
    static readonly BaseStats NoEvs = new(0, 0, 0, 0, 0, 0);

    static SpeciesInfo Species(int id, int type = TypeChart.Normal) =>
        new(id, $"Mon{id}", new BaseStats(80, 60, 60, 60, 60, 60), [type.ToString()], 45, NoEvs, 127, 20, 70, 3, [1],
            ["None"], null, 100, 6, 200, 1, [type], [0]);

    static MoveInfo Move(int id, int type, MoveCategory category, int power, MoveKind kind = MoveKind.Damage, Ailment ailment = Ailment.None,
        int chance = 0, int priority = 0, int target = 0, int flags = 0) =>
        new(id, $"Move{id}", type, kind, category, power, 101, 35, priority, 1, 1, ailment, chance, 0, 0, 0, 0, 0, target, [], flags);

    static readonly MoveInfo Tackle = Move(33, TypeChart.Normal, MoveCategory.Physical, 50, flags: 1);
    static readonly MoveInfo Splash = Move(150, TypeChart.Normal, MoveCategory.Status, 0, target: 7);
    static readonly MoveInfo SeismicToss = Move(Moves.SeismicToss, TypeChart.Fighting, MoveCategory.Physical, 1);
    static readonly MoveInfo Protect = Move(Moves.Protect, TypeChart.Normal, MoveCategory.Status, 0, priority: 4, target: 7);
    static readonly MoveInfo Fly = Move(Moves.Fly, TypeChart.Flying, MoveCategory.Physical, 90, flags: 1);
    static readonly MoveInfo HyperBeam = Move(Moves.HyperBeam, TypeChart.Normal, MoveCategory.Special, 150);
    static readonly MoveInfo Substitute = Move(Moves.Substitute, TypeChart.Normal, MoveCategory.Status, 0, target: 7);
    static readonly MoveInfo Toxic = Move(Moves.Toxic, TypeChart.Poison, MoveCategory.Status, 0, MoveKind.Ailment, Ailment.Poison);
    static readonly MoveInfo LeechSeed = Move(73, TypeChart.Grass, MoveCategory.Status, 0, MoveKind.Ailment, Ailment.LeechSeed);
    static readonly MoveInfo Wrap = Move(35, TypeChart.Normal, MoveCategory.Physical, 15, MoveKind.DamageAilment, Ailment.Trap, 100);
    static readonly MoveInfo Reflect = Move(Moves.Reflect, TypeChart.Psychic, MoveCategory.Status, 0, target: 7);
    static readonly MoveInfo Thief = Move(Moves.Thief, TypeChart.Dark, MoveCategory.Physical, 40, flags: 1);
    static readonly MoveInfo UTurn = Move(Moves.UTurn, TypeChart.Bug, MoveCategory.Physical, 70, flags: 1);
    static readonly MoveInfo Flail = Move(Moves.Flail, TypeChart.Normal, MoveCategory.Physical, 1, flags: 1);

    static BattleData Data(int foeType = TypeChart.Normal) => new([Species(1), Species(2, foeType)],
        [Tackle, Splash, SeismicToss, Protect, Fly, HyperBeam, Substitute, Toxic, LeechSeed, Wrap, Reflect, Thief, UTurn, Flail], [[], [], []]);

    static (BattleData, Pokemon, Pokemon) Setup(MoveInfo[] mine, MoveInfo[] theirs, int foeType = TypeChart.Normal)
    {
        var data = Data(foeType);
        var a = Pokemon.Create(data, 1, 40, new Random(1));
        var b = Pokemon.Create(data, 2, 40, new Random(2));
        foreach (var m in mine)
            a.Moves.Add(new MoveSlot(m));
        foreach (var m in theirs)
            b.Moves.Add(new MoveSlot(m));
        // The player's Pokémon always moves first.
        a.Ivs[(int)Stat.Speed] = 31;
        b.Ivs[(int)Stat.Speed] = 0;
        return (data, a, b);
    }

    [Fact]
    public void Seismic_toss_deals_the_users_level_but_not_to_ghosts()
    {
        var (data, mine, wild) = Setup([SeismicToss], [Splash]);
        new SingleBattle(data, mine, wild, new Random(3)).Fight(0);
        Assert.Equal(wild.MaxHp - 40, wild.Hp);

        (data, mine, wild) = Setup([SeismicToss], [Splash], TypeChart.Ghost);
        new SingleBattle(data, mine, wild, new Random(3)).Fight(0);
        Assert.Equal(wild.MaxHp, wild.Hp);
    }

    [Fact]
    public void Protect_blocks_the_hit()
    {
        var (data, mine, wild) = Setup([Tackle], [Protect]);
        var events = new SingleBattle(data, mine, wild, new Random(4)).Fight(0);
        Assert.Equal(wild.MaxHp, wild.Hp);
        Assert.Contains(events, e => e is MessageEvent { Text: var t } && t.Contains("protected itself"));
    }

    [Fact]
    public void Fly_charges_out_of_reach_then_strikes_the_next_turn()
    {
        var (data, mine, wild) = Setup([Fly], [Tackle]);
        var battle = new SingleBattle(data, mine, wild, new Random(5));
        battle.Fight(0);
        Assert.True(battle.PlayerLocked);
        Assert.Equal(mine.MaxHp, mine.Hp);   // the foe's Tackle can't reach it
        Assert.Equal(wild.MaxHp, wild.Hp);
        battle.Fight(0);
        Assert.True(wild.Hp < wild.MaxHp);
        Assert.False(battle.PlayerLocked);
    }

    [Fact]
    public void Hyper_beam_needs_a_turn_to_recharge()
    {
        var (data, mine, wild) = Setup([HyperBeam], [Splash]);
        wild.Hp = 10_000;
        var battle = new SingleBattle(data, mine, wild, new Random(6));
        battle.Fight(0);
        Assert.True(battle.PlayerLocked);
        Assert.Contains(battle.Fight(0), e => e is MessageEvent { Text: var t } && t.Contains("must recharge"));
    }

    [Fact]
    public void Substitute_takes_hits_and_shields_from_status()
    {
        var (data, mine, wild) = Setup([Substitute], [Tackle, Toxic]);
        var battle = new SingleBattle(data, mine, wild, new Random(7));
        battle.Fight(0);
        Assert.Equal(mine.MaxHp - mine.MaxHp / 4, mine.Hp);   // only the substitute's cost, whatever the foe did
        Assert.Equal(Ailment.None, mine.Status);
    }

    [Fact]
    public void Toxic_damage_grows_each_turn()
    {
        var (data, mine, wild) = Setup([Toxic, Splash], [Splash]);
        wild.Hp = wild.MaxHp;
        var battle = new SingleBattle(data, mine, wild, new Random(8));
        battle.Fight(0);
        var first = wild.MaxHp - wild.Hp;
        var before = wild.Hp;
        battle.Fight(1);
        Assert.Equal(Math.Max(1, wild.MaxHp / 16), first);
        Assert.Equal(wild.MaxHp * 2 / 16, before - wild.Hp);
    }

    [Fact]
    public void Leech_seed_saps_the_foe_to_heal_the_user()
    {
        var (data, mine, wild) = Setup([LeechSeed], [Splash]);
        mine.Hp = 10;
        new SingleBattle(data, mine, wild, new Random(9)).Fight(0);
        Assert.Equal(wild.MaxHp - wild.MaxHp / 8, wild.Hp);
        Assert.Equal(10 + wild.MaxHp / 8, mine.Hp);
    }

    [Fact]
    public void Wrap_keeps_the_target_from_running()
    {
        var (data, mine, wild) = Setup([Tackle], [Wrap]);
        mine.Hp = 10_000;
        var battle = new SingleBattle(data, mine, wild, new Random(10));
        battle.Fight(0);
        Assert.Contains(battle.Run(), e => e is MessageEvent { Text: var t } && t.Contains("can't escape"));
        Assert.Equal(BattleOutcome.Ongoing, battle.Outcome);
    }

    [Fact]
    public void Reflect_halves_physical_damage()
    {
        int Taken(bool reflect)
        {
            var (data, mine, wild) = Setup([reflect ? Reflect : Splash, Splash], [Tackle]);
            mine.Hp = 10_000;
            var battle = new SingleBattle(data, mine, wild, new Random(11));
            battle.Fight(0);
            var before = mine.Hp;
            battle.Fight(1);
            return before - mine.Hp;
        }
        Assert.True(Taken(true) < Taken(false));
    }

    [Fact]
    public void Thief_steals_the_foes_item()
    {
        var (data, mine, wild) = Setup([Thief], [Splash]);
        wild.HeldItem = HeldItems.Leftovers;
        new SingleBattle(data, mine, wild, new Random(12)).Fight(0);
        Assert.Equal((HeldItems.Leftovers, 0), (mine.HeldItem, wild.HeldItem));
    }

    [Fact]
    public void U_turn_switches_out_and_the_foe_then_hits_the_newcomer()
    {
        var data = Data();
        var lead = Pokemon.Create(data, 1, 50, new Random(13));
        lead.Moves.Add(new MoveSlot(UTurn));
        var bench = Pokemon.Create(data, 1, 50, new Random(14));
        bench.Moves.Add(new MoveSlot(Tackle));
        var wild = Pokemon.Create(data, 2, 30, new Random(15));
        wild.Moves.Add(new MoveSlot(Tackle));
        wild.Hp = 10_000;
        var battle = new SingleBattle(data, [lead, bench], new Bag(), wild, new Random(16));
        battle.Fight(0);
        Assert.True(battle.NeedsSwitch);
        battle.SendOut(1);
        Assert.Same(bench, battle.Player.Mon);
        Assert.True(bench.Hp < bench.MaxHp);
        Assert.False(battle.NeedsSwitch);
    }

    [Fact]
    public void Flail_hits_harder_at_low_hp()
    {
        int Hit(int hp)
        {
            var (data, mine, wild) = Setup([Flail], [Splash]);
            mine.Hp = hp;
            wild.Hp = 10_000;
            new SingleBattle(data, mine, wild, new Random(17)).Fight(0);
            return 10_000 - wild.Hp;
        }
        Assert.True(Hit(1) > Hit(1_000) * 4);
    }
}
