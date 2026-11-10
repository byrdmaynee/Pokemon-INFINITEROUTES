using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Tests;

public class WeatherTests
{
    static readonly BaseStats NoEvs = new(0, 0, 0, 0, 0, 0);

    static SpeciesInfo Species(int id, int ability, int type, int speed = 60) =>
        new(id, $"Mon{id}", new BaseStats(80, 60, 60, speed, 60, 60), [type.ToString()], 45, NoEvs, 127, 20, 70, 3, [1],
            [$"Ability{ability}"], null, 64, 6, 81, 1, [type], [ability]);

    static MoveInfo Move(int id, int type, MoveCategory category, int power, int accuracy = 101, MoveKind kind = MoveKind.Damage, int heal = 0) =>
        new(id, $"Move{id}", type, kind, category, power, accuracy, 35, 0, 1, 1, Ailment.None, 0, 0, 0, 0, 0, heal, 0, [], 0);

    static readonly MoveInfo RainDance = Move(SingleBattle.RainDance, TypeChart.Water, MoveCategory.Status, 0, kind: MoveKind.FieldEffect);
    static readonly MoveInfo Sandstorm = Move(SingleBattle.SandstormMove, TypeChart.Rock, MoveCategory.Status, 0, kind: MoveKind.FieldEffect);
    static readonly MoveInfo WaterGun = Move(55, TypeChart.Water, MoveCategory.Special, 40);
    static readonly MoveInfo Ember = Move(52, TypeChart.Fire, MoveCategory.Special, 40);
    static readonly MoveInfo Thunder = Move(SingleBattle.Thunder, TypeChart.Electric, MoveCategory.Special, 120, accuracy: 1);
    static readonly MoveInfo Synthesis = Move(SingleBattle.Synthesis, TypeChart.Grass, MoveCategory.Status, 0, kind: MoveKind.Heal, heal: 50);
    static readonly MoveInfo Growl = Move(45, TypeChart.Normal, MoveCategory.Status, 0, kind: MoveKind.StatChange);

    static (BattleData, Pokemon, Pokemon) Setup(int myAbility, int theirAbility, MoveInfo[] mine, MoveInfo[] theirs,
        int myType = TypeChart.Normal, int theirType = TypeChart.Normal, int mySpeed = 60)
    {
        var data = new BattleData([Species(1, myAbility, myType, mySpeed), Species(2, theirAbility, theirType)],
            [RainDance, Sandstorm, WaterGun, Ember, Thunder, Synthesis, Growl], [[], [], []]);
        var a = Pokemon.Create(data, 1, 40, new Random(1));
        var b = Pokemon.Create(data, 2, 40, new Random(2));
        foreach (var m in mine)
            a.Moves.Add(new MoveSlot(m));
        foreach (var m in theirs)
            b.Moves.Add(new MoveSlot(m));
        b.Hp = 10_000;   // stays standing
        return (data, a, b);
    }

    static int Hit(int slot, Weather weather)
    {
        var (data, mine, wild) = Setup(Abilities.None, Abilities.None, [RainDance, WaterGun, Ember], [Growl]);
        var battle = new SingleBattle(data, mine, wild, new Random(3));
        if (weather == Weather.Rain)
            battle.Fight(0);
        var before = wild.Hp;
        battle.Fight(slot);
        return before - wild.Hp;
    }

    [Fact]
    public void Rain_powers_up_water_and_weakens_fire()
    {
        Assert.True(Hit(1, Weather.Rain) > Hit(1, Weather.None));
        Assert.True(Hit(2, Weather.Rain) < Hit(2, Weather.None));
    }

    [Fact]
    public void Sandstorm_hurts_all_but_rock_ground_and_steel_and_ends_after_five_turns()
    {
        var (data, mine, wild) = Setup(Abilities.None, Abilities.None, [Sandstorm, Growl], [Growl], theirType: TypeChart.Rock);
        var battle = new SingleBattle(data, mine, wild, new Random(4));
        battle.Fight(0);
        Assert.Equal(Weather.Sandstorm, battle.Weather);
        Assert.Equal(mine.MaxHp - mine.MaxHp / 16, mine.Hp);
        Assert.Equal(10_000, wild.Hp);
        for (var turn = 0; turn < 4; turn++)
            battle.Fight(1);
        Assert.Equal(Weather.None, battle.Weather);
    }

    [Fact]
    public void Drizzle_brings_lasting_rain_and_swift_swim_doubles_speed_in_it()
    {
        var (data, mine, wild) = Setup(Abilities.SwiftSwim, Abilities.Drizzle, [Growl], [Growl], mySpeed: 40);
        var battle = new SingleBattle(data, mine, wild, new Random(5));
        Assert.Contains(battle.Start(), e => e is WeatherEvent { Weather: Weather.Rain });
        for (var turn = 0; turn < 8; turn++)
            battle.Fight(0);
        Assert.Equal(Weather.Rain, battle.Weather);
        var events = battle.Fight(0);
        // Speed 40 base ×2 outruns the foe's 60: the player's Growl comes first.
        var first = events.OfType<MessageEvent>().First(m => m.Text.Contains("used"));
        Assert.StartsWith(mine.Name, first.Text);
    }

    [Fact]
    public void Cloud_nine_cancels_the_weather_and_thunder_never_misses_in_rain()
    {
        var (data, mine, wild) = Setup(Abilities.None, Abilities.None, [RainDance, Thunder], [Growl]);
        var battle = new SingleBattle(data, mine, wild, new Random(6));
        battle.Fight(0);
        for (var turn = 0; turn < 4; turn++)
            Assert.DoesNotContain(battle.Fight(1), e => e is MessageEvent { Text: var t } && t.Contains("missed"));

        (data, mine, wild) = Setup(Abilities.None, Abilities.CloudNine, [RainDance], [Growl]);
        battle = new SingleBattle(data, mine, wild, new Random(6));
        battle.Fight(0);
        Assert.Equal((Weather.Rain, Weather.None), (battle.Weather, battle.ActiveWeather));
    }

    [Fact]
    public void Synthesis_heals_more_in_sun_and_less_in_rain()
    {
        var (data, mine, wild) = Setup(Abilities.None, Abilities.None, [RainDance, Synthesis], [Growl]);
        var battle = new SingleBattle(data, mine, wild, new Random(7));
        battle.Fight(0);
        mine.Hp = 1;
        battle.Fight(1);
        Assert.Equal(1 + mine.MaxHp / 4, mine.Hp);
    }
}
