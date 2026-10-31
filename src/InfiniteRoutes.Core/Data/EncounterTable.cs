namespace InfiniteRoutes.Core.Data;

/// <summary>One wild encounter slot. <see cref="Form"/> is the species' alternate form (0 normal).</summary>
public sealed record EncounterSlot(int Species, int Form, int MinLevel, int MaxLevel);

/// <summary>
/// A zone's wild Pokémon for one season. Each rate is the chance an encounter check succeeds on a step
/// (0 = none of that kind here); each slot list is in the ROM's order, so <see cref="GrassWeights"/>, <see cref="SurfWeights"/> and <see cref="FishWeights"/> apply.
/// </summary>
public sealed record EncounterSet(
    int GrassRate, int DoubleGrassRate, int ShakingGrassRate, int SurfRate, int SurfSpotRate, int FishRate, int FishSpotRate,
    IReadOnlyList<EncounterSlot> Grass,
    IReadOnlyList<EncounterSlot> DoubleGrass,
    IReadOnlyList<EncounterSlot> ShakingGrass,
    IReadOnlyList<EncounterSlot> Surf,
    IReadOnlyList<EncounterSlot> SurfSpots,
    IReadOnlyList<EncounterSlot> Fish,
    IReadOnlyList<EncounterSlot> FishSpots)
{
    /// <summary>Percent chance of each grass slot (12 slots) and each water slot (5 slots), as in Black/White.</summary>
    public static readonly int[] GrassWeights = [20, 20, 10, 10, 10, 10, 5, 5, 4, 4, 1, 1];
    public static readonly int[] SurfWeights = [60, 30, 5, 4, 1];
    public static readonly int[] FishWeights = [40, 40, 15, 4, 1];

    /// <summary>Picks a slot by its weight from <paramref name="slots"/>; null if every slot is empty.</summary>
    public static EncounterSlot? Pick(IReadOnlyList<EncounterSlot> slots, IReadOnlyList<int> weights, Random rng)
    {
        var total = 0;
        for (var i = 0; i < slots.Count && i < weights.Count; i++)
            if (slots[i].Species > 0)
                total += weights[i];
        if (total == 0)
            return null;
        var roll = rng.Next(total);
        for (var i = 0; i < slots.Count && i < weights.Count; i++)
        {
            if (slots[i].Species <= 0)
                continue;
            if (roll < weights[i])
                return slots[i];
            roll -= weights[i];
        }
        return null;
    }
}

/// <summary>A zone's encounter table: one set, or four (spring, summer, autumn, winter) for seasonal routes.</summary>
public sealed record EncounterTable(int Id, IReadOnlyList<EncounterSet> Seasons)
{
    public EncounterSet ForSeason(int season) => Seasons[season >= 0 && season < Seasons.Count ? season : 0];
}
