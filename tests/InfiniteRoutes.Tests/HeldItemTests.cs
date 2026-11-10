using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Tests;

public class HeldItemTests
{
    static readonly BaseStats NoEvs = new(0, 0, 0, 0, 0, 0);

    static SpeciesInfo Species(int id, int type = TypeChart.Normal, int[]? wildItems = null) =>
        new(id, $"Mon{id}", new BaseStats(80, 60, 60, 60, 60, 60), [type.ToString()], 45, NoEvs, 127, 20, 70, 3, [1],
            ["None"], null, 100, 6, 81, 1, [type], [0], 0, wildItems);

    static MoveInfo Move(int id, int type, MoveCategory category, int power, MoveKind kind = MoveKind.Damage,
        IReadOnlyList<StatChange>? changes = null, Ailment ailment = Ailment.None, int flags = 0) =>
        new(id, $"Move{id}", type, kind, category, power, 101, 35, 0, 1, 1, ailment, 0, 0, 0, 0, 0, 0, 0, changes ?? [], flags);

    static readonly MoveInfo Tackle = Move(33, TypeChart.Normal, MoveCategory.Physical, 50, flags: 1);
    static readonly MoveInfo Scratch = Move(10, TypeChart.Normal, MoveCategory.Physical, 40, flags: 1);
    static readonly MoveInfo Ember = Move(52, TypeChart.Fire, MoveCategory.Special, 40);
    static readonly MoveInfo Earthquake = Move(89, TypeChart.Ground, MoveCategory.Physical, 100);
    static readonly MoveInfo Growl = Move(45, TypeChart.Normal, MoveCategory.Status, 0, MoveKind.StatChange, [new StatChange(BattleStat.Attack, -1, 0)]);
    static readonly MoveInfo ThunderWave = Move(86, TypeChart.Electric, MoveCategory.Status, 0, MoveKind.Ailment, ailment: Ailment.Paralysis);

    static (BattleData, Pokemon, Pokemon) Setup(MoveInfo[] mine, MoveInfo[] theirs, int theirType = TypeChart.Normal, int level = 30)
    {
        var data = new BattleData([Species(1), Species(2, theirType), Species(3, wildItems: [HeldItems.OranBerry, HeldItems.OranBerry, 0])],
            [Tackle, Scratch, Ember, Earthquake, Growl, ThunderWave], [[], [], [], []]);
        var a = Pokemon.Create(data, 1, level, new Random(1));
        var b = Pokemon.Create(data, 2, level, new Random(2));
        foreach (var m in mine)
            a.Moves.Add(new MoveSlot(m));
        foreach (var m in theirs)
            b.Moves.Add(new MoveSlot(m));
        return (data, a, b);
    }

    static int Hit(int item, MoveInfo move, int theirType = TypeChart.Normal, int theirItem = 0)
    {
        var (data, mine, wild) = Setup([move], [Growl], theirType);
        mine.HeldItem = item;
        wild.HeldItem = theirItem;
        wild.Hp = 10_000;
        new SingleBattle(data, mine, wild, new Random(3)).Fight(0);
        return 10_000 - wild.Hp;
    }

    [Fact]
    public void Power_items_raise_damage()
    {
        var plain = Hit(0, Tackle);
        Assert.True(Hit(HeldItems.ChoiceBand, Tackle) > plain);
        Assert.True(Hit(251, Tackle) > plain);   // Silk Scarf
        Assert.True(Hit(HeldItems.LifeOrb, Tackle) > plain);
        Assert.True(Hit(HeldItems.NormalGem, Tackle) > plain);
        Assert.True(Hit(0, Ember, TypeChart.Grass, theirItem: HeldItems.OccaBerry) < Hit(0, Ember, TypeChart.Grass));
    }

