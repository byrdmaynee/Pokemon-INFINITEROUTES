using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// The offline test world's game state: battle data from the extracted ROM, the player's party, PC box and Bag.
/// Lives for the whole client session (leaving to the menu and coming back keeps them) until the assets change.
/// Nothing is saved to disk yet.
/// </summary>
public static class OfflineSession
{
    /// <summary>Snivy, Black/White's grass starter; the test world hands it out at level 5.</summary>
    const int StarterSpecies = 495;
    const int StarterLevel = 5;
    public const int PartySize = 6;

    static GameAssets? _assets;

    public static BattleData Data { get; private set; } = null!;
    public static IReadOnlyList<EncounterTable> Encounters { get; private set; } = [];
    public static List<Pokemon> Party { get; } = new();
    /// <summary>Caught Pokémon that didn't fit in the party.</summary>
    public static List<Pokemon> Box { get; } = new();
    public static Bag Bag { get; private set; } = new();
    public static Random Rng { get; } = new();

    /// <summary>Loads the battle data for <paramref name="assets"/> (once) and hands out a starter and items if the party is empty.</summary>
    public static bool Ensure(GameAssets assets)
    {
        if (!ReferenceEquals(assets, _assets))
        {
            var moves = assets.LoadMoves();
            if (moves.Count == 0)
                return false;
            Data = new BattleData(assets.LoadSpecies(), moves, assets.LoadLearnsets(), assets.LoadText("items"));
            Encounters = assets.LoadEncounters();
            Party.Clear();
            Box.Clear();
            _assets = assets;
        }
        if (Party.Count == 0)
        {
            Party.Add(Pokemon.Create(Data, StarterSpecies, StarterLevel, Rng));
            Bag = new Bag();
            Bag.Add(ItemEffect.PokeBall, 10);
            Bag.Add(ItemEffect.GreatBall, 3);
            Bag.Add(ItemEffect.Potion, 5);
            Bag.Add(ItemEffect.Antidote, 2);
            Bag.Add(ItemEffect.ParlyzHeal, 2);
            Bag.Add(ItemEffect.ReviveItem, 1);
        }
        return true;
    }

    /// <summary>The first Pokémon that can still fight, or null when all have fainted.</summary>
    public static Pokemon? Lead => Party.FirstOrDefault(p => !p.Fainted);

    /// <summary>Adds a caught Pokémon to the party, or the box when the party is full. Returns where it went.</summary>
    public static string Keep(Pokemon mon)
    {
        if (Party.Count < PartySize)
        {
            Party.Add(mon);
            return $"{mon.Name} was added to your party.";
        }
        Box.Add(mon);
        return $"{mon.Name} was sent to the PC.";
    }

    public static void HealParty()
    {
        foreach (var mon in Party)
            mon.HealFully();
    }
}
