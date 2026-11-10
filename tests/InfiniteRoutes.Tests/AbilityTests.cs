using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Tests;

public class AbilityTests
{
    static readonly BaseStats NoEvs = new(0, 0, 0, 0, 0, 0);

    static SpeciesInfo Species(int id, int[] abilities, int type = TypeChart.Normal, BaseStats? stats = null, int hidden = 0) =>
        new(id, $"Mon{id}", stats ?? new BaseStats(60, 60, 60, 60, 60, 60), [type.ToString()], 45, NoEvs, 127, 20, 70, 3, [1],
            abilities.Select(a => $"Ability{a}").ToList(), hidden == 0 ? null : $"Ability{hidden}", 64, 6, 81, 1, [type], abilities, hidden);

    static MoveInfo Move(int id, int type, MoveCategory category, int power, MoveKind kind = MoveKind.Damage,
        IReadOnlyList<StatChange>? changes = null, int flags = 0, int target = 0) =>
        new(id, $"Move{id}", type, kind, category, power, 101, 35, 0, 1, 1, Ailment.None, 0, 0, 0, 0, 0, 0, target, changes ?? [], flags);

    static readonly MoveInfo Tackle = Move(33, TypeChart.Normal, MoveCategory.Physical, 50, flags: 1);
    static readonly MoveInfo Earthquake = Move(89, TypeChart.Ground, MoveCategory.Physical, 100);
    static readonly MoveInfo ThunderShock = Move(84, TypeChart.Electric, MoveCategory.Special, 40);
    static readonly MoveInfo VineWhip = Move(22, TypeChart.Grass, MoveCategory.Physical, 45);
    static readonly MoveInfo Growl = Move(45, TypeChart.Normal, MoveCategory.Status, 0, MoveKind.StatChange, [new StatChange(BattleStat.Attack, -1, 0)]);
    static readonly MoveInfo Splash = Move(150, TypeChart.Normal, MoveCategory.Status, 0, MoveKind.StatChange, [new StatChange(BattleStat.Speed, 1, 0)], target: 7);

    static (BattleData Data, Pokemon Mine, Pokemon Theirs) Setup(int mine, int theirs, MoveInfo myMove, MoveInfo theirMove,
        int myType = TypeChart.Normal, int theirType = TypeChart.Normal, int seed = 1)
    {
        var data = new BattleData([Species(1, [mine], myType), Species(2, [theirs], theirType)],
            [Tackle, Earthquake, ThunderShock, VineWhip, Growl, Splash], [[], [], []]);
        var rng = new Random(seed);
        var a = Pokemon.Create(data, 1, 30, rng);
        var b = Pokemon.Create(data, 2, 30, rng);
        a.Moves.Add(new MoveSlot(myMove));
        b.Moves.Add(new MoveSlot(theirMove));
        return (data, a, b);
    }

    [Fact]
    public void Intimidate_lowers_the_foes_attack_on_entry_unless_its_ability_stops_it()
    {
        var (data, mine, wild) = Setup(Abilities.None, Abilities.Intimidate, Tackle, Splash);
        var battle = new SingleBattle(data, mine, wild, new Random(2));
        var events = battle.Start();
        Assert.Contains(events, e => e is AbilityEvent { Side: BattleSide.Foe, Ability: "Ability22" });
        Assert.Equal(-1, battle.Player.Stage(BattleStat.Attack));

        (data, mine, wild) = Setup(Abilities.HyperCutter, Abilities.Intimidate, Tackle, Splash);
        battle = new SingleBattle(data, mine, wild, new Random(2));
        battle.Start();
        Assert.Equal(0, battle.Player.Stage(BattleStat.Attack));

        (data, mine, wild) = Setup(Abilities.Defiant, Abilities.Intimidate, Tackle, Splash);
        battle = new SingleBattle(data, mine, wild, new Random(2));
        battle.Start();
        Assert.Equal(1, battle.Player.Stage(BattleStat.Attack));   // −1, then +2
    }

    [Fact]
    public void Levitate_dodges_ground_moves_but_not_through_mold_breaker()
    {
        var (data, mine, wild) = Setup(Abilities.None, Abilities.Levitate, Earthquake, Splash);
        var battle = new SingleBattle(data, mine, wild, new Random(3));
        battle.Fight(0);
        Assert.Equal(wild.MaxHp, wild.Hp);

        (data, mine, wild) = Setup(Abilities.MoldBreaker, Abilities.Levitate, Earthquake, Splash);
        battle = new SingleBattle(data, mine, wild, new Random(3));
        Assert.Contains(battle.Start(), e => e is MessageEvent { Text: var t } && t.Contains("breaks the mold"));
        battle.Fight(0);
        Assert.True(wild.Hp < wild.MaxHp);
    }

