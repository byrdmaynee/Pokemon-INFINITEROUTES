using Godot;
using InfiniteRoutes.Core.Net;
using OnlineSession = InfiniteRoutes.Client.Online.OnlineSession;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// The in-game admin GUI (BUILD_SPEC §11.2–11.3), shown only in Admin Mode and only with the buttons the role allows
/// (the server checks every action again):
/// <list type="bullet">
/// <item>F9: the admin panel — everyone online (refreshed every few seconds) with Inspect, Go to, Summon, Spectate,
/// Mute and Kick; a reason box for mutes and kicks; Invisible and Noclip; an announcement; leaving Admin Mode.</item>
/// <item>Inspect opens a window with the player's account, character and game.</item>
/// <item>Right-clicking another player in the world opens the same actions for them.</item>
/// <item>Spectating: the camera follows the watched player (and the map loads around them); /spectate off returns.</item>
/// </list>
/// </summary>
public partial class OverworldScreen
{
    PanelContainer? _adminPanel;
    VBoxContainer? _adminRows;
    LineEdit? _adminReason;
    PanelContainer? _inspector;
    PopupMenu? _playerMenu;
    double _adminRefresh;
    int _spectating;
    string _spectatingName = "";

    /// <summary>The player being spectated, or 0; for smoke tests.</summary>
    public int SpectatingId => _spectating;

    public bool AdminPanelOpen => _adminPanel is not null;

    /// <summary>The rows the admin panel shows now; for smoke tests.</summary>
    public List<AdminPlayerRow> AdminPanelRows { get; } = [];

    bool Can(string permission) => _admin is { Elevated: true } a && a.Permissions.Contains(permission);

    void SendAdmin(string command) => OnlineSession.Game?.Send(new AdminCommand(command));

    // ---- Spectating -------------------------------------------------------------------------------------------------

    void SetSpectating(SpectateStatus status)
    {
        _spectating = status.TargetId;
        _spectatingName = status.Name;
        _player.Visible = _spectating == 0;
        UpdateHud();
        RefreshAdminPanel();
    }

    /// <summary>Redraws an open admin panel (its toggles and buttons follow Admin Mode, invisibility, spectating).</summary>
    void RefreshAdminPanel()
    {
        if (_adminPanel is null)
            return;
        ToggleAdminPanel();
        if (_admin is { Elevated: true })
            ToggleAdminPanel();
    }

    /// <summary>While spectating: the map around the watched player loads, and the (hidden) character stays with them.</summary>
    void FollowSpectated()
    {
        if (_spectating == 0 || !_remotes.TryGetValue(_spectating, out var target) || _moving)
            return;
        if (target.Tile != _tile)
        {
            _tile = target.Tile;
            StreamChunks();
            UpdateHud();
        }
        _player.Position = target.Position;
    }

    string SpectateHud => _spectating != 0 ? $"\n👁 Spectating {_spectatingName} — /spectate off" : "";

    // ---- The panel ---------------------------------------------------------------------------------------------------

