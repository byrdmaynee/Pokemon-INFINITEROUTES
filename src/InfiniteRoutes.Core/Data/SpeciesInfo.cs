namespace InfiniteRoutes.Core.Data;

public sealed record BaseStats(int Hp, int Attack, int Defense, int Speed, int SpAttack, int SpDefense)
{
    public int Total => Hp + Attack + Defense + Speed + SpAttack + SpDefense;
}

/// <summary>
/// Per-species data as extracted from a ROM. Names are resolved from the player's own ROM text,
/// so they follow the ROM's language. <see cref="AbilityIds"/> runs parallel to <see cref="Abilities"/> (the battle
/// engine works by id, see <see cref="Battle.Abilities"/>); <see cref="HiddenAbilityId"/> is 0 when there is none.
/// </summary>
public sealed record SpeciesInfo(
    int Id,
    string Name,
    BaseStats Stats,
    IReadOnlyList<string> Types,
    int CatchRate,
    BaseStats EvYield,
    int GenderRatio,
    int HatchCycles,
    int BaseFriendship,
    int GrowthRate,
    IReadOnlyList<int> EggGroups,
    IReadOnlyList<string> Abilities,
    string? HiddenAbility,
    int BaseExp,
    int HeightDecimetres,
    int WeightHectograms,
    int FormCount,
    IReadOnlyList<int>? TypeIds = null,
    IReadOnlyList<int>? AbilityIds = null,
    int HiddenAbilityId = 0)
{
    /// <summary>
    /// Wild Pokémon gender: genderless (255), always female (254), always male (0), else female when a
    /// random byte is below the ratio.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsGenderless => GenderRatio == 255;
}
