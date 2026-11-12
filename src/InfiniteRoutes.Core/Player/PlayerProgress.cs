namespace InfiniteRoutes.Core.Player;

/// <summary>
/// What a character has done in a region: badges, trainers beaten and the running counters quests check.
/// Offline it lives in memory; online it becomes the character's region progress row (BUILD_SPEC §5).
/// </summary>
public sealed class PlayerProgress
{
    /// <summary>Badge index → when it was earned.</summary>
    public SortedDictionary<int, DateTime> Badges { get; } = new();
    public HashSet<int> DefeatedTrainers { get; } = new();
    /// <summary>NPCs whose gift has been collected, by <see cref="NpcDeal.Key"/> ("zone:npc").</summary>
    public HashSet<string> Gifts { get; } = new();
    /// <summary>Species id → how many of it have been caught.</summary>
    public Dictionary<int, int> CaughtSpecies { get; } = new();
    /// <summary>The highest level in the party right now; set with <see cref="PartyCount"/>.</summary>
    public int TopLevel { get; set; }
    public int Money { get; set; } = 3000;
    public int Caught { get; set; }
    public int WildWins { get; set; }
    public int CenterVisits { get; set; }
    /// <summary>Pokémon in the party right now; set by whoever owns the party before quests are checked.</summary>
    public int PartyCount { get; set; }
    public DateTime Started { get; init; } = DateTime.Now;
    /// <summary>Time played before this session (the session adds its own on top when it saves).</summary>
    public TimeSpan PlayTime { get; set; }

    /// <summary>Counts a beaten trainer, and earns the badge if they were a gym leader. Returns the new badge, if any.</summary>
    public BadgeInfo? Defeated(Data.TrainerInfo trainer, DateTime now)
    {
        DefeatedTrainers.Add(trainer.Id);
        if (Player.Badges.EarnedBy(trainer) is not { } badge || Badges.ContainsKey(badge.Index))
            return null;
        Badges[badge.Index] = now;
        return badge;
    }
}
