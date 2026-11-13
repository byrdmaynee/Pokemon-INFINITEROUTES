using Godot;
using InfiniteRoutes.Client.Online;
using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// Battles online: the server rolls the encounter and plays the battle; this side builds the same battle from the game
/// the server sent with it and replays each action with the server's dice (see <see cref="IBattleLink"/>). What the
/// battle did to the game arrives as the server's next <see cref="StateSnapshot"/>.
/// </summary>
public partial class OverworldScreen
{
    BattleStart? _pendingBattle;
    OnlineBattle? _onlineBattle;

    /// <summary>Battles out of step with the server so far; for smoke tests.</summary>
    public int BattleMismatches => _onlineBattle?.Mismatches ?? _lastMismatches;
    int _lastMismatches;

    /// <summary>The battle on screen, if any; for smoke tests.</summary>
    public BattleScreen? BattleOpen => _battle;

    /// <summary>A wild Pokémon appeared (server): opens the battle once the step being walked is done.</summary>
    void QueueOnlineBattle(BattleStart start) => _pendingBattle = start;

    void OpenPendingBattle()
    {
        if (_pendingBattle is not { } start || _moving || _battle is not null || OnlineSession.Game is not { } game)
            return;
        _pendingBattle = null;
        var data = OfflineSession.Data;
        Pokemon? wild = null;
        GameState? state = null;
        var trainer = TrainerById(start.Trainer);
        try
        {
            if (data is not null && _onlineState is { } save)
            {
                state = save.Restore(data, new QuestLog([]));
                if (trainer is null && Core.Player.SaveGame.PokemonFromJson(start.Wild) is { } wildSave)
                    wild = Pokemon.FromSave(data, wildSave);
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        if (state is null || (wild is null && trainer is null) || state.Lead is null)
        {
            // Nothing to show it with (no battle data): let the server end it.
            game.Send(new BattleInput(start.Battle, 0, "end"));
            return;
        }
        _onlineBattle = new OnlineBattle(start.Battle, game);
        if (wild is not null)
            OpenBattle(new SingleBattle(data!, state.Party, state.Bag, wild, new Random(start.Seed)), _onlineBattle);
        else
        {
            // As the server does: the seed picks the trainer's team, then runs the battle.
            var rng = new Random(start.Seed);
            OpenBattle(new SingleBattle(data!, state.Party, state.Bag, SingleBattle.TrainerParty(data!, trainer!, rng), trainer!, rng), _onlineBattle);
        }
    }

    /// <summary>The battle screen closed: tell the server, which records the battle and sends the result.</summary>
    void EndOnlineBattle()
    {
        if (_onlineBattle is not { } link)
            return;
        _lastMismatches = link.Mismatches;
        link.Tell("end");
        _onlineBattle = null;
    }

    /// <summary>How the last battle ended; for smoke tests.</summary>
    public BattleOutcome LastBattleOutcome { get; private set; }

    void BattleResult(BattleOver over)
    {
        LastBattleOutcome = (BattleOutcome)over.Outcome;
        foreach (var line in over.Lines)
            AddSystem(line);
        if (over.Lines.Count > 0)
            ShowMessage(string.Join("\n", over.Lines));
        AfterTrainerBattle(LastBattleOutcome);
    }

    /// <summary>The server end of a battle on screen.</summary>
    sealed class OnlineBattle(int id, GameConnection game) : IBattleLink
    {
        int _seq;
        TaskCompletionSource<BattleTurn>? _waiting;
        string _digest = "";

        public int Id => id;
        public int Mismatches { get; private set; }

        public async Task<string?> Act(SingleBattle battle, string kind, IReadOnlyList<NetChoice?>? choices, int a)
        {
            _waiting = new TaskCompletionSource<BattleTurn>(TaskCreationOptions.RunContinuationsAsynchronously);
            game.Send(new BattleInput(id, ++_seq, kind, choices, a));
            var answer = await Task.WhenAny(_waiting.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            if (answer != _waiting.Task)
                return "The server didn't answer.";
            var turn = _waiting.Task.Result;
            if (!turn.Accepted)
                return turn.Reason.Length > 0 ? turn.Reason : "You can't do that now.";
            battle.Reseed(turn.Seed);
            _digest = turn.Digest;
            return null;
        }

        /// <summary>The server's answer to the action waiting for one.</summary>
        public void Answer(BattleTurn turn)
        {
            if (turn.Battle == id && turn.Seq == _seq)
                _waiting?.TrySetResult(turn);
        }

        public void Played(SingleBattle battle)
        {
            if (_digest.Length == 0 || battle.Digest() == _digest)
                return;
            // The server's battle is the one that counts (its result comes as the game state); this only shows it.
            Mismatches++;
            GD.PushWarning($"Battle {id} is out of step with the server:\n  server {_digest}\n  here   {battle.Digest()}");
        }

        public void Tell(string kind, int a = 0, int b = 0, int c = 0, string text = "") =>
            game.Send(new BattleInput(id, _seq, kind, null, a, b, c, text));
    }
}
