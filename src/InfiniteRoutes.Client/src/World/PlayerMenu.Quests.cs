using Godot;
using InfiniteRoutes.Core.Player;

namespace InfiniteRoutes.Client.World;

/// <summary>The player menu's Quests tab: the quests the server's game holds, their objectives and rewards.</summary>
public sealed partial class PlayerMenu
{
    /// <summary>The server's game with the quest catalog (quests show only with it).</summary>
    public Func<GameState?> Game { get; init; } = () => null;

    VBoxContainer _quests = null!;

    /// <summary>Quest titles shown, active first; for smoke tests.</summary>
    public List<string> QuestsShown { get; } = [];

    Control QuestsTabPage()
    {
        _quests = Ui.Ui.VBox(6);
        var page = Scroll(_quests);
        page.Name = "Quests";
        return page;
    }

    void ShowQuests()
    {
        Ui.Ui.ClearChildren(_quests);
        QuestsShown.Clear();
        if (Game() is not { } game)
        {
            _quests.AddChild(Ui.Ui.Text("No quests here (this server keeps no game for you).", 16, Ui.Ui.Muted));
            return;
        }
        var log = game.Quests;
        _quests.AddChild(Wrapped("Story quests start by themselves; townspeople with a yellow \"!\" over their head have side quests. Rewards go straight into your Bag."));
        foreach (var category in Enum.GetValues<QuestCategory>())
        {
            var active = log.Active.Where(q => q.Info.Category == category).ToList();
            if (active.Count == 0)
                continue;
            _quests.AddChild(Ui.Ui.Text(category == QuestCategory.Daily ? "Daily" : category.ToString(), 18, Ui.Ui.Accent));
            foreach (var quest in active)
            {
                var info = quest.Info;
                QuestsShown.Add(info.Title);
                _quests.AddChild(Ui.Ui.Text((log.Tracked == quest ? "◆ " : "") + info.Title, 17));
                _quests.AddChild(Wrapped(info.Description));
                for (var i = 0; i < info.Objectives.Count; i++)
                {
                    var o = info.Objectives[i];
                    var n = quest.Progress(i, game.Progress);
                    _quests.AddChild(Ui.Ui.Text($"    {(n >= o.Count ? "✓" : "○")} {o.Text}{(o.Count > 1 ? $"  {n}/{o.Count}" : "")}", 15,
                        n >= o.Count ? Ui.Ui.Good : Ui.Ui.Muted));
                }
                var rewards = new List<string>();
                if (info.Reward.Money > 0)
                    rewards.Add($"₽{info.Reward.Money:N0}");
                rewards.AddRange(info.Reward.ItemList.Select(r => $"{ItemName(r.Item)} ×{r.Count}"));
                if (rewards.Count > 0)
                    _quests.AddChild(Ui.Ui.Text("    Reward: " + string.Join(", ", rewards), 14, Ui.Ui.Good));
            }
        }
        var completed = log.Completed.ToList();
        if (completed.Count > 0)
        {
            _quests.AddChild(Ui.Ui.Text($"Completed ({completed.Count})", 18, Ui.Ui.Good));
            foreach (var (quest, _) in completed.Take(12))
                _quests.AddChild(Ui.Ui.Text($"   ✓ {quest.Title}", 14, Ui.Ui.Muted));
        }
        if (log.Active.Count == 0 && completed.Count == 0)
            _quests.AddChild(Ui.Ui.Text("No quests yet.", 16, Ui.Ui.Muted));
    }
}
