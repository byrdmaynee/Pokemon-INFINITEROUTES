using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Core.Player;

/// <summary>A gym badge. <paramref name="Leaders"/> are the trainer names (class "Leader") whose defeat earns it.</summary>
public sealed record BadgeInfo(int Index, string Name, string City, string Type, IReadOnlyList<string> Leaders, uint Color);

public static class Badges
{
    /// <summary>
    /// Unova's eight badges in gym order. Striaton has three leaders (one per starter); Opelucid has Drayden in Black
    /// and Iris in White, so a badge lists every leader who can award it.
    /// </summary>
    public static readonly IReadOnlyList<BadgeInfo> Unova =
    [
        new(0, "Trio Badge", "Striaton City", "Grass / Fire / Water", ["Cilan", "Chili", "Cress"], 0xE0503Cu),
        new(1, "Basic Badge", "Nacrene City", "Normal", ["Lenora"], 0xC8B28Cu),
        new(2, "Insect Badge", "Castelia City", "Bug", ["Burgh"], 0x8CC63Cu),
        new(3, "Bolt Badge", "Nimbasa City", "Electric", ["Elesa"], 0xF5D03Cu),
        new(4, "Quake Badge", "Driftveil City", "Ground", ["Clay"], 0xB4783Cu),
        new(5, "Jet Badge", "Mistralton City", "Flying", ["Skyla"], 0x6EB4F0u),
        new(6, "Freeze Badge", "Icirrus City", "Ice", ["Brycen"], 0x9EE6F0u),
        new(7, "Legend Badge", "Opelucid City", "Dragon", ["Drayden", "Iris"], 0x7864DCu),
    ];

    /// <summary>The badge a gym leader awards, or null for anyone else.</summary>
    public static BadgeInfo? EarnedBy(TrainerInfo trainer) =>
        trainer.ClassName == "Leader" ? Unova.FirstOrDefault(b => b.Leaders.Contains(trainer.Name)) : null;

    /// <summary>
    /// Why a gym leader won't battle yet: gyms are taken in order, so a leader waits for the badges before theirs. Null
    /// when they will (or the trainer isn't a leader, or the badge is already earned).
    /// </summary>
    public static string? Gate(PlayerProgress progress, TrainerInfo trainer)
    {
        if (EarnedBy(trainer) is not { } badge || progress.Badges.ContainsKey(badge.Index))
            return null;
        return Unova.FirstOrDefault(b => b.Index < badge.Index && !progress.Badges.ContainsKey(b.Index)) is { } missing
            ? $"{trainer.Title}: You'll need the {missing.Name} from the {missing.City} Gym before I battle you. Come back once you've earned it!"
            : null;
    }

    /// <summary>The first trainer in <paramref name="trainers"/> who awards <paramref name="badge"/>.</summary>
    public static TrainerInfo? LeaderFor(BadgeInfo badge, IEnumerable<TrainerInfo> trainers) =>
        trainers.FirstOrDefault(t => EarnedBy(t) == badge);
}
