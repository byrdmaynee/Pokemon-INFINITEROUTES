using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Core.Player;

public enum QuestCategory { Story, Side, Daily }

public enum ObjectiveKind
{
    /// <summary>Catch <c>Count</c> Pokémon after the quest starts.</summary>
    Catch,
    /// <summary>Beat the trainer with id <c>Target</c>.</summary>
    DefeatTrainer,
    /// <summary>Beat <c>Count</c> different trainers after the quest starts.</summary>
    DefeatTrainers,
    /// <summary>Earn the badge with index <c>Target</c>.</summary>
    EarnBadge,
    /// <summary>Hold <c>Count</c> badges.</summary>
    Badges,
    /// <summary>Win <c>Count</c> wild battles after the quest starts.</summary>
    WinWild,
    /// <summary>Heal at a Pokémon Center <c>Count</c> times after the quest starts.</summary>
    HealAtCenter,
    /// <summary>Have <c>Count</c> Pokémon in the party.</summary>
    PartySize,
}

public sealed record QuestObjective(ObjectiveKind Kind, string Text, int Count = 1, int Target = 0)
{
    /// <summary>Counter-based objectives only count what happens after the quest starts.</summary>
    public bool CountsFromStart => Kind is ObjectiveKind.Catch or ObjectiveKind.DefeatTrainers or ObjectiveKind.WinWild or ObjectiveKind.HealAtCenter;

    /// <summary>The raw counter this objective reads, before subtracting the value at the quest's start.</summary>
    public int Counter(PlayerProgress p) => Kind switch
    {
        ObjectiveKind.Catch => p.Caught,
        ObjectiveKind.DefeatTrainer => p.DefeatedTrainers.Contains(Target) ? 1 : 0,
        ObjectiveKind.DefeatTrainers => p.DefeatedTrainers.Count,
        ObjectiveKind.EarnBadge => p.Badges.ContainsKey(Target) ? 1 : 0,
        ObjectiveKind.Badges => p.Badges.Count,
        ObjectiveKind.WinWild => p.WildWins,
        ObjectiveKind.HealAtCenter => p.CenterVisits,
        ObjectiveKind.PartySize => p.PartyCount,
        _ => 0,
    };
}

public sealed record QuestReward(int Money = 0, IReadOnlyList<(int Item, int Count)>? Items = null)
{
    public IReadOnlyList<(int Item, int Count)> ItemList => Items ?? [];
}

/// <param name="Next">The quest that starts when this one is done (story chains).</param>
public sealed record QuestInfo(string Id, string Title, string Description, QuestCategory Category,
    IReadOnlyList<QuestObjective> Objectives, QuestReward Reward, string? Next = null);

/// <summary>A quest in progress: the counters at its start, so "catch 3" means three new catches.</summary>
public sealed class ActiveQuest
{
    readonly int[] _baseline;

    public ActiveQuest(QuestInfo info, PlayerProgress progress, DateTime started)
        : this(info, started, info.Objectives.Select(o => o.CountsFromStart ? o.Counter(progress) : 0).ToArray()) { }

    ActiveQuest(QuestInfo info, DateTime started, int[] baseline)
    {
        Info = info;
        Started = started;
        _baseline = baseline;
    }

    public ActiveQuestSave ToSave() => new(Info.Id, Started, [.. _baseline]);

    /// <summary>Null when the quest no longer exists or its objectives changed shape.</summary>
    public static ActiveQuest? FromSave(QuestInfo info, ActiveQuestSave save) =>
        save.Baseline.Length == info.Objectives.Count ? new ActiveQuest(info, save.Started, [.. save.Baseline]) : null;

    public QuestInfo Info { get; }
    public DateTime Started { get; }

    public int Progress(int objective, PlayerProgress p)
    {
        var o = Info.Objectives[objective];
        return Math.Clamp(o.Counter(p) - _baseline[objective], 0, o.Count);
    }

    public bool Done(PlayerProgress p) => Info.Objectives.Select((o, i) => Progress(i, p) >= o.Count).All(x => x);
}

/// <summary>
/// The player's quests: active ones in the order they started, the one tracked on the HUD, and what's finished.
/// Daily quests restart each day; everything else completes once.
/// </summary>
public sealed class QuestLog
{
    readonly Dictionary<string, QuestInfo> _catalog;
    readonly List<ActiveQuest> _active = new();
    readonly Dictionary<string, DateTime> _completed = new();

