namespace InfiniteRoutes.Core.Battle;

/// <summary>
/// Generation 5 type effectiveness, by the ROM's type ids: 0 Normal, 1 Fighting, 2 Flying, 3 Poison, 4 Ground, 5 Rock,
/// 6 Bug, 7 Ghost, 8 Steel, 9 Fire, 10 Water, 11 Grass, 12 Electric, 13 Psychic, 14 Ice, 15 Dragon, 16 Dark.
/// </summary>
public static class TypeChart
{
    public const int Count = 17;
    public const int Normal = 0, Fighting = 1, Flying = 2, Poison = 3, Ground = 4, Rock = 5, Bug = 6, Ghost = 7, Steel = 8,
        Fire = 9, Water = 10, Grass = 11, Electric = 12, Psychic = 13, Ice = 14, Dragon = 15, Dark = 16;

    // Rows: attacking type; columns: defending type. 0 no effect, h half, 1 normal, 2 double.
    static readonly string[] Rows =
    [
        "11111h10h11111111", // Normal
        "21hh12h021111h212", // Fighting
        "12111h21h112h1111", // Flying
        "111hhh1h011211111", // Poison
        "110212h1221h21111", // Ground
        "1h21h121h21111211", // Rock
        "1hhh111hhh1212112", // Bug
        "01111112h1111211h", // Ghost (Gen 5: Steel still resists Ghost and Dark)
        "11111211hhh1h1211", // Steel
        "11111h212hh2112h1", // Fire
        "1111221112hh111h1", // Water
        "11hh22h1hh2h111h1", // Grass
        "11210111112hh11h1", // Electric
        "12121111h1111h110", // Psychic
        "11212111hhh211h21", // Ice
        "11111111h11111121", // Dragon
        "1h111112h1111211h", // Dark
    ];

    /// <summary>Damage multiplier of an attack of <paramref name="attack"/> type on one defending type.</summary>
    public static double Single(int attack, int defend)
    {
        if (attack < 0 || attack >= Count || defend < 0 || defend >= Count)
            return 1;
        return Rows[attack][defend] switch { '0' => 0, 'h' => 0.5, '2' => 2, _ => 1 };
    }

    /// <summary>Combined multiplier against every type of the defender (0, 0.25, 0.5, 1, 2 or 4).</summary>
    public static double Against(int attack, IEnumerable<int> defenderTypes) =>
        defenderTypes.Aggregate(1.0, (m, t) => m * Single(attack, t));
}
