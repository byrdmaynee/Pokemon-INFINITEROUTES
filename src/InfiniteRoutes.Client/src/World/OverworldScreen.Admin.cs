using Godot;
using InfiniteRoutes.Core.Net;
using OnlineSession = InfiniteRoutes.Client.Online.OnlineSession;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// The in-game admin GUI (BUILD_SPEC §11.2–11.3), shown only in Admin Mode and only with what the role allows (the
/// server checks every action again):
/// <list type="bullet">
/// <item>F9: the <see cref="AdminWindow"/> — players, the player editor, roles and the audit log.</item>
/// <item>Right-clicking another player in the world: quick actions, and opening them in the window.</item>
/// <item>/inspect's answer opens a read-only window.</item>
/// <item>Spectating: the camera follows the watched player (and the map loads around them); /spectate off returns.</item>
/// </list>
/// </summary>
public partial class OverworldScreen
{
    AdminWindow? _adminWindow;
    PanelContainer? _inspector;
    PopupMenu? _playerMenu;
    int _spectating;
    string _spectatingName = "";

    /// <summary>The player being spectated, or 0; for smoke tests.</summary>
    public int SpectatingId => _spectating;

    public bool AdminPanelOpen => _adminWindow is not null && IsInstanceValid(_adminWindow);

    /// <summary>The admin window while it's open; for smoke tests.</summary>
    public AdminWindow? AdminWindowOpen => AdminPanelOpen ? _adminWindow : null;

    /// <summary>The player list the admin window showed last; for smoke tests.</summary>
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
    }

    /// <summary>Admin Mode's state changed: the window follows (and closes when Admin Mode ends).</summary>
    void RefreshAdminPanel()
    {
        if (!AdminPanelOpen)
            return;
        if (_admin is not { Elevated: true })
            ToggleAdminPanel();
        else
            _adminWindow!.StatusChanged();
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

    // ---- The window --------------------------------------------------------------------------------------------------

    /// <summary>F9: opens or closes the admin window (Admin Mode only).</summary>
    public void ToggleAdminPanel()
    {
        if (AdminPanelOpen)
        {
            _adminWindow!.QueueFree();
            _adminWindow = null;
            return;
        }
        if (_admin is not { Elevated: true })
        {
            AddSystem("The admin window needs Admin Mode (/admin).");
            return;
        }
        _adminWindow = new AdminWindow(m => OnlineSession.Game?.Send(m), () => _admin);
        AddChild(_adminWindow);
        _adminWindow.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        _adminWindow.GrowHorizontal = _adminWindow.GrowVertical = GrowDirection.Both;
    }

    /// <summary>Opens the window on a player's page.</summary>
    void OpenInWindow(string name)
    {
        if (!AdminPanelOpen)
            ToggleAdminPanel();
        _adminWindow?.Open(name);
    }

    void SetAdminRows(AdminPlayers players)
    {
        AdminPanelRows.Clear();
        AdminPanelRows.AddRange(players.Players);
        if (AdminPanelOpen)
            _adminWindow!.SetRows(players);
    }

    /// <summary>Hands the window what the server sent for it.</summary>
    void ShowAdminData(Message message)
    {
        if (!AdminPanelOpen)
            return;
        switch (message)
        {
            case AdminPlayerDetail detail:
                _adminWindow!.ShowDetail(detail);
                break;
            case AdminRoles roles:
                _adminWindow!.SetRoles(roles);
                break;
            case AdminAudit audit:
                _adminWindow!.SetAudit(audit);
                break;
        }
    }

    /// <summary>The actions this staff member may take on a player, as (label, action).</summary>
    /// <summary>What anyone can do with another player.</summary>
    List<(string Label, Action Action)> SocialActions(string name) =>
    [
        ("Whisper", () => OpenChatInput($"/w {name} ")),
        ("Send a message", () => WithPlayerMenu(m => m.OpenConversation(name))),
        ("Send mail", () => WithPlayerMenu(m => m.WriteTo(name))),
        ("Offer a trade", () => WithPlayerMenu(m => m.TradeWith(name))),
        ("Add friend", () => OnlineSession.Game?.Send(new SocialAction("friend-add", new Dictionary<string, string> { ["name"] = name }))),
        ("Block", () => OnlineSession.Game?.Send(new SocialAction("block", new Dictionary<string, string> { ["name"] = name }))),
    ];

    /// <summary>Opens the player menu (if it isn't) and does something in it.</summary>
    void WithPlayerMenu(Action<PlayerMenu> act)
    {
        if (PlayerMenuOpen is null)
            ToggleOnlineMenu();
        if (PlayerMenuOpen is { } menu)
            act(menu);
    }

    List<(string Label, Action Action)> Actions(string name)
    {
        var list = new List<(string, Action)>();
        if (Can("player.view"))
            list.Add(("Open in admin window", () => OpenInWindow(name)));
        if (Can("world.teleport"))
            list.Add(("Go to", () => SendAdmin($"tp {name}")));
        if (Can("player.summon"))
            list.Add(("Summon", () => SendAdmin($"summon {name}")));
        if (Can("admin.spectate"))
            list.Add(("Spectate", () => SendAdmin($"spectate {name}")));
        if (Can("player.view"))
            list.Add(("Inspect", () => SendAdmin($"inspect {name}")));
        return list;
    }

    /// <summary>Closes the window when Admin Mode ends.</summary>
    void TickAdmin(double delta)
    {
        if (_adminWindow is not null && (!IsInstanceValid(_adminWindow) || _admin is not { Elevated: true }))
        {
            if (IsInstanceValid(_adminWindow))
                _adminWindow.QueueFree();
            _adminWindow = null;
        }
        FollowSpectated();
    }

    // ---- The inspector -----------------------------------------------------------------------------------------------

    /// <summary>An admin reply meant for a window (true when it was shown there instead of in chat).</summary>
    bool ShowAdminWindow(AdminReply reply)
    {
        if (AdminPanelOpen && reply.Topic.Length == 0)
            _adminWindow!.ShowReply(reply);
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
        lines.AddChild(Ui.Ui.Text("Read-only. F9 → Player edits players.", 13, Ui.Ui.Muted));
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

    /// <summary>
    /// Clicking another player (either button): the nearest one under the cursor gets a menu — whisper, message, mail,
    /// trade, friend, block — and, in Admin Mode, the staff actions.
    /// </summary>
    bool PlayerClick(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right or MouseButton.Left } click || _onlineMenu is not null)
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
        var actions = SocialActions(name);
        var staff = Actions(name);
        var menu = new PopupMenu();
        menu.AddItem(name);
        menu.SetItemDisabled(0, true);
        menu.AddSeparator();
        foreach (var (label, _) in actions)
            menu.AddItem(label);
        if (staff.Count > 0)
        {
            menu.AddSeparator("Staff");
            actions.Add(("", () => { }));   // the separator's slot
            foreach (var (label, _) in staff)
                menu.AddItem(label);
            actions.AddRange(staff);
        }
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

    /// <summary>Opens a player in the admin window; for smoke tests.</summary>
    public void OpenPlayerForTest(string name) => OpenInWindow(name);
}