    public QuestLog(IEnumerable<QuestInfo> catalog) => _catalog = catalog.ToDictionary(q => q.Id);

    public IReadOnlyList<ActiveQuest> Active => _active;
    public IEnumerable<(QuestInfo Quest, DateTime When)> Completed =>
        _completed.OrderByDescending(c => c.Value).Select(c => (_catalog[c.Key], c.Value));
    public string? TrackedId { get; set; }
    /// <summary>The quest picked for the HUD, else the current story quest, else the oldest active one.</summary>
    public ActiveQuest? Tracked => _active.FirstOrDefault(q => q.Info.Id == TrackedId)
        ?? _active.FirstOrDefault(q => q.Info.Category == QuestCategory.Story) ?? _active.FirstOrDefault();

    public bool IsDone(string id) => _completed.ContainsKey(id);

    public QuestLogSave ToSave() =>
        new(_active.Select(q => q.ToSave()).ToList(), _completed.ToDictionary(c => c.Key, c => c.Value), TrackedId);

    /// <summary>Restores saved quests; ones no longer in the catalog are dropped.</summary>
    public void Load(QuestLogSave save)
    {
        _active.Clear();
        _completed.Clear();
        foreach (var q in save.Active)
            if (_catalog.TryGetValue(q.Id, out var info) && ActiveQuest.FromSave(info, q) is { } active && _active.All(a => a.Info.Id != q.Id))
                _active.Add(active);
        foreach (var (id, when) in save.Completed)
            if (_catalog.ContainsKey(id))
                _completed[id] = when;
        TrackedId = save.Tracked;
    }

    /// <summary>Starts a quest from the catalog unless it's already active or (for non-dailies) done.</summary>
    public bool Start(string id, PlayerProgress progress, DateTime now)
    {
        if (!_catalog.TryGetValue(id, out var info) || _active.Any(q => q.Info.Id == id)
            || (info.Category != QuestCategory.Daily && _completed.ContainsKey(id)))
            return false;
        _active.Add(new ActiveQuest(info, progress, now));
        return true;
    }

    /// <summary>Stops an active side or daily quest. Story quests can't be dropped.</summary>
    public bool Abandon(string id)
    {
        var quest = _active.FirstOrDefault(q => q.Info.Id == id);
        if (quest is null || quest.Info.Category == QuestCategory.Story)
            return false;
        _active.Remove(quest);
        return true;
    }

    /// <summary>
    /// Finishes every active quest whose objectives are met, starts the quests they chain to, and restarts daily
    /// quests finished on an earlier day. Returns the quests finished now so their rewards can be handed out.
    /// </summary>
    public List<QuestInfo> Update(PlayerProgress progress, DateTime now)
    {
        var finished = new List<QuestInfo>();
        foreach (var (id, when) in _completed.ToList())
            if (_catalog[id].Category == QuestCategory.Daily && when.Date < now.Date && _active.All(q => q.Info.Id != id))
                Start(id, progress, now);
        for (var pass = 0; pass < _catalog.Count; pass++)   // a chained quest may already be met when it starts
        {
            var done = _active.Where(q => q.Done(progress)).ToList();
            if (done.Count == 0)
                break;
            foreach (var quest in done)
            {
                _active.Remove(quest);
                _completed[quest.Info.Id] = now;
                finished.Add(quest.Info);
                if (quest.Info.Next is { } next)
                    Start(next, progress, now);
            }
        }
        return finished;
    }
}

public sealed record ActiveQuestSave(string Id, DateTime Started, int[] Baseline);

public sealed record QuestLogSave(List<ActiveQuestSave> Active, Dictionary<string, DateTime> Completed, string? Tracked);

/// <summary>The quests the offline test world offers, built from the ROM's trainers so names match the game.</summary>
public static class StarterQuests
{
    public const string FirstCatch = "first-catch", FirstTrainer = "first-trainer", Champion = "eight-badges";
    public const string RestStop = "rest-stop", FullTeam = "full-team", FieldWork = "field-work", RouteRival = "route-rival";
    public const string DailyCatch = "daily-catch", DailyBattles = "daily-battles";

