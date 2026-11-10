namespace InfiniteRoutes.Core.Battle;

/// <summary>Ability ids, as numbered in the ROM's ability table (personal bytes 0x18–0x1A).</summary>
public static class Abilities
{
    public const int None = 0, Stench = 1, Drizzle = 2, SpeedBoost = 3, SandVeil = 8, CloudNine = 13, SwiftSwim = 33,
        Chlorophyll = 34, RainDish = 44, SandStream = 45, Drought = 70, AirLock = 76, SnowCloak = 81, Hydration = 93,
        SolarPower = 94, LeafGuard = 102, IceBody = 115, SnowWarning = 117, Overcoat = 142, SandRush = 146, SandForce = 159,
        Gluttony = 82, Unburden = 84, Klutz = 103, Frisk = 119, Unnerve = 127, Harvest = 139, BattleArmor = 4, Sturdy = 5, Limber = 7, Static = 9, VoltAbsorb = 10,
        WaterAbsorb = 11, CompoundEyes = 14, Insomnia = 15, Immunity = 17, FlashFire = 18, ShieldDust = 19, OwnTempo = 20,
        Intimidate = 22, ShadowTag = 23, RoughSkin = 24, WonderGuard = 25, Levitate = 26, EffectSpore = 27, Synchronize = 28,
        ClearBody = 29, NaturalCure = 30, LightningRod = 31, SereneGrace = 32, Trace = 36, HugePower = 37, PoisonPoint = 38,
        InnerFocus = 39, MagmaArmor = 40, WaterVeil = 41, MagnetPull = 42, Pressure = 46, ThickFat = 47, EarlyBird = 48,
        FlameBody = 49, RunAway = 50, KeenEye = 51, HyperCutter = 52, Truant = 54, Hustle = 55, ShedSkin = 61, Guts = 62,
        MarvelScale = 63, LiquidOoze = 64, Overgrow = 65, Blaze = 66, Torrent = 67, Swarm = 68, RockHead = 69, ArenaTrap = 71,
        VitalSpirit = 72, WhiteSmoke = 73, PurePower = 74, ShellArmor = 75, MotorDrive = 78, Rivalry = 79, Steadfast = 80,
        AngerPoint = 83, Heatproof = 85, Simple = 86, DrySkin = 87, Download = 88, PoisonHeal = 90, Adaptability = 91,
        SkillLink = 92, QuickFeet = 95, Normalize = 96, Sniper = 97, MagicGuard = 98, NoGuard = 99, Stall = 100,
        Technician = 101, MoldBreaker = 104, SuperLuck = 105, Aftermath = 106, Unaware = 109, TintedLens = 110, Filter = 111,
        SlowStart = 112, Scrappy = 113, StormDrain = 114, SolidRock = 116, Reckless = 120, Multitype = 121, FlowerGift = 122,
        SheerForce = 125, Contrary = 126, Defiant = 128, Defeatist = 129, WeakArmor = 133, Multiscale = 136, ToxicBoost = 137,
        FlareBoost = 138, PoisonTouch = 143, Regenerator = 144, BigPecks = 145, WonderSkin = 147, Analytic = 148, Illusion = 149,
        Imposter = 150, Moxie = 153, Justified = 154, Rattled = 155, SapSipper = 157, Prankster = 158, IronBarbs = 160,
        ZenMode = 161, VictoryStar = 162, Turboblaze = 163, Teravolt = 164;

    /// <summary>Abilities that work in battle so far (the rest need held items, double battles or the field).</summary>
    public static readonly IReadOnlySet<int> Implemented = new HashSet<int>
    {
        Drizzle, SandVeil, CloudNine, SwiftSwim, Chlorophyll, RainDish, SandStream, Drought, AirLock, SnowCloak, Hydration,
        SolarPower, LeafGuard, IceBody, SnowWarning, Overcoat, SandRush, SandForce, FlowerGift,
        Gluttony, Unburden, Klutz, Frisk, Unnerve, Harvest,
        Stench, SpeedBoost, BattleArmor, Sturdy, Limber, Static, VoltAbsorb, WaterAbsorb, CompoundEyes, Insomnia, Immunity,
        FlashFire, ShieldDust, OwnTempo, Intimidate, ShadowTag, RoughSkin, WonderGuard, Levitate, EffectSpore, Synchronize,
        ClearBody, NaturalCure, LightningRod, SereneGrace, Trace, HugePower, PoisonPoint, InnerFocus, MagmaArmor, WaterVeil,
        MagnetPull, Pressure, ThickFat, EarlyBird, FlameBody, RunAway, KeenEye, HyperCutter, Truant, Hustle, ShedSkin, Guts,
        MarvelScale, LiquidOoze, Overgrow, Blaze, Torrent, Swarm, RockHead, ArenaTrap, VitalSpirit, WhiteSmoke, PurePower,
        ShellArmor, MotorDrive, Rivalry, Steadfast, AngerPoint, Heatproof, Simple, DrySkin, Download, PoisonHeal, Adaptability,
        SkillLink, QuickFeet, Normalize, Sniper, MagicGuard, NoGuard, Stall, Technician, MoldBreaker, SuperLuck, Aftermath,
        Unaware, TintedLens, Filter, SlowStart, Scrappy, StormDrain, SolidRock, Reckless, SheerForce, Contrary, Defiant,
        Defeatist, WeakArmor, Multiscale, ToxicBoost, FlareBoost, PoisonTouch, Regenerator, BigPecks, WonderSkin, Analytic,
        Moxie, Justified, Rattled, SapSipper, Prankster, IronBarbs, VictoryStar, Turboblaze, Teravolt,
    };

    /// <summary>Mold Breaker and its Unova versions: the user's moves ignore the target's <see cref="Breakable"/> abilities.</summary>
    public static bool BreaksMold(int ability) => ability is MoldBreaker or Turboblaze or Teravolt;

    /// <summary>Defensive abilities that Mold Breaker ignores.</summary>
    public static readonly IReadOnlySet<int> Breakable = new HashSet<int>
    {
        BattleArmor, ShellArmor, Sturdy, Limber, VoltAbsorb, WaterAbsorb, Insomnia, VitalSpirit, Immunity, FlashFire, ShieldDust,
        OwnTempo, WonderGuard, Levitate, ClearBody, WhiteSmoke, LightningRod, InnerFocus, MagmaArmor, WaterVeil, ThickFat,
        KeenEye, HyperCutter, BigPecks, MarvelScale, MotorDrive, Heatproof, Simple, Contrary, DrySkin, Unaware, Filter, SolidRock,
        StormDrain, SapSipper, Multiscale, WonderSkin, SandVeil, SnowCloak, LeafGuard, FlowerGift,
    };

    /// <summary>Abilities Trace can't copy.</summary>
    public static bool Untraceable(int ability) => ability is None or Trace or Multitype or FlowerGift or Illusion or Imposter or ZenMode;

    /// <summary>The type an attacker's pinch ability (at ⅓ HP or less) powers up, or −1.</summary>
    public static int PinchType(int ability) => ability switch
    {
        Overgrow => TypeChart.Grass,
        Blaze => TypeChart.Fire,
        Torrent => TypeChart.Water,
        Swarm => TypeChart.Bug,
        _ => -1,
    };
}
