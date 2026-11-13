using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;

namespace InfiniteRoutes.Server.World;

/// <summary>
/// NPCs online (BUILD_SPEC §9): the client shows what an NPC says (its lines come with the data pack); what it does — a
/// trainer battle, a gym leader waiting for earlier badges, a gift handed over once, a side quest — the server decides
/// from the zone events, and quests finish and pay out here.
/// </summary>
public sealed partial class WorldService
{
    /// <summary>How far from an NPC's home tile a player may talk to it: across a counter, plus how far it wanders.</summary>
    const int TalkReach = 2;

    /// <summary>The shared script every Pokémon Center nurse runs.</summary>
    const int NurseScript = 2100;

    void NpcActionLocked(WorldPlayer p, NpcAction action)
    {
        if (rules is null || p.State is not { } state || p.Battle is not null || p.Spectating is not null)
            return;
        void Refuse(string text) => p.Link.Send(new NpcResult(action.Zone, action.Npc, (int)NpcDealKind.Refused, text));
        if (world.Npc(action.Zone, action.Npc) is not { } npc || world.MapOf(action.Zone) != p.Map || npc.Flag != 0 && npc.TrainerId < 0)
        {
            Refuse("There's nobody there.");
            return;
        }
        int dx = Math.Abs(p.X - npc.X), dy = Math.Abs(p.Y - npc.Y), wander = npc.RangeX + npc.RangeY;
        var near = action.Spotted
            ? (dx == 0 || dy == 0) && dx + dy <= Math.Max(npc.Sight, 1) + wander + 1
            : dx + dy <= TalkReach + wander;
        if (!near)
        {
            Refuse("You're too far away.");
            return;
        }
        if (action.Spotted && (npc.Sight <= 0 || p.Elevated))
            return;

        if (Extractor.Extraction.UnchartedRoutes.IsObstacle(npc))
        {
            // A small tree or a boulder: with enough Gym Badges (or in Admin Mode) it's cleared out of this player's way.
            var (badges, cleared, blocked) = Extractor.Extraction.UnchartedRoutes.ObstacleRule(npc.Script);
            if (action.Spotted || p.Cleared.Contains((action.Zone, npc.Id)))
                return;
            if (!p.Elevated && state.Progress.Badges.Count < badges)
            {
                p.Link.Send(new NpcResult(action.Zone, action.Npc, (int)NpcDealKind.None, blocked));
                return;
            }
            p.Cleared.Add((action.Zone, npc.Id));
            p.Link.Send(new NpcResult(action.Zone, action.Npc, (int)NpcDealKind.Cleared, cleared));
            return;
        }

        if (npc.Script == NurseScript && !action.Spotted)
        {
            state.HealParty();
            state.Progress.CenterVisits++;
            p.StateDirty = true;
            QuestsLocked(p, notify: true);
            SaveNowLocked(p);
            SendState(p);
            p.Link.Send(new NpcResult(action.Zone, action.Npc, (int)NpcDealKind.None,
                "Welcome to the Pokémon Center!\nYour Pokémon have been restored to full health. We hope to see you again!"));
            return;
        }

        var deal = new NpcDeal(action.Zone, action.Npc, npc.TrainerId, npc.Talk?.GiftItem ?? 0, npc.Talk?.GiftCount ?? 0);
        var outcome = state.Deal(deal, rules.Trainers, rules.Data, DateTime.UtcNow);
        switch (outcome.Kind)
        {
            case NpcDealKind.Battle:
                StartBattleLocked(p, null, outcome.Trainer);
                return;
            case NpcDealKind.Gift or NpcDealKind.Quest:
                p.StateDirty = true;
                QuestsLocked(p, notify: true);
                SaveNowLocked(p);
                SendState(p);
                break;
        }
        p.Link.Send(new NpcResult(action.Zone, action.Npc, (int)outcome.Kind, outcome.Text));
    }

    /// <summary>
    /// Finishes the player's quests whose objectives are met and puts the rewards straight into their game. Returns a
    /// line per quest; with <paramref name="notify"/> they're also sent as notices (otherwise the caller shows them).
    /// </summary>
    List<string> QuestsLocked(WorldPlayer p, bool notify)
    {
        if (rules is null || p.State is not { } state)
            return [];
        var lines = new List<string>();
        foreach (var quest in state.UpdateQuests(DateTime.UtcNow))
        {
            var reward = state.GrantReward(quest, rules.Data);
            lines.Add(reward.Length > 0 ? $"Quest complete: {quest.Title}! You received {reward}." : $"Quest complete: {quest.Title}!");
        }
        if (lines.Count > 0)
        {
            p.StateDirty = true;
            if (notify)
                foreach (var line in lines)
                    p.Link.Send(new Notice(line));
        }
        return lines;
    }
}