    public static string BadgeQuest(int index) => $"badge-{index}";

    public static List<QuestInfo> Unova(IReadOnlyList<TrainerInfo> trainers)
    {
        var jimmy = trainers.FirstOrDefault(t => t.Id == 1);
        var quests = new List<QuestInfo>
        {
            new(FirstCatch, "A New Partner", "Professor Juniper wants to see how you handle a Poké Ball. Catch a wild Pokémon in the tall grass.",
                QuestCategory.Story, [new(ObjectiveKind.Catch, "Catch a wild Pokémon")],
                new(Items: [(ItemEffect.PokeBall, 5)]), jimmy is null ? BadgeQuest(0) : FirstTrainer),
            new(RestStop, "Rest Stop", "Every Pokémon Center heals for free. Visit one and talk to the nurse.",
                QuestCategory.Side, [new(ObjectiveKind.HealAtCenter, "Heal at a Pokémon Center")], new(Items: [(ItemEffect.Potion, 3)])),
            new(FullTeam, "Full Team", "Six Pokémon travel with a trainer. Fill your party.",
                QuestCategory.Side, [new(ObjectiveKind.PartySize, "Have 6 Pokémon in your party", 6)], new(Items: [(ItemEffect.GreatBall, 5)])),
            new(FieldWork, "Field Work", "Win battles against wild Pokémon to toughen up your team.",
                QuestCategory.Side, [new(ObjectiveKind.WinWild, "Defeat 10 wild Pokémon", 10)], new(Money: 1000)),
            new(RouteRival, "Route Rival", "Trainers wait on every route. Take them on.",
                QuestCategory.Side, [new(ObjectiveKind.DefeatTrainers, "Defeat 5 trainers", 5)], new(Money: 1500, Items: [(ItemEffect.SuperPotion, 3)])),
            new(DailyCatch, "Daily: Survey", "Catch three Pokémon today.",
                QuestCategory.Daily, [new(ObjectiveKind.Catch, "Catch 3 Pokémon", 3)], new(Money: 500, Items: [(ItemEffect.PokeBall, 3)])),
            new(DailyBattles, "Daily: Sparring", "Win five wild battles today.",
                QuestCategory.Daily, [new(ObjectiveKind.WinWild, "Defeat 5 wild Pokémon", 5)], new(Money: 500)),
            new(Champion, "Road to the League", "With all eight badges, the Pokémon League's gates open.",
                QuestCategory.Story, [new(ObjectiveKind.Badges, "Earn all 8 badges", 8)], new(Money: 10000, Items: [(ItemEffect.MasterBall, 1)])),
        };
        if (jimmy is not null)
            quests.Add(new(FirstTrainer, "Trainer's Debut", $"{jimmy.Title} is waiting on Route 2. Show him what you and your partner can do.",
                QuestCategory.Story, [new(ObjectiveKind.DefeatTrainer, $"Defeat {jimmy.Title}", Target: jimmy.Id)],
                new(Money: 500), BadgeQuest(0)));

        foreach (var badge in Badges.Unova)
        {
            var leaders = string.Join(" / ", badge.Leaders.Where(n => trainers.Any(t => t.Name == n && Badges.EarnedBy(t) == badge)).DefaultIfEmpty(badge.Leaders[0]));
            var next = badge.Index + 1 < Badges.Unova.Count ? BadgeQuest(badge.Index + 1) : Champion;
            quests.Add(new(BadgeQuest(badge.Index), $"The {badge.City[..badge.City.LastIndexOf(' ')]} Gym",
                $"Travel to {badge.City} and defeat Leader {leaders} ({badge.Type}) for the {badge.Name}.",
                QuestCategory.Story, [new(ObjectiveKind.EarnBadge, $"Earn the {badge.Name}", Target: badge.Index)],
                new(Money: 1000 * (badge.Index + 1), Items: [(ItemEffect.SuperPotion,2 + badge.Index / 2)]), next));
        }
        return quests;
    }

    /// <summary>The quests a new character starts with.</summary>
    public static readonly string[] Initial = [FirstCatch, RestStop, FullTeam, FieldWork, RouteRival, DailyCatch, DailyBattles];
}