    [Fact]
    public void Volt_absorb_heals_instead_of_taking_damage()
    {
        var (data, mine, wild) = Setup(Abilities.None, Abilities.VoltAbsorb, ThunderShock, Splash);
        wild.Hp = wild.MaxHp / 2;
        var battle = new SingleBattle(data, mine, wild, new Random(4));
        battle.Fight(0);
        Assert.Equal(wild.MaxHp / 2 + wild.MaxHp / 4, wild.Hp);
    }

    [Fact]
    public void Overgrow_powers_up_grass_moves_in_a_pinch()
    {
        int DamageAt(int hp, int ability)
        {
            var (data, mine, wild) = Setup(ability, Abilities.None, VineWhip, Splash);
            mine.Hp = hp;
            var battle = new SingleBattle(data, mine, wild, new Random(5));
            battle.Fight(0);
            return wild.MaxHp - wild.Hp;
        }
        var normal = DamageAt(1000, Abilities.Overgrow);
        Assert.Equal(normal, DamageAt(1000, Abilities.None));
        Assert.True(DamageAt(1, Abilities.Overgrow) > normal);
    }

    [Fact]
    public void Sturdy_survives_a_knockout_blow_from_full_hp()
    {
        var (data, mine, _) = Setup(Abilities.HugePower, Abilities.Sturdy, Tackle, Splash);
        // Level 30 with doubled Attack against a level 2: Tackle would knock it out from full HP.
        var weak = Pokemon.Create(data, 2, 2, new Random(7));
        weak.Moves.Add(new MoveSlot(Splash));
        var battle = new SingleBattle(data, mine, weak, new Random(6));
        var events = battle.Fight(0);
        Assert.Equal(1, weak.Hp);
        Assert.Contains(events, e => e is MessageEvent { Text: var t } && t.Contains("endured the hit"));
    }

    [Fact]
    public void Static_can_paralyze_a_pokemon_that_touches_it()
    {
        var (data, mine, wild) = Setup(Abilities.None, Abilities.Static, Tackle, Splash);
        wild.Hp = 10_000;   // never faints
        var battle = new SingleBattle(data, mine, wild, new Random(8));
        for (var turn = 0; turn < 40 && mine.Status == Ailment.None; turn++)
            battle.Fight(0);
        Assert.Equal(Ailment.Paralysis, mine.Status);
    }

    [Fact]
    public void Speed_boost_raises_speed_after_each_turn_but_the_first()
    {
        var (data, mine, wild) = Setup(Abilities.SpeedBoost, Abilities.None, Growl, Growl);
        var battle = new SingleBattle(data, mine, wild, new Random(9));
        battle.Fight(0);
        Assert.Equal(0, battle.Player.Stage(BattleStat.Speed));
        battle.Fight(0);
        battle.Fight(0);
        Assert.Equal(2, battle.Player.Stage(BattleStat.Speed));
    }

    [Fact]
    public void Shadow_tag_stops_running_and_run_away_always_escapes()
    {
        var (data, mine, wild) = Setup(Abilities.None, Abilities.ShadowTag, Tackle, Splash);
        var battle = new SingleBattle(data, mine, wild, new Random(10));
        Assert.Contains(battle.Run(), e => e is MessageEvent { Text: var t } && t.Contains("prevents escape"));
        Assert.Equal(BattleOutcome.Ongoing, battle.Outcome);

        (data, mine, wild) = Setup(Abilities.RunAway, Abilities.ShadowTag, Tackle, Splash);
        Array.Fill(wild.Ivs, 31);
        battle = new SingleBattle(data, mine, wild, new Random(10));
        battle.Run();
        Assert.Equal(BattleOutcome.Escaped, battle.Outcome);
    }

    [Fact]
    public void Ability_slot_is_saved_and_trainers_can_choose_it()
    {
        var data = new BattleData([Species(1, [Abilities.Overgrow, Abilities.Static], hidden: Abilities.Moxie)], [Tackle], [[], []]);
        var mon = Pokemon.Create(data, 1, 10, new Random(11));
        mon.SetAbilitySlot(1);
        Assert.Equal((Abilities.Static, "Ability9"), (mon.AbilityId, mon.AbilityName));
        Assert.Equal(Abilities.Static, Pokemon.FromSave(data, mon.ToSave())!.AbilityId);

        var trainer = new TrainerInfo(1, "Ann", 1, "Lass", 0, [], 0, 10, [new TrainerPokemon(1, 0, 10, 0, 0x30, 0, null)]);
        Assert.Equal(Abilities.Moxie, SingleBattle.TrainerParty(data, trainer, new Random(12))[0].AbilityId);
    }
}
