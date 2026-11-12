using Godot;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;
using OnlineSession = InfiniteRoutes.Client.Online.OnlineSession;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// Online chat, Admin Mode and the online menu.
/// <list type="bullet">
/// <item>Chat (bottom left): Enter or T to type. Plain text is local (players near you on your channel); <c>/g</c>
/// global, <c>/w name</c> whisper, <c>/r</c> reply to the last whisper, <c>/ch n</c> channel, <c>/help</c>.</item>
/// <item>Staff: <c>/admin</c> asks for the password and an authenticator code to enter Admin Mode; any other
/// <c>/command</c> goes to the server's admin console (it checks permissions). The HUD shows Admin Mode.</item>
/// <item>X opens the online menu: the party the server holds (make one the lead), the Bag, the trainer card.</item>
/// </list>
/// </summary>
public partial class OverworldScreen
{
    const int ChatHistory = 80;

    PanelContainer? _chatPanel;
    RichTextLabel? _chatLog;
    LineEdit? _chatInput;
    PlayerMenu? _onlineMenu;
    SocialBadges _badges = new(0, 0, 0, 0);

    /// <summary>The player menu while it's open; for smoke tests.</summary>
    public PlayerMenu? PlayerMenuOpen => _onlineMenu is not null && IsInstanceValid(_onlineMenu) ? _onlineMenu : null;
    PanelContainer? _elevatePrompt;
    string? _replyTo;
    AdminStatus? _admin;
    Core.Player.SaveGame? _onlineState;

    /// <summary>True while the chat line or an online panel has the keyboard (no walking then).</summary>
    bool OnlineUiBusy => _chatInput?.HasFocus() == true || _elevatePrompt is not null || _onlineMenu is not null || _spectating != 0 || AdminPanelOpen;

    /// <summary>Chat lines shown so far, as plain text; for smoke tests.</summary>
    public List<string> ChatLines { get; } = [];

    /// <summary>The game state the server sent last; for smoke tests.</summary>
    public Core.Player.SaveGame? OnlineState => _onlineState;

    public bool AdminMode => _admin?.Elevated == true;

