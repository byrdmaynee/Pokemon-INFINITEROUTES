using System.Text.RegularExpressions;
using Godot;
using InfiniteRoutes.Client.Online;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// Talking to NPCs: they say their own lines from the ROM's zone scripts (<see cref="NpcInfo.Talk"/>); trainers and gym
/// leaders battle, leaders only once the badges before theirs are earned; some NPCs hand over an item once; townspeople
/// with a "!" hand out side quests. Offline the game here decides (<see cref="GameState.Deal"/>); online the server
/// does, answering <see cref="NpcAction"/> with a battle or an <see cref="NpcResult"/>.
/// </summary>
public partial class OverworldScreen
{
    GameState? _onlineGame;
    SaveGame? _onlineGameFrom;

    /// <summary>The trainer NPC whose battle is on (or about to be), for their after-battle lines.</summary>
    NpcActor? _battleNpc;

    /// <summary>The game NPCs act on: the copy of the server's game online (for showing only), the offline game otherwise.</summary>
    GameState? NpcGame => _online ? OnlineGame : _battles ? OfflineSession.State : null;

    /// <summary>The server's game for this character as last sent, with the quest catalog so quests show.</summary>
    GameState? OnlineGame
    {
        get
        {
            if (_onlineState is not { } save || OfflineSession.Data is null)
                return null;
            if (!ReferenceEquals(save, _onlineGameFrom))
            {
                _onlineGameFrom = save;
                try
                {
                    _onlineGame = save.Restore(OfflineSession.Data, new QuestLog(StarterQuests.Unova(OfflineSession.Trainers)));
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or NullReferenceException)
                {
                    _onlineGame = null;
                }
            }
            return _onlineGame;
        }
    }

    /// <summary>The trainer an NPC battles as, if it has one with Pokémon and hasn't been beaten yet.</summary>
    TrainerInfo? TrainerFor(NpcActor npc) =>
        NpcGame is { } game && npc.Info.TrainerId is var id and >= 0
        && !game.Progress.DefeatedTrainers.Contains(id) && TrainerById(id) is { Pokemon.Count: > 0 } trainer ? trainer : null;

    static bool IsLeader(NpcInfo npc) =>
        npc.TrainerId is var id and >= 0 && id < OfflineSession.Trainers.Count && Badges.EarnedBy(OfflineSession.Trainers[id]) is not null;

    /// <summary>
    /// Which of a zone's NPCs stand on the map. Story flags aren't tracked yet, so only NPCs no flag can hide — plus gym
    /// leaders (a flag hides some until their story scene), unless someone already stands on their tile (Striaton's
    /// three leaders share one spot; the one without a flag stays).
    /// </summary>
    static IEnumerable<NpcInfo> ShownNpcs(ZoneInfo zone)
    {
        var shown = zone.Npcs.Where(n => n.Flag == 0).ToList();
        foreach (var leader in zone.Npcs.Where(n => n.Flag != 0 && IsLeader(n)))
            if (!shown.Any(s => s.X == leader.X && s.Y == leader.Y))
                shown.Add(leader);
        return shown;
    }

    static readonly Regex PlayerName = new(@"\[VAR 0100\(\d+\)\]"), OtherVar = new(@"\[VAR [^\]]*\]");

    /// <summary>A script line as message-box pages, with the player's name filled in.</summary>
    IReadOnlyList<string> Pages(IEnumerable<string>? lines)
    {
        var name = _online ? OnlineSession.Character?.Name ?? "" : OfflineSession.PlayerName;
        return (lines ?? []).SelectMany(l => TrainerInfo.Pages(OtherVar.Replace(PlayerName.Replace(l, name), ""))).ToList();
    }

    /// <summary>
    /// The player talked to <paramref name="npc"/>: an unbeaten trainer says their opening lines and battles; a beaten one
    /// says what they say from then on; anyone else says their lines, then hands over their gift or quest if they have one.
    /// </summary>
    async void TalkTo(NpcActor npc)
    {
        _moving = true;
        var talk = npc.Info.Talk;
        var game = NpcGame;
        try
        {
            if (TrainerFor(npc) is { } trainer)
            {
                var intro = Pages(talk?.Lines);
                await Say(intro.Count > 0 ? intro : TrainerInfo.Pages(trainer.Intro));
                if (IsInstanceValid(this))
                    await Act(npc, spotted: false);
                return;
            }
            if (TrainerById(npc.Info.TrainerId) is { } beaten && game is not null)
            {
                var after = Pages(talk?.Later);
                if (after.Count == 0)
                    after = TrainerInfo.Pages(beaten.After);
                await Say(after.Count > 0 ? after : [$"{beaten.Title}: …"]);
                return;
            }
            var key = NpcDeal.KeyOf(npc.Zone, npc.Info.Id);
            var giftDone = talk is { GiftItem: > 0 } && game?.Progress.Gifts.Contains(key) == true;
            var lines = Pages(giftDone && talk!.Later is not null ? talk.Later : talk?.Lines);
            await Say(lines.Count > 0 ? lines : ["…"]);
            if (IsInstanceValid(this) && game is not null
                && ((talk is { GiftItem: > 0 } && !giftDone) || game.Quests.Offer(key, game.Progress, DateTime.Now) is not null))
                await Act(npc, spotted: false);
        }
        finally
        {
            if (IsInstanceValid(this) && _battle is null)
                _moving = false;
        }
    }

