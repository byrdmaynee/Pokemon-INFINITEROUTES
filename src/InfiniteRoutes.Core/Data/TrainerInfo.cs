namespace InfiniteRoutes.Core.Data;

/// <summary>
/// One of a trainer's Pokémon. <see cref="Difficulty"/> (0–255) sets every IV to Difficulty × 31 / 255.
/// <see cref="Moves"/> is null when the trainer uses the moves the species knows at that level.
/// </summary>
public sealed record TrainerPokemon(int Species, int Form, int Level, int Difficulty, int GenderAbility, int HeldItem,
    IReadOnlyList<int>? Moves);

/// <summary>
/// A trainer from the ROM's trainer table. <see cref="BattleType"/> is 0 single, 1 double, 2 triple, 3 rotation.
/// <see cref="PrizeBase"/> × 4 × the last Pokémon's level is the prize money. <see cref="Intro"/>, <see cref="Defeat"/>
/// and <see cref="After"/> are its lines from the ROM (before, on losing and after a battle), with \n line breaks,
/// \r scrolls and \f page breaks; see <see cref="Pages"/>.
/// </summary>
public sealed record TrainerInfo(int Id, string Name, int Class, string ClassName, int BattleType, IReadOnlyList<int> Items,
    int Ai, int PrizeBase, IReadOnlyList<TrainerPokemon> Pokemon, string? Intro = null, string? Defeat = null, string? After = null)
{
    /// <summary>"Youngster Jimmy"-style display name.</summary>
    public string Title => string.IsNullOrEmpty(ClassName) ? Name : $"{ClassName} {Name}";

    /// <summary>A line split into its message-box pages.</summary>
    public static IReadOnlyList<string> Pages(string? line) => string.IsNullOrWhiteSpace(line) ? []
        : line.Split('\f').Select(p => p.Replace('\r', '\n').Trim()).Where(p => p.Length > 0).ToList();
}
