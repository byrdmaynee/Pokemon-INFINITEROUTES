using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Core.Player;

/// <summary>What a new game starts with (offline and on a server): Snivy at level 5 and the starting items.</summary>
public static class StarterKit
{
    public const int Species = 495, Level = 5;
    /// <summary>The Super Rod (handed out from the start while the test world has no way to earn it).</summary>
    public const int SuperRod = 447;

    /// <summary>Puts the starter in the party and the starting items in the Bag.</summary>
    public static void Fill(GameState state, BattleData data, Random rng)
    {
        state.Party.Add(Pokemon.Create(data, Species, Level, rng));
        state.Bag.Add(ItemEffect.PokeBall, 10);
        state.Bag.Add(ItemEffect.GreatBall, 3);
        state.Bag.Add(ItemEffect.Potion, 5);
        state.Bag.Add(ItemEffect.Antidote, 2);
        state.Bag.Add(ItemEffect.ParlyzHeal, 2);
        state.Bag.Add(ItemEffect.ReviveItem, 1);
        state.Bag.Add(HeldItems.OranBerry, 2);
        state.Bag.Add(SuperRod);
        state.Progress.PartyCount = state.Party.Count;
    }

    /// <summary>A whole new game: the kit plus the starting quests over the region's quest catalog.</summary>
    public static GameState NewGame(BattleData data, IReadOnlyList<TrainerInfo> trainers, Random rng, DateTime now)
    {
        var state = new GameState(new QuestLog(StarterQuests.Unova(trainers)));
        Fill(state, data, rng);
        foreach (var id in StarterQuests.Initial)
            state.Quests.Start(id, state.Progress, now);
        return state;
    }
}
