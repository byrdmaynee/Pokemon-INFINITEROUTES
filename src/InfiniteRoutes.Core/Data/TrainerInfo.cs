namespace InfiniteRoutes.Core.Data;

/// <summary>
/// One of a trainer's Pokémon. <see cref="Difficulty"/> (0–255) sets every IV to Difficulty × 31 / 255.
/// <see cref="Moves"/> is null when the trainer uses the moves the species knows at that level.
/// </summary>
public sealed record TrainerPokemon(int Species, int Form, int Level, int Difficulty, int GenderAbility, int HeldItem,
    IReadOnlyList<int>? Moves);

/// <summary>
/// A trainer from the ROM's trainer table. <see cref="BattleType"/> is 0 single, 1 double, 2 triple, 3 rotation.
/// <see cref="PrizeBase"/> × 4 × the last Pokémon's level is the prize money.
/// </summary>
public sealed record TrainerInfo(int Id, string Name, int Class, string ClassName, int BattleType, IReadOnlyList<int> Items,
    int Ai, int PrizeBase, IReadOnlyList<TrainerPokemon> Pokemon)
{
    /// <summary>"Youngster Jimmy"-style display name.</summary>
    public string Title => string.IsNullOrEmpty(ClassName) ? Name : $"{ClassName} {Name}";
}