    /// <summary>F9: opens or closes the admin panel (Admin Mode only).</summary>
    public void ToggleAdminPanel()
    {
        if (_adminPanel is not null)
        {
            _adminPanel.QueueFree();
            _adminPanel = null;
            return;
        }
        if (_admin is not { Elevated: true } admin)
        {
            AddSystem("The admin panel needs Admin Mode (/admin).");
            return;
        }
        _adminRows = Ui.Ui.VBox(4);
        _adminReason = new LineEdit { PlaceholderText = "Reason (required for mute and kick)", CustomMinimumSize = new Vector2(420, 0) };
        var announce = new LineEdit { PlaceholderText = "Announcement to everyone", CustomMinimumSize = new Vector2(420, 0) };
        var content = Ui.Ui.VBox(8, Ui.Ui.Text($"Admin panel — {admin.Role}", 20, Ui.Ui.Accent));
        var toggles = Ui.Ui.HBox(8);
        if (Can("admin.invisible"))
            toggles.AddChild(Ui.Ui.Button(admin.Invisible ? "Invisible: on" : "Invisible: off", () => SendAdmin($"invisible {(_admin!.Invisible ? "off" : "on")}")));
        if (Can("admin.noclip"))
            toggles.AddChild(Ui.Ui.Button(admin.Noclip ? "Noclip: on" : "Noclip: off", () => SendAdmin($"noclip {(_admin!.Noclip ? "off" : "on")}")));
        if (_spectating != 0)
            toggles.AddChild(Ui.Ui.Button("Stop spectating", () => SendAdmin("spectate off")));
        toggles.AddChild(Ui.Ui.Button("Leave Admin Mode", () => SendAdmin("exit")));
        content.AddChild(toggles);
        if (Can("server.announce"))
            content.AddChild(Ui.Ui.HBox(8, announce, Ui.Ui.Button("Announce", () =>
            {
                if (announce.Text.Trim().Length > 0)
                    SendAdmin($"announce {announce.Text.Trim()}");
                announce.Text = "";
            })));
        content.AddChild(_adminReason);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(760, 300), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(_adminRows);
        _adminRows.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        content.AddChild(scroll);
        content.AddChild(Ui.Ui.Text("Every action is in the audit log. F9 closes.", 13, Ui.Ui.Muted));
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.13f, 0.95f), BorderColor = new Color(0.98f, 0.6f, 0.35f), BorderWidthLeft = 3,
            ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 12, ContentMarginBottom = 12,
        });
        panel.AddChild(content);
        AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterRight);
        panel.GrowHorizontal = GrowDirection.Begin;
        panel.GrowVertical = GrowDirection.Both;
        panel.OffsetRight = -16;
        _adminPanel = panel;
        ShowRows();
        SendAdmin("players");
    }

    void SetAdminRows(AdminPlayers players)
    {
        AdminPanelRows.Clear();
        AdminPanelRows.AddRange(players.Players);
        ShowRows();
    }

    void ShowRows()
    {
        if (_adminRows is null)
            return;
        Ui.Ui.ClearChildren(_adminRows);
        var me = OnlineSession.Game?.Welcome.PlayerId;
        foreach (var row in AdminPanelRows)
        {
            var flags = (row.Role != "Player" ? $" · {row.Role}" : "") + (row.Elevated ? " · admin" : "") + (row.Invisible ? " · invisible" : "")
                + (row.MutedUntil is { } m ? $" · muted to {m}" : "");
            var line = Ui.Ui.HBox(6, Ui.Ui.Text($"{row.Name} ({row.Account}) — ch {row.Channel}, zone {row.Location.Zone} {row.Location.X},{row.Location.Y}{flags}", 14));
            line.GetChild<Label>(0).SizeFlagsHorizontal = SizeFlags.ExpandFill;
            if (row.Id != me)
                foreach (var (label, action) in Actions(row.Name))
                    line.AddChild(SmallButton(label, action));
            _adminRows.AddChild(line);
        }
    }

    static Button SmallButton(string text, Action action)
    {
        var b = Ui.Ui.Button(text, action, 13);
        b.CustomMinimumSize = new Vector2(0, 26);
        return b;
    }

    /// <summary>The actions this staff member may take on a player, as (label, action).</summary>
    List<(string Label, Action Action)> Actions(string name)
    {
        var list = new List<(string, Action)>();
        string Reason() => _adminReason?.Text.Trim() is { Length: > 0 } r ? r : "";
        if (Can("player.view"))
            list.Add(("Inspect", () => SendAdmin($"inspect {name}")));
        if (Can("world.teleport"))
            list.Add(("Go to", () => SendAdmin($"tp {name}")));
        if (Can("player.summon"))
            list.Add(("Summon", () => SendAdmin($"summon {name}")));
        if (Can("admin.spectate"))
            list.Add(("Spectate", () => SendAdmin($"spectate {name}")));
        if (Can("chat.mute"))
            list.Add(("Mute 10m", () =>
            {
                if (Reason() is { Length: > 0 } r)
                    SendAdmin($"mute {name} 10 {r}");
                else
                    AddSystem("Type a reason first (the box in the admin panel).");
            }));
        if (Can("player.kick"))
            list.Add(("Kick", () =>
            {
                if (Reason() is { Length: > 0 } r)
                    SendAdmin($"kick {name} {r}");
                else
                    AddSystem("Type a reason first (the box in the admin panel).");
            }));
        return list;
    }

    /// <summary>Keeps the panel's list fresh while it's open.</summary>
    void TickAdmin(double delta)
    {
        if (_adminPanel is not null && (_adminRefresh += delta) >= 3)
        {
            _adminRefresh = 0;
            if (_admin is { Elevated: true })
                SendAdmin("players");
            else
                ToggleAdminPanel();
        }
        FollowSpectated();
    }

    // ---- The inspector -----------------------------------------------------------------------------------------------

    /// <summary>An admin reply meant for a window (true when it was shown there instead of in chat).</summary>
    bool ShowAdminWindow(AdminReply reply)
    {
        if (!reply.Topic.StartsWith("inspect:", StringComparison.Ordinal))
            return false;
        _inspector?.QueueFree();
        var lines = Ui.Ui.VBox(4, Ui.Ui.Text($"Inspector — {reply.Topic["inspect:".Length..]}", 20, Ui.Ui.Accent));
        foreach (var line in reply.Lines)
        {
            var label = Ui.Ui.Text(line, 15, wrap: true);
            label.CustomMinimumSize = new Vector2(520, 0);
            lines.AddChild(label);
        }
        lines.AddChild(Ui.Ui.Text("Read-only. Editing players comes with the M4 admin panels.", 13, Ui.Ui.Muted));
        var panel = new PanelContainer();
        lines.AddChild(Ui.Ui.Button("Close", () =>
        {
            panel.QueueFree();
            if (_inspector == panel)
                _inspector = null;
        }));
        panel.AddChild(Ui.Ui.Margin(lines, 16));
        AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterLeft);
        panel.GrowVertical = GrowDirection.Both;
        panel.OffsetLeft = 16;
        _inspector = panel;
        return true;
    }

    // ---- Right-click a player ------------------------------------------------------------------------------------------

    /// <summary>Right-click in Admin Mode: the nearest other player under the cursor gets an action menu.</summary>
    bool AdminClick(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } click || _admin is not { Elevated: true })
            return false;
        RemotePlayer? best = null;
        var bestDistance = 48f;
        foreach (var remote in _remotes.Values)
        {
            var body = remote.GlobalPosition + new Vector3(0, 0.9f, 0);
            if (_camera.IsPositionBehind(body))
                continue;
            var distance = _camera.UnprojectPosition(body).DistanceTo(click.Position);
            if (distance < bestDistance)
                (best, bestDistance) = (remote, distance);
        }
        if (best is null)
            return false;
        OpenPlayerMenu(best.PlayerName, click.Position);
        return true;
    }

    /// <summary>The action menu for <paramref name="name"/> at a screen point (public so smoke tests can open it).</summary>
    public void OpenPlayerMenu(string name, Vector2 at)
    {
        _playerMenu?.QueueFree();
        var actions = Actions(name);
        var menu = new PopupMenu();
        menu.AddItem(name);
        menu.SetItemDisabled(0, true);
        menu.AddSeparator();
        foreach (var (label, _) in actions)
            menu.AddItem(label);
        menu.IdPressed += id =>
        {
            var index = (int)id - 2;
            if (index >= 0 && index < actions.Count)
                actions[index].Action();
        };
        AddChild(menu);
        menu.Position = new Vector2I((int)at.X, (int)at.Y);
        menu.Popup();
        _playerMenu = menu;
    }

    /// <summary>The labels of the right-click menu now open; for smoke tests.</summary>
    public IReadOnlyList<string> PlayerMenuItems =>
        _playerMenu is { } m ? Enumerable.Range(0, m.ItemCount).Select(i => m.GetItemText(i)).Where(t => t.Length > 0).ToList() : [];
}