    [Fact]
    public void Choice_items_lock_the_first_move_used()
    {
        var (data, mine, wild) = Setup([Tackle, Scratch], [Growl]);
        mine.HeldItem = HeldItems.ChoiceScarf;
        wild.Hp = 10_000;
        var battle = new SingleBattle(data, mine, wild, new Random(4));
        battle.Fight(0);
        Assert.Contains(battle.Fight(1), e => e is MessageEvent { Text: var t } && t.Contains("can only use"));
        Assert.DoesNotContain(battle.Fight(0), e => e is MessageEvent { Text: var t } && t.Contains("can only use"));
    }

    [Fact]
    public void Leftovers_heal_and_berries_are_eaten_once()
    {
        var (data, mine, wild) = Setup([Growl], [Growl]);
        mine.HeldItem = HeldItems.Leftovers;
        mine.Hp = 10;
        var battle = new SingleBattle(data, mine, wild, new Random(5));
        battle.Fight(0);
        Assert.Equal(10 + mine.MaxHp / 16, mine.Hp);

        (data, mine, wild) = Setup([Tackle], [Tackle]);
        wild.HeldItem = HeldItems.SitrusBerry;
        wild.Hp = wild.MaxHp / 2 + 3;
        battle = new SingleBattle(data, mine, wild, new Random(6));
        battle.Fight(0);
        Assert.Equal(0, wild.HeldItem);
        Assert.True(wild.Hp > wild.MaxHp / 2 - 30);
    }

    [Fact]
    public void Lum_berry_cures_a_status_right_away()
    {
        var (data, mine, wild) = Setup([ThunderWave], [Growl]);
        wild.HeldItem = HeldItems.LumBerry;
        new SingleBattle(data, mine, wild, new Random(7)).Fight(0);
        Assert.Equal((Ailment.None, 0), (wild.Status, wild.HeldItem));
    }

    [Fact]
    public void Focus_sash_survives_a_knockout_from_full_hp_and_air_balloon_dodges_ground()
    {
        var (data, mine, wild) = Setup([Earthquake], [Growl], level: 60);
        var weak = Pokemon.Create(data, 2, 3, new Random(8));
        weak.Moves.Add(new MoveSlot(Growl));
        weak.HeldItem = HeldItems.FocusSash;
        new SingleBattle(data, mine, weak, new Random(8)).Fight(0);
        Assert.Equal((1, 0), (weak.Hp, weak.HeldItem));

        (data, mine, wild) = Setup([Earthquake], [Growl]);
        wild.HeldItem = HeldItems.AirBalloon;
        var battle = new SingleBattle(data, mine, wild, new Random(9));
        Assert.Contains(battle.Start(), e => e is MessageEvent { Text: var t } && t.Contains("floats in the air"));
        battle.Fight(0);
        Assert.Equal(wild.MaxHp, wild.Hp);
    }

    [Fact]
    public void Exp_share_gives_its_holder_half_and_lucky_egg_adds_half()
    {
        var (data, mine, wild) = Setup([Tackle], [Growl], level: 50);
        var bench = Pokemon.Create(data, 1, 5, new Random(10));
        bench.HeldItem = HeldItems.ExpShare;
        var weak = Pokemon.Create(data, 2, 5, new Random(11));
        weak.Moves.Add(new MoveSlot(Growl));
        var before = bench.Exp;
        new SingleBattle(data, [mine, bench], new Bag(), weak, new Random(12)).Fight(0);
        Assert.True(bench.Exp > before);

        var plain = Pokemon.Create(data, 1, 5, new Random(13));
        var lucky = Pokemon.Create(data, 1, 5, new Random(13));
        lucky.HeldItem = HeldItems.LuckyEgg;
        Assert.True(SingleBattle.ExpYield(weak, lucky) > SingleBattle.ExpYield(weak, plain));
    }

    [Fact]
    public void Wild_pokemon_may_hold_their_species_items_and_keep_them_in_saves()
    {
        var (data, _, _) = Setup([Tackle], [Growl]);
        var mon = Pokemon.Create(data, 3, 10, new Random(14));
        Assert.Equal(HeldItems.OranBerry, mon.HeldItem);   // both slots name the same item: always held
        Assert.Equal(HeldItems.OranBerry, Pokemon.FromSave(data, mon.ToSave())!.HeldItem);
    }
}
