using Godot;
using InfiniteRoutes.Client.Online;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// The hand-made Infinite Routes the server has sent (<see cref="RouteSet"/>, <see cref="RouteChanged"/>): the game
/// draws these in place of generated routes while playing online.
/// </summary>
static class OnlineRoutes
{
    static readonly Dictionary<int, RouteFile> Saved = new();

    public static IReadOnlyDictionary<int, RouteFile> Files => Saved;

    public static void Set(GameAssets assets, IEnumerable<string> files)
    {
        Saved.Clear();
        foreach (var json in files)
            if (RouteFiles.FromJson(json) is { } file)
                Saved[file.Node] = file;
        Attach(assets);
    }

    public static void Change(GameAssets assets, int node, string json)
    {
        if (json.Length > 0 && RouteFiles.FromJson(json) is { } file)
            Saved[node] = file with { Node = node };
        else
            Saved.Remove(node);
        Attach(assets);
    }

    /// <summary>Offline there are no hand-made routes (they're the server's).</summary>
    public static void Clear(GameAssets assets)
    {
        Saved.Clear();
        assets.RouteSource = null;
        assets.ForgetRoutes();
    }

    static IReadOnlyDictionary<int, string>? _classNames;

    /// <summary>A trainer made for a hand-made route the server has sent, by its id; null if there's none.</summary>
    public static Core.Data.TrainerInfo? Trainer(int id)
    {
        if (!UnchartedRoutes.IsRouteTrainer(id))
            return null;
        var (node, person) = UnchartedRoutes.RouteTrainerOf(id);
        _classNames ??= OfflineSession.Trainers.GroupBy(t => t.Class).ToDictionary(g => g.Key, g => g.First().ClassName);
        return Saved.TryGetValue(node, out var file) ? UnchartedRoutes.TrainerOf(file, person, c => _classNames.GetValueOrDefault(c, "")) : null;
    }

    static void Attach(GameAssets assets)
    {
        assets.RouteSource = node => Saved.GetValueOrDefault(node);
        assets.ForgetRoutes();
    }
}

/// <summary>
/// Hand-made routes online, and the route editor: F10 in Admin Mode, for roles with the route-editing permission
/// (Owners, and whichever roles an Owner gives it to). The server checks the permission again on every save.
/// </summary>
public partial class OverworldScreen
{
    public const string RouteEditPermission = "world.routes.edit";

    RouteEditorScreen? _routeEditor;

    public bool RouteEditorOpen => _routeEditor is not null && IsInstanceValid(_routeEditor);

    /// <summary>The open route editor; for smoke tests.</summary>
    public RouteEditorScreen? RouteEditor => RouteEditorOpen ? _routeEditor : null;

    /// <summary>Whether a battle is on screen; for smoke tests.</summary>
    public bool InBattle => _battle is not null;

    bool MayEditRoutes => _admin?.Permissions.Contains(RouteEditPermission) == true;

    /// <summary>A trainer by id: the ROM's, or one made for a hand-made route.</summary>
    static Core.Data.TrainerInfo? TrainerById(int id) =>
        id >= 0 && id < OfflineSession.Trainers.Count ? OfflineSession.Trainers[id] : OnlineRoutes.Trainer(id);