    /// <summary>
    /// What the NPC does after talking: battle, refuse, gift, quest. Online the server is asked; offline the game here
    /// decides and the result is shown at once.
    /// </summary>
    async Task Act(NpcActor npc, bool spotted)
    {
        if (_online)
        {
            if (npc.Info.TrainerId >= 0)
                _battleNpc = npc;
            OnlineSession.Game?.Send(new NpcAction(npc.Zone, npc.Info.Id, spotted));
            return;
        }
        if (!_battles)
            return;
        var talk = npc.Info.Talk;
        var outcome = OfflineSession.State.Deal(new NpcDeal(npc.Zone, npc.Info.Id, npc.Info.TrainerId, talk?.GiftItem ?? 0, talk?.GiftCount ?? 0),
            TrainerById, OfflineSession.Data, DateTime.Now);
        switch (outcome.Kind)
        {
            case NpcDealKind.Battle:
                _battleNpc = npc;
                StartTrainerBattle(outcome.Trainer!);
                return;
            case NpcDealKind.None:
                return;
        }
        await Say(TrainerInfo.Pages(outcome.Text));
        if (outcome.Kind is NpcDealKind.Gift or NpcDealKind.Quest)
        {
            CheckQuests();
            RefreshQuestMarks();
        }
    }

    /// <summary>The server's answer to <see cref="NpcAction"/> when it didn't start a battle.</summary>
    /// <summary>Takes an NPC off the map (it comes back when its cell is loaded again).</summary>
    void RemoveNpc(NpcActor npc)
    {
        foreach (var list in _npcs.Values)
            list.Remove(npc);
        npc.QueueFree();
    }

    void OnNpcResult(NpcResult result)
    {
        if ((NpcDealKind)result.Kind != NpcDealKind.Battle)
            _battleNpc = null;
        if (result.Text.Length > 0)
            _ = Say(TrainerInfo.Pages(result.Text));
        // An HM obstacle the server says is cleared: out of the way until the map is left.
        if ((NpcDealKind)result.Kind == NpcDealKind.Cleared && Npcs.FirstOrDefault(n => n.Zone == result.Zone && n.Info.Id == result.Npc) is { } gone)
            RemoveNpc(gone);
        RefreshQuestMarks();
    }

    /// <summary>After a won trainer battle: what the trainer says on losing in their script (gym leaders hand over the badge).</summary>
    void AfterTrainerBattle(Core.Battle.BattleOutcome outcome)
    {
        var npc = _battleNpc;
        _battleNpc = null;
        if (outcome == Core.Battle.BattleOutcome.Won && npc is not null && IsInstanceValid(npc) && Pages(npc.Info.Talk?.Win) is { Count: > 0 } win)
            _ = Say(win);
        RefreshQuestMarks();
    }

    /// <summary>Puts a "!" over every NPC in view who has a quest for the player now.</summary>
    void RefreshQuestMarks()
    {
        var game = NpcGame;
        var givers = game?.Quests.Givers.ToHashSet() ?? [];
        foreach (var npc in Npcs)
        {
            var key = NpcDeal.KeyOf(npc.Zone, npc.Info.Id);
            npc.SetQuestMark(game is not null && givers.Contains(key) && game.Quests.Offer(key, game.Progress, DateTime.Now) is not null);
        }
    }

    // Smoke-test hooks.
    public string DialogText => _dialog.Visible ? _dialogText.Text : "";
    public void CloseDialog() => _dialog.Visible = false;
    public string QuestHudText => _questHud.Visible ? _questHud.Text : "";

    /// <summary>Turns the player to face <paramref name="dir"/> without stepping.</summary>
    public void FaceForTest(Vector2I dir)
    {
        _dir = dir;
        ShowFrame(0);
        if (_online)
            OnlineSession.Game?.Send(new Turn(DirToFacing(dir) switch { Facing.Up => 1, Facing.Left => 2, Facing.Right => 3, _ => 0 }));
    }

    /// <summary>NPCs showing a quest "!" now; for smoke tests.</summary>
    public IEnumerable<NpcActor> QuestGivers => Npcs.Where(n => n.QuestMarked);
}
