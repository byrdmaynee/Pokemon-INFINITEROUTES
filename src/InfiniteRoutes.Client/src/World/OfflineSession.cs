using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// The offline test world's game state: battle data from the extracted ROM and the player's party. Lives for the
/// whole client session (leaving to the menu and coming back keeps the party) until the assets change. Nothing is
/// saved to disk yet.
/// </summary>
public static class OfflineSession
{
    /// <summary>Snivy, Black/White's grass starter; the test world hands it out at level 5.</summary>
    const int StarterSpecies = 495;
    const int StarterLevel = 5;

    static GameAssets? _assets;

    public static BattleData Data { get; private set; } = null!;
    public static IReadOnlyList<EncounterTable> Encounters { get; private set; } = [];
    public static List<Pokemon> Party { get; } = new();
    public static Random Rng { get; } = new();

    /// <summary>Loads the battle data for <paramref name="assets"/> (once) and gives the player a starter if the party is empty.</summary>
    public static bool Ensure(GameAssets assets)
    {
        if (!ReferenceEquals(assets, _assets))
        {
            var moves = assets.LoadMoves();
            if (moves.Count == 0)
                return false;
            Data = new BattleData(assets.LoadSpecies(), moves, assets.LoadLearnsets());
            Encounters = assets.LoadEncounters();
            Party.Clear();
            _assets = assets;
        }
        if (Party.Count == 0)
            Party.Add(Pokemon.Create(Data, StarterSpecies, StarterLevel, Rng));
        return true;
    }

    /// <summary>The first Pokémon that can still fight, or null when all have fainted.</summary>
    public static Pokemon? Lead => Party.FirstOrDefault(p => !p.Fainted);

    public static void HealParty()
    {
        foreach (var mon in Party)
            mon.HealFully();
    }
}