    /// <summary>F10: opens or closes the route editor.</summary>
    public void ToggleRouteEditor()
    {
        if (RouteEditorOpen)
        {
            CloseRouteEditor();
            return;
        }
        if (!MayEditRoutes)
        {
            AddSystem("Your role can't use the route editor. An Owner can give a role that permission (F9 → Roles).");
            return;
        }
        if (_admin is not { Elevated: true })
        {
            AddSystem("The route editor needs Admin Mode (/admin).");
            return;
        }
        _routeEditor = new RouteEditorScreen(() => OnlineRoutes.Files, m => OnlineSession.Game?.Send(m), CloseRouteEditor);
        AddChild(_routeEditor);
        _routeEditor.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    void CloseRouteEditor()
    {
        if (RouteEditorOpen)
            _routeEditor!.QueueFree();
        _routeEditor = null;
    }

    void OnRoutes(RouteSet set)
    {
        OnlineRoutes.Set(_assets, set.Files);
        ReloadRoute(null);
        RouteEditor?.RoutesChanged();
    }

    void OnRouteChanged(RouteChanged change)
    {
        OnlineRoutes.Change(_assets, change.Node, change.File);
        ReloadRoute(change.Node);
        RouteEditor?.RoutesChanged();
    }

    /// <summary>Draws the route the player is standing in again, if it's <paramref name="node"/> (or any route, for null).</summary>
    void ReloadRoute(int? node)
    {
        if (!UnchartedRoutes.IsRoute(_zone) || node is { } n && UnchartedRoutes.ZoneOf(n) != _zone
            || !_zones.TryGetValue(_zone, out var zone) || _assets.LoadMap(zone.Map) is not { } map)
            return;
        _tiles.Clear();
        EnterMap(map);
        StreamChunks();
        UpdateHud();
    }

    TaskCompletionSource<bool>? _asking;
    Control? _askPanel;

    /// <summary>The yes/no question on screen, if any; for smoke tests.</summary>
    public string? Question => _askPanel is not null && IsInstanceValid(_askPanel) ? _askPanel.GetMeta("question").AsString() : null;

    /// <summary>Answers the yes/no question on screen; for smoke tests.</summary>
    public void AnswerForTest(bool yes) => Answer(yes);

    /// <summary>
    /// Asks a yes/no question over the world (Yes is focused: Enter or Z says yes, Esc or X no). The player can't move
    /// until it's answered.
    /// </summary>
    Task<bool> AskYesNo(string question)
    {
        Answer(false);
        _asking = new TaskCompletionSource<bool>();
        _moving = true;
        var yes = Ui.Ui.Button("Yes", () => Answer(true), 18);
        var no = Ui.Ui.Button("No", () => Answer(false), 18);
        foreach (var b in new[] { yes, no })
            b.CustomMinimumSize = new Vector2(110, 40);
        var panel = new PanelContainer();
        panel.SetMeta("question", question);
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.1f, 0.12f, 0.16f, 0.96f), BorderColor = Ui.Ui.Accent,
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 14, ContentMarginBottom = 14,
        });
        panel.AddChild(Ui.Ui.VBox(12, Ui.Ui.Text(question, 20), Ui.Ui.HBox(12, Ui.Ui.Spacer(), yes, no)));
        AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom);
        panel.GrowHorizontal = GrowDirection.Both;
        panel.GrowVertical = GrowDirection.Begin;
        panel.OffsetBottom = -40;
        _askPanel = panel;
        yes.CallDeferred(Control.MethodName.GrabFocus);
        return _asking.Task;
    }

    void Answer(bool yes)
    {
        if (_asking is not { } asking)
            return;
        _asking = null;
        if (_askPanel is not null && IsInstanceValid(_askPanel))
            _askPanel.QueueFree();
        _askPanel = null;
        _moving = false;
        asking.TrySetResult(yes);
    }

    /// <summary>Esc or X answers no; Z answers yes (Enter presses the focused button).</summary>
    bool AskInput(InputEvent e)
    {
        if (_asking is null || e is not InputEventKey { Pressed: true, Echo: false } key)
            return false;
        if (key.Keycode is Key.Escape or Key.X)
            Answer(false);
        else if (key.Keycode == Key.Z)
            Answer(true);
        else
            return false;
        return true;
    }

    /// <summary>The route editor's answers from the server (saved, or why not).</summary>
    bool ShowRouteReply(AdminReply reply)
    {
        if (reply.Topic != "route")
            return false;
        var text = reply.Lines.Count > 0 ? reply.Lines[0] : "";
        Main.Instance.Toast(text);
        if (!reply.Ok)
            AddSystem(text);
        return true;
    }
}
