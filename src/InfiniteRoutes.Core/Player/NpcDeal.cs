namespace InfiniteRoutes.Core.Player;

/// <summary>
/// What an NPC can do for the player, from its zone script (the extractor's <c>NpcTalk</c>): battle as
/// <see cref="Trainer"/>, or hand over <see cref="GiftItem"/> × <see cref="GiftCount"/> once. Quest givers are found by
/// <see cref="Key"/>.
/// </summary>
public sealed record NpcDeal(int Zone, int Npc, int Trainer = -1, int GiftItem = 0, int GiftCount = 0)
{
    /// <summary>"zone:npc": how gifts and quest givers name an NPC.</summary>
    public string Key => KeyOf(Zone, Npc);

    public static string KeyOf(int zone, int npc) => $"{zone}:{npc}";
}

public enum NpcDealKind { None, Battle, Refused, Gift, Quest,
    /// <summary>An HM obstacle (a small tree, a boulder) was cleared out of the way: it's gone until the player leaves the map.</summary>
    Cleared }

/// <summary>What talking to an NPC came to: a battle with <see cref="Trainer"/>, a refusal, a gift or a new quest, with a line to show.</summary>
public sealed record NpcOutcome(NpcDealKind Kind, string Text = "", Data.TrainerInfo? Trainer = null, QuestInfo? Quest = null);
