using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Core.Player;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Server.Game;

/// <summary>
/// The game data the server plays by (species, moves, learnsets, items, trainers from the data pack) and the
/// characters' game states: a new game for a new character, and loading and saving the rest as SaveGame JSON —
/// the same format as the offline save.
/// </summary>
public sealed class GameRules(BattleData data, IReadOnlyList<TrainerInfo> trainers, string game)
{
    readonly Random _rng = new();
    readonly object _gate = new();

    public BattleData Data => data;

    /// <summary>Loads the game data from the data pack; null when the pack has no battle data (extracted before 0.6.0).</summary>
    public static GameRules? From(GameAssets assets)
    {
        var moves = assets.LoadMoves();
        if (moves.Count == 0)
            return null;
        return new GameRules(new BattleData(assets.LoadSpecies(), moves, assets.LoadLearnsets(), assets.LoadText("items"), assets.LoadItemPrices()),
            assets.LoadTrainers(), assets.Stamp.GameId);
    }

    QuestLog Quests() => new(StarterQuests.Unova(trainers));

    public GameState NewGame()
    {
        lock (_gate)
            return StarterKit.NewGame(data, trainers, _rng, DateTime.UtcNow);
    }

    public string NewGameJson() => Json(NewGame());

    /// <summary>A character's state from its JSON; a new game when there's none (or it's unreadable).</summary>
    public GameState Load(string? json)
    {
        try
        {
            if (json is not null && SaveGame.FromJson(json) is { } save)
            {
                var state = save.Restore(data, Quests());
                if (state.Party.Count > 0)
                    return state;
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return NewGame();
    }

    public string Json(GameState state) => SaveGame.From(state, game, DateTime.UtcNow).ToJson();
}
