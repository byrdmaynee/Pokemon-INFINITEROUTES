namespace InfiniteRoutes.Core.Battle;

/// <summary>Item ids (the ROM's item table) of the items that do something when a Pokémon holds them in battle.</summary>
public static class HeldItems
{
    public const int CheriBerry = 149, ChestoBerry = 150, PechaBerry = 151, RawstBerry = 152, AspearBerry = 153, LeppaBerry = 154,
        OranBerry = 155, PersimBerry = 156, LumBerry = 157, SitrusBerry = 158, FigyBerry = 159, IapapaBerry = 163,
        OccaBerry = 184, ChilanBerry = 200, LiechiBerry = 201, GanlonBerry = 202, SalacBerry = 203, PetayaBerry = 204,
        ApicotBerry = 205, RowapBerry = 212,
        BrightPowder = 213, WhiteHerb = 214, MachoBrace = 215, ExpShare = 216, QuickClaw = 217, ChoiceBand = 220,
        KingsRock = 221, AmuletCoin = 223, SoulDew = 225, DeepSeaTooth = 226, DeepSeaScale = 227, SmokeBall = 228,
        FocusBand = 230, LuckyEgg = 231, ScopeLens = 232, Leftovers = 234, LightBall = 236, ShellBell = 253, LaxIncense = 255,
        LuckyPunch = 256, MetalPowder = 257, ThickClub = 258, Stick = 259, WideLens = 265, MuscleBand = 266, WiseGlasses = 267,
        ExpertBelt = 268, LifeOrb = 270, ToxicOrb = 272, FlameOrb = 273, QuickPowder = 274, FocusSash = 275, ZoomLens = 276,
        IronBall = 278, LaggingTail = 279, BlackSludge = 281, IcyRock = 282, SmoothRock = 283, HeatRock = 284, DampRock = 285,
        ChoiceScarf = 287, PowerBracer = 289, PowerWeight = 294, BigRoot = 296, ChoiceSpecs = 297, FullIncense = 316,
        LuckIncense = 319, RazorClaw = 326, RazorFang = 327, RockyHelmet = 540, AirBalloon = 541, AbsorbBulb = 545,
        CellBattery = 546, FireGem = 548, NormalGem = 564;

    /// <summary>The type order gems (548–564) and type-resist berries (184–200) follow.</summary>
    static readonly int[] GemOrder =
    [
        TypeChart.Fire, TypeChart.Water, TypeChart.Electric, TypeChart.Grass, TypeChart.Ice, TypeChart.Fighting, TypeChart.Poison,
        TypeChart.Ground, TypeChart.Flying, TypeChart.Psychic, TypeChart.Bug, TypeChart.Rock, TypeChart.Ghost, TypeChart.Dragon,
        TypeChart.Dark, TypeChart.Steel, TypeChart.Normal,
    ];

    /// <summary>The type a gem powers up ×1.5 once, or −1.</summary>
    public static int GemType(int item) => item is >= FireGem and <= NormalGem ? GemOrder[item - FireGem] : -1;

    /// <summary>The type whose super-effective hit (any Normal hit, for Chilan) a berry halves once, or −1.</summary>
    public static int ResistBerryType(int item) => item is >= OccaBerry and <= ChilanBerry ? GemOrder[item - OccaBerry] : -1;

    public static bool IsBerry(int item) => item is >= CheriBerry and <= RowapBerry;

    /// <summary>The type an item powers up ×1.2 (type items, Plates, incenses), or −1.</summary>
    public static int BoostedType(int item) => item switch
    {
        251 => TypeChart.Normal,
        241 or 303 => TypeChart.Fighting,
        244 or 306 => TypeChart.Flying,
        245 or 304 => TypeChart.Poison,
        237 or 305 => TypeChart.Ground,
        238 or 309 or 315 => TypeChart.Rock,
        222 or 308 => TypeChart.Bug,
        247 or 310 => TypeChart.Ghost,
        233 or 313 => TypeChart.Steel,
        249 or 298 => TypeChart.Fire,
        243 or 299 or 254 or 317 => TypeChart.Water,
        239 or 301 or 318 => TypeChart.Grass,
        242 or 300 => TypeChart.Electric,
        248 or 307 or 314 => TypeChart.Psychic,
        246 or 302 => TypeChart.Ice,
        250 or 311 => TypeChart.Dragon,
        240 or 312 => TypeChart.Dark,
        _ => -1,
    };

    /// <summary>The ailment a status berry cures (Lum: any, returned as <see cref="Data.Ailment.None"/> with true).</summary>
    public static Data.Ailment? CuredBy(int item) => item switch
    {
        CheriBerry => Data.Ailment.Paralysis,
        ChestoBerry => Data.Ailment.Sleep,
        PechaBerry => Data.Ailment.Poison,
        RawstBerry => Data.Ailment.Burn,
        AspearBerry => Data.Ailment.Freeze,
        LumBerry => Data.Ailment.None,
        _ => null,
    };

    /// <summary>The stat a pinch berry raises at ¼ HP, or <see cref="Data.BattleStat.None"/>.</summary>
    public static Data.BattleStat PinchStat(int item) => item switch
    {
        LiechiBerry => Data.BattleStat.Attack,
        GanlonBerry => Data.BattleStat.Defense,
        SalacBerry => Data.BattleStat.Speed,
        PetayaBerry => Data.BattleStat.SpAttack,
        ApicotBerry => Data.BattleStat.SpDefense,
        _ => Data.BattleStat.None,
    };

    /// <summary>The weather a rock makes last 8 turns instead of 5.</summary>
    public static Weather RockWeather(int item) => item switch
    {
        HeatRock => Weather.Sun,
        DampRock => Weather.Rain,
        SmoothRock => Weather.Sandstorm,
        IcyRock => Weather.Hail,
        _ => Weather.None,
    };

    /// <summary>Items that halve Speed: Iron Ball, Macho Brace and the Power items.</summary>
    public static bool Slows(int item) => item is IronBall or MachoBrace or >= PowerBracer and <= PowerWeight;
}
