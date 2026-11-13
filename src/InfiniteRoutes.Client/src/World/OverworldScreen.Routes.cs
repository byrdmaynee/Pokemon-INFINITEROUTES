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

    /// <summary>
    /// Cut or Strength from the Bag: on the small tree or boulder the player faces (the server checks the badges, as
    /// when talking to it). Returns why it can't be used, or null when it was (the menu closes).
    /// </summary>
    string? UseFieldMove(int item)
    {
        var cut = item == Core.Battle.ItemEffect.HmCut;
        var script = cut ? UnchartedRoutes.CutScript : UnchartedRoutes.StrengthScript;
        if (NpcAt(_tile + _dir) is not { } obstacle || obstacle.Info.Script != script)
            return cut ? "There's no small tree in front of you to cut." : "There's no boulder in front of you to move.";
        ToggleOnlineMenu();
        OnlineSession.Game?.Send(new NpcAction(obstacle.Zone, obstacle.Info.Id));
        return null;
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
