using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Core.Battle;

public enum Weather { None, Sun, Rain, Sandstorm, Hail }

/// <summary>The weather changed (or ended, with <see cref="Weather.None"/>); the battle screen shows it.</summary>
public sealed record WeatherEvent(Weather Weather) : BattleEvent;

/// <summary>
/// Gen 5 weather: sun and rain power up Fire or Water moves by half and weaken the other; sandstorm and hail hurt
/// everything but Rock/Ground/Steel or Ice types by 1/16 a turn, and sandstorm raises Rock types' Sp. Def by half.
/// Moves set it for five turns, abilities (Drizzle, Drought, Sand Stream, Snow Warning) until it's replaced.
/// </summary>
public sealed partial class SingleBattle
{
    public const int SunnyDay = 241, RainDance = 240, SandstormMove = 201, HailMove = 258, Thunder = 87, Blizzard = 59,
        Hurricane = 542, SolarBeam = 76, WeatherBall = 311, MorningSun = 234, Synthesis = 235, Moonlight = 236;

    /// <summary>The weather on the field (which Cloud Nine or Air Lock may be suppressing; see <see cref="ActiveWeather"/>).</summary>
    public Weather Weather { get; private set; }

    /// <summary>Turns of weather left; 0 means it lasts until something replaces it.</summary>
    int _weatherTurns;

    /// <summary>The weather that takes effect: none while a Pokémon with Cloud Nine or Air Lock is out.</summary>
    public Weather ActiveWeather =>
        Weather != Weather.None && !OnField(Abilities.CloudNine) && !OnField(Abilities.AirLock) ? Weather : Weather.None;

    bool OnField(int ability) => (!Player.Mon.Fainted && Has(Player, ability)) || (!Foe.Mon.Fainted && Has(Foe, ability));

    /// <summary>Starts <paramref name="weather"/> for <paramref name="turns"/> turns (0: until replaced); false if it's already in place.</summary>
    bool SetWeather(Weather weather, int turns, List<BattleEvent> events)
    {
        if (Weather == weather)
            return false;
        Weather = weather;
        _weatherTurns = turns;
        events.Add(new WeatherEvent(weather));
        events.Add(new MessageEvent(weather switch
        {
            Weather.Sun => "The sunlight turned harsh!",
            Weather.Rain => "It started to rain!",
            Weather.Sandstorm => "A sandstorm kicked up!",
            _ => "It started to hail!",
        }));
        return true;
    }

    /// <summary>The weather a move sets, or <see cref="Weather.None"/>.</summary>
    static Weather WeatherMove(MoveInfo move) => move.Id switch
    {
        SunnyDay => Weather.Sun,
        RainDance => Weather.Rain,
        SandstormMove => Weather.Sandstorm,
        HailMove => Weather.Hail,
        _ => Weather.None,
    };

    /// <summary>The weather an entry ability sets, or <see cref="Weather.None"/>.</summary>
    static Weather WeatherAbility(int ability) => ability switch
    {
        Abilities.Drought => Weather.Sun,
        Abilities.Drizzle => Weather.Rain,
        Abilities.SandStream => Weather.Sandstorm,
        Abilities.SnowWarning => Weather.Hail,
        _ => Weather.None,
    };

    /// <summary>Weather Ball's type in the current weather.</summary>
    int WeatherBallType(MoveInfo move) => ActiveWeather switch
    {
        Weather.Sun => TypeChart.Fire,
        Weather.Rain => TypeChart.Water,
        Weather.Sandstorm => TypeChart.Rock,
        Weather.Hail => TypeChart.Ice,
        _ => move.Type,
    };

    /// <summary>A move's power after the weather: Weather Ball doubles in any weather, Solar Beam halves in anything but sun.</summary>
    int WeatherPower(MoveInfo move, int power)
    {
        var weather = ActiveWeather;
        if (move.Id == WeatherBall && weather != Weather.None)
            return power * 2;
        if (move.Id == SolarBeam && weather is Weather.Rain or Weather.Sandstorm or Weather.Hail)
            return power / 2;
        return power;
    }

    /// <summary>The damage multiplier sun and rain give a move of <paramref name="type"/>.</summary>
    double WeatherDamage(int type) => (ActiveWeather, type) switch
    {
        (Weather.Sun, TypeChart.Fire) or (Weather.Rain, TypeChart.Water) => 1.5,
        (Weather.Sun, TypeChart.Water) or (Weather.Rain, TypeChart.Fire) => 0.5,
        _ => 1,
    };