    void BuildChat()
    {
        if (!_online)
            return;
        _chatPanel = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        _chatPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.45f), ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6 });
        _chatLog = new RichTextLabel
        {
            BbcodeEnabled = true, ScrollFollowing = true, CustomMinimumSize = new Vector2(520, 170), MouseFilter = MouseFilterEnum.Ignore,
            FitContent = false,
        };
        _chatLog.AddThemeFontSizeOverride("normal_font_size", 15);
        _chatInput = new LineEdit { PlaceholderText = "Enter to chat · /g global · /w name whisper · /help", Visible = false };
        _chatInput.TextSubmitted += SubmitChat;
        _chatInput.GuiInput += e =>
        {
            if (e.IsActionPressed("ui_cancel"))
            {
                CloseChatInput();
                GetViewport().SetInputAsHandled();
            }
        };
        _chatPanel.AddChild(Ui.Ui.VBox(4, _chatLog, _chatInput));
        AddChild(_chatPanel);
        _chatPanel.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomLeft);
        _chatPanel.GrowVertical = GrowDirection.Begin;
        _chatPanel.OffsetLeft = 12;
        _chatPanel.OffsetBottom = -34;
        AddChatLine("[color=#9ad]Connected. Enter to chat — /help for commands.[/color]", "Connected.");
    }

    void OpenChatInput(string prefill = "")
    {
        if (_chatInput is null)
            return;
        _chatInput.Visible = true;
        _chatInput.Text = prefill;
        _chatInput.GrabFocus();
        _chatInput.CaretColumn = prefill.Length;
    }

    void CloseChatInput()
    {
        if (_chatInput is null)
            return;
        _chatInput.Text = "";
        _chatInput.ReleaseFocus();
        _chatInput.Visible = false;
    }

    /// <summary>Sends a chat line or runs a /command (public so smoke tests can chat).</summary>
    public void SubmitChat(string text)
    {
        CloseChatInput();
        text = text.Trim();
        if (text.Length == 0 || OnlineSession.Game is not { } game)
            return;
        if (!text.StartsWith('/'))
        {
            game.Send(new Say(ChatKind.Local, text));
            return;
        }
        var space = text.IndexOf(' ');
        var verb = (space < 0 ? text[1..] : text[1..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : text[(space + 1)..].Trim();
        switch (verb)
        {
            case "g" or "global":
                game.Send(new Say(ChatKind.Global, rest));
                break;
            case "l" or "local":
                game.Send(new Say(ChatKind.Local, rest));
                break;
            case "w" or "whisper" or "msg":
            {
                var split = rest.IndexOf(' ');
                if (split < 0)
                    AddSystem("Whisper with /w <name> <message>.");
                else
                    game.Send(new Say(ChatKind.Whisper, rest[(split + 1)..], rest[..split]));
                break;
            }
            case "r" or "reply":
                if (_replyTo is null)
                    AddSystem("Nobody has whispered to you yet.");
                else
                    game.Send(new Say(ChatKind.Whisper, rest, _replyTo));
                break;
            case "ch" or "channel":
                if (int.TryParse(rest, out var channel))
                    game.Send(new SwitchChannel(channel));
                else
                    AddSystem("Switch channel with /ch <number>.");
                break;
            case "admin":
                if (_admin is null)
                    AddSystem("Your account has no staff role on this server.");
                else if (_admin.Elevated)
                    AddSystem("You're already in Admin Mode (/exit leaves it).");
                else
                    ShowElevatePrompt();
                break;
            case "help" when !AdminMode:
                AddSystem("Chat: just type for players near you · /g global · /w name whisper · /r reply · /ch n channel" +
                    (_admin is not null ? " · /admin Admin Mode" : ""));
                break;
            default:
                // Everything else is an admin console command; the server decides what's allowed.
                game.Send(new AdminCommand(text[1..]));
                break;
        }
    }

    void AddSystem(string text) => AddChatLine($"[color=#9ad]{Escape(text)}[/color]", text);

    static string Escape(string text) => text.Replace("[", "[lb]");

    void AddChatLine(string bbcode, string plain)
    {
        if (_chatLog is null)
            return;
        ChatLines.Add(plain);
        if (ChatLines.Count > ChatHistory)
            ChatLines.RemoveAt(0);
        _chatLog.AppendText((_chatLog.GetParsedText().Length > 0 ? "\n" : "") + bbcode);
    }

    void ShowChat(ChatLine line)
    {
        var staff = line.Staff ? "[color=#f96][Staff][/color] " : "";
        var from = Escape(line.From);
        var text = Escape(line.Text);
        switch (line.Kind)
        {
            case ChatKind.Global:
                AddChatLine($"[color=#fc6][G] {staff}{from}:[/color] {text}", $"[G] {line.From}: {line.Text}");
                break;
            case ChatKind.Whisper:
                var mine = line.From == OnlineSession.Character?.Name;
                if (!mine)
                    _replyTo = line.From;
                var who = mine ? $"to {Escape(line.To ?? "")}" : $"from {from}";
                AddChatLine($"[color=#f9c][W] {staff}{who}:[/color] {text}", $"[W] {(mine ? "to " + line.To : "from " + line.From)}: {line.Text}");
                break;
            case ChatKind.System:
                AddChatLine($"[color=#9ad]{(from.Length > 0 ? from + ": " : "")}{text}[/color]", line.From.Length > 0 ? $"{line.From}: {line.Text}" : line.Text);
                break;
            default:
                AddChatLine($"{staff}[color=#fff]{from}:[/color] {text}", $"{line.From}: {line.Text}");
                break;
        }
    }

    void ShowAdminReply(AdminReply reply)
    {
        foreach (var line in reply.Lines)
            AddChatLine($"[color={(reply.Ok ? "#fb7" : "#f77")}]{Escape(line)}[/color]", line);
    }

    void SetAdminStatus(AdminStatus status)
    {
        var wasElevated = _admin?.Elevated == true;
        _admin = status;
        // Turning it on is confirmed by the server's reply; going off (on /exit or a timeout) is said here.
        if (wasElevated && !status.Elevated)
            AddSystem("Admin Mode off.");
        UpdateHud();
        RefreshAdminPanel();
    }

    /// <summary>The HUD line for Admin Mode, when it's on.</summary>
    string AdminHud => (_admin is { Elevated: true } a
        ? $"\n⚑ ADMIN MODE ({a.Role}){(a.Invisible ? " · invisible" : "")}{(a.Noclip ? " · noclip" : "")} · F9 panel"
        : "") + SpectateHud + SocialHud;

    bool AdminNoclip => _admin is { Elevated: true, Noclip: true };

    void ShowElevatePrompt()
    {
        var password = new LineEdit { PlaceholderText = "Account password", Secret = true, CustomMinimumSize = new Vector2(320, 0) };
        var code = new LineEdit { PlaceholderText = "6-digit code from your authenticator app" };
        void Close()
        {
            _elevatePrompt?.QueueFree();
            _elevatePrompt = null;
        }
        void Send()
        {
            OnlineSession.Game?.Send(new Elevate(password.Text, code.Text));
            Close();
        }
        code.TextSubmitted += _ => Send();
        password.TextSubmitted += _ => code.GrabFocus();
        var panel = new PanelContainer();
        panel.AddChild(Ui.Ui.Margin(Ui.Ui.VBox(10,
            Ui.Ui.Text("Enter Admin Mode", 20, Ui.Ui.Accent),
            Ui.Ui.Text("Every admin action is recorded in the audit log.", 14, Ui.Ui.Muted),
            password, code,
            Ui.Ui.HBox(10, Ui.Ui.Button("Enter", Send), Ui.Ui.Button("Cancel", Close))), 16));
        AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        panel.GrowHorizontal = panel.GrowVertical = GrowDirection.Both;
        _elevatePrompt = panel;
        password.CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>The state the server holds for this character, for the online menu.</summary>
    void SetOnlineState(string json)
    {
        try
        {
            _onlineState = Core.Player.SaveGame.FromJson(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }
        // An open menu shows the new state at once.
        PlayerMenuOpen?.StateChanged();
    }

    /// <summary>
    /// X online: opens or closes the player menu (the game the server holds, the market, friends, mail, messages).
    /// </summary>
    void ToggleOnlineMenu()
    {
        if (_onlineMenu is not null)
        {
            if (IsInstanceValid(_onlineMenu))
                _onlineMenu.QueueFree();
            _onlineMenu = null;
            return;
        }
        var menu = new PlayerMenu(m => OnlineSession.Game?.Send(m), () => _onlineState,
            zone => _zones.TryGetValue(zone, out var z) && z.Location.Length > 0 ? z.Location : $"zone {zone}", OnlineSession.Character?.Name ?? "")
        {
            Game = () => OnlineGame,
        };
        menu.TreeExited += () =>
        {
            if (_onlineMenu == menu)
                _onlineMenu = null;
        };
        AddChild(menu);
        menu.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        menu.GrowHorizontal = menu.GrowVertical = GrowDirection.Both;
        menu.SetBadges(_badges);
        _onlineMenu = menu;
    }

    /// <summary>What the server sent for the player menu: to the menu if it's open, else a line or a toast.</summary>
    void ShowSocial(Message message)
    {
        var menu = PlayerMenuOpen;
        switch (message)
        {
            case SocialBadges badges:
                _badges = badges;
                UpdateHud();
                menu?.SetBadges(badges);
                break;
            case SocialReply reply:
                if (menu is not null)
                    menu.ShowReply(reply);
                else if (reply.Text.Length > 0)
                    Toast(reply.Text);
                break;
            case DmReceived dm:
                if (menu is not null)
                    menu.AddDm(dm);
                if ((menu is null || menu.CurrentTab != PlayerMenu.MessagesTab)
                    && !string.Equals(dm.Message.From, OnlineSession.Character?.Name, StringComparison.OrdinalIgnoreCase))
                    AddSystem($"✉ {dm.Message.From}: {dm.Message.Text}   (X → Messages)");
                break;
            case MarketPage page:
                menu?.SetMarket(page);
                break;
            case TradeList trades:
                menu?.SetTrades(trades);
                break;
            case FriendsList friends:
                menu?.SetFriends(friends);
                break;
            case MailList mail:
                menu?.SetMail(mail);
                break;
            case DmThreads threads:
                menu?.SetThreads(threads);
                break;
            case DmConversation conversation:
                menu?.SetConversation(conversation);
                break;
        }
    }

    /// <summary>What's waiting (mail, messages, friend requests, trade offers), for the HUD.</summary>
    string SocialHud
    {
        get
        {
            var parts = new List<string>();
            if (_badges.Mail > 0)
                parts.Add($"✉ {_badges.Mail} mail");
            if (_badges.Messages > 0)
                parts.Add($"💬 {_badges.Messages}");
            if (_badges.Requests > 0)
                parts.Add($"👋 {_badges.Requests}");
            if (_badges.Trades > 0)
                parts.Add($"⇄ {_badges.Trades} offer{(_badges.Trades == 1 ? "" : "s")}");
            return parts.Count > 0 ? $"\n{string.Join("  ", parts)}  (X)" : "";
        }
    }

    /// <summary>Opens or closes the online menu; for smoke tests.</summary>
    public void OpenOnlineMenuForTest() => ToggleOnlineMenu();

    /// <summary>Keys for chat and the online menu; true when handled.</summary>
    bool OnlineInput(InputEvent e)
    {
        if (!_online || _chatInput is null)
            return false;
        if (_chatInput.HasFocus() || _elevatePrompt is not null)
            return false;
        if (PlayerClick(e))
            return true;
        if (e is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode is Key.Enter or Key.KpEnter or Key.T && _onlineMenu is null)
            {
                OpenChatInput();
                return true;
            }
            if (key.Keycode == Key.Slash && _onlineMenu is null)
            {
                OpenChatInput("/");
                return true;
            }
            if (key.Keycode == Key.F9)
            {
                ToggleAdminPanel();
                return true;
            }
            if (key.Keycode == Key.X)
            {
                ToggleOnlineMenu();
                return true;
            }
        }
        if (e.IsActionPressed("ui_cancel") && _onlineMenu is not null)
        {
            ToggleOnlineMenu();
            return true;
        }
        return false;
    }
}