    /// <summary>Thunder and Hurricane never miss in rain (50% in sun); Blizzard never misses in hail. Null leaves accuracy alone.</summary>
    int? WeatherAccuracy(MoveInfo move) => (move.Id, ActiveWeather) switch
    {
        (Thunder or Hurricane, Weather.Rain) => 101,
        (Thunder or Hurricane, Weather.Sun) => 50,
        (Blizzard, Weather.Hail) => 101,
        _ => null,
    };

    /// <summary>Morning Sun, Synthesis and Moonlight: ⅔ of max HP in sun, ¼ in other weather, ½ otherwise.</summary>
    int HealPercent(MoveInfo move) => move.Id is MorningSun or Synthesis or Moonlight
        ? ActiveWeather switch { Weather.None => 50, Weather.Sun => 66, _ => 25 }
        : move.Heal;

    /// <summary>Swift Swim, Chlorophyll and Sand Rush double Speed in their weather.</summary>
    int WeatherSpeed(Battler b, int speed) => (b.Ability, ActiveWeather) switch
    {
        (Abilities.SwiftSwim, Weather.Rain) or (Abilities.Chlorophyll, Weather.Sun) or (Abilities.SandRush, Weather.Sandstorm) => speed * 2,
        _ => speed,
    };

    bool WeatherImmune(Battler b, Weather weather) =>
        Has(b, Abilities.MagicGuard) || Has(b, Abilities.Overcoat) || weather switch
        {
            Weather.Sandstorm => b.Types.Any(t => t is TypeChart.Rock or TypeChart.Ground or TypeChart.Steel)
                || b.Ability is Abilities.SandVeil or Abilities.SandRush or Abilities.SandForce,
            Weather.Hail => b.Types.Contains(TypeChart.Ice) || b.Ability is Abilities.IceBody or Abilities.SnowCloak,
            _ => true,
        };

    /// <summary>
    /// The weather's turn: it counts down (or ends), sandstorm and hail hurt, and the weather abilities act — Rain Dish,
    /// Ice Body, Dry Skin, Solar Power, Hydration.
    /// </summary>
    void WeatherEndOfTurn(List<BattleEvent> events)
    {
        if (Weather == Weather.None || Over)
            return;
        if (_weatherTurns > 0 && --_weatherTurns == 0)
        {
            events.Add(new MessageEvent(Weather switch
            {
                Weather.Sun => "The sunlight faded.",
                Weather.Rain => "The rain stopped.",
                Weather.Sandstorm => "The sandstorm subsided.",
                _ => "The hail stopped.",
            }));
            Weather = Weather.None;
            events.Add(new WeatherEvent(Weather.None));
            return;
        }
        events.Add(new MessageEvent(Weather switch
        {
            Weather.Sun => "The sunlight is strong.",
            Weather.Rain => "Rain continues to fall.",
            Weather.Sandstorm => "The sandstorm rages.",
            _ => "Hail continues to fall.",
        }));
        var weather = ActiveWeather;
        if (weather == Weather.None)
            return;
        foreach (var b in new[] { Player, Foe })
        {
            if (b.Mon.Fainted || Over)
                continue;
            var name = Label(b);
            if (weather is Weather.Sandstorm or Weather.Hail && !WeatherImmune(b, weather))
            {
                Damage(b, Math.Max(1, b.Mon.MaxHp / 16), events,
                    weather == Weather.Sandstorm ? $"{name} is buffeted by the sandstorm!" : $"{name} is pelted by hail!");
                CheckFaints(events);
                continue;
            }
            switch (b.Ability, weather)
            {
                case (Abilities.RainDish, Weather.Rain) or (Abilities.IceBody, Weather.Hail) when b.Mon.Hp < b.Mon.MaxHp:
                    events.Add(Popup(b));
                    Heal(b, Math.Max(1, b.Mon.MaxHp / 16), events, $"{name} restored its HP!");
                    break;
                case (Abilities.DrySkin, Weather.Rain) when b.Mon.Hp < b.Mon.MaxHp:
                    events.Add(Popup(b));
                    Heal(b, Math.Max(1, b.Mon.MaxHp / 8), events, $"{name} restored its HP!");
                    break;
                case (Abilities.DrySkin or Abilities.SolarPower, Weather.Sun) when !Has(b, Abilities.MagicGuard):
                    events.Add(Popup(b));
                    Damage(b, Math.Max(1, b.Mon.MaxHp / 8), events, $"{name} is hurt by the harsh sunlight!");
                    CheckFaints(events);
                    break;
                case (Abilities.Hydration, Weather.Rain) when b.Mon.Status != Ailment.None:
                    events.Add(Popup(b));
                    b.Mon.SleepTurns = 0;
                    SetStatus(b, Ailment.None, events, $"{name}'s status returned to normal!");
                    break;
            }
        }
    }
}
