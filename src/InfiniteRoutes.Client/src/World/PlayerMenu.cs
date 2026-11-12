using Godot;
using InfiniteRoutes.Client.Ui;
using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// The player menu online (X): the game the server holds (party, Bag, trainer card), the Global Market (buy, sell and
/// trade offers), friends, mail and direct messages — and the InfiniteLoots Shop tab, not open yet. Everything is the
/// server's: this window asks for what it shows and sends what the player does, and the server answers with
/// <see cref="SocialReply"/> lines and fresh lists.
/// </summary>
public sealed partial class PlayerMenu : PanelContainer
{
    public const int GameTab = 0, QuestsTab = 1, MarketTab = 2, FriendsTab = 3, MailTab = 4, MessagesTab = 5, ShopTab = 6;
    const int Buy = 0, Sell = 1, Trade = 2;

    readonly Action<Message> _send;
    readonly Func<SaveGame?> _state;
    readonly Func<int, string> _zoneName;
    readonly string _me;

    TabContainer _tabs = null!;
    TabContainer _market = null!;
    Label _status = null!;
    VBoxContainer _game = null!, _buyRows = null!, _sellBox = null!, _tradeBox = null!, _friendRows = null!, _mailList = null!,
        _letter = null!, _threads = null!, _conversation = null!;
    ScrollContainer _conversationScroll = null!;
    LineEdit _search = null!, _dmTo = null!, _dmText = null!, _mailTo = null!, _friendName = null!, _tradeTo = null!;
    OptionButton _kind = null!, _sort = null!;
    SpinBox _maxPrice = null!;
    SocialBadges _badges = new(0, 0, 0, 0);
    TradeList? _trades;
    MarketPage? _mine;
    string _open = "";

    // Smoke-test views of what the window shows.
    public MarketPage? LastSearch { get; private set; }
    public TradeList? TradesShown => _trades;
    public FriendsList? FriendsShown { get; private set; }
    public MailList? MailShown { get; private set; }
    public DmConversation? ConversationShown { get; private set; }
    public string StatusText => _status.Text;
    public int CurrentTab => _tabs.CurrentTab;
    public bool ShopDisabled => _tabs.IsTabDisabled(ShopTab);

    public PlayerMenu() : this(_ => { }, () => null, _ => "", "") { }

    public PlayerMenu(Action<Message> send, Func<SaveGame?> state, Func<int, string> zoneName, string me)
    {
        _send = send;
        _state = state;
        _zoneName = zoneName;
        _me = me;
    }

    static BattleData? Data => OfflineSession.Data;

    void Do(string verb, params (string Key, string Value)[] args) =>
        _send(new SocialAction(verb, args.ToDictionary(a => a.Key, a => a.Value)));

    public override void _Ready()
    {
        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.1f, 0.14f, 0.97f), BorderColor = Ui.Ui.Accent, BorderWidthLeft = 3, BorderWidthTop = 3,
            BorderWidthRight = 3, BorderWidthBottom = 3, ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 10, ContentMarginBottom = 10,
        });
        CustomMinimumSize = new Vector2(1100, 640);
        _status = Ui.Ui.Text("", 15, Ui.Ui.Muted, wrap: true);
        _status.CustomMinimumSize = new Vector2(900, 0);
        _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 540) };
        _tabs.AddChild(GameTabPage());
        _tabs.AddChild(QuestsTabPage());
        _tabs.AddChild(MarketTabPage());
        _tabs.AddChild(FriendsTabPage());
        _tabs.AddChild(MailTabPage());
        _tabs.AddChild(MessagesTabPage());
        _tabs.AddChild(ShopTabPage());
        _tabs.SetTabDisabled(ShopTab, true);
        _tabs.SetTabTooltip(ShopTab, "Coming later");
        _tabs.TabChanged += tab => Refresh((int)tab);
        AddChild(Ui.Ui.VBox(8,
            Ui.Ui.HBox(12, Ui.Ui.Text(_me, 20, Ui.Ui.Accent), Ui.Ui.Spacer(), Ui.Ui.Button("Close (X)", () => QueueFree())),
            _tabs, _status));
        UpdateTitles();
        Do("badges");
    }

    /// <summary>Asks the server for what a tab shows.</summary>
    void Refresh(int tab)
    {
        switch (tab)
        {
            case GameTab:
                ShowGame();
                break;
            case QuestsTab:
                ShowQuests();
                break;
            case MarketTab:
                RefreshMarket(_market.CurrentTab);
                break;
            case FriendsTab:
                Do("friends");
                break;
            case MailTab:
                Do("mails");
                break;
            case MessagesTab:
                Do("dm-threads");
                if (_open.Length > 0)
                    Do("dm-open", ("with", _open));
                break;
        }
    }

    void RefreshMarket(int sub)
    {
        switch (sub)
        {
            case Buy:
                Search();
                break;
            case Sell:
                ShowSell();
                Do("market-mine");
                break;
            case Trade:
                ShowTrade();
                Do("trades");
                break;
        }
    }

    /// <summary>Opens a tab (from the HUD or the friends list); for smoke tests too.</summary>
    public void OpenTab(int tab, int marketSub = -1)
    {
        if (tab == ShopTab)
            return;
        if (marketSub >= 0)
            _market.CurrentTab = marketSub;
        if (_tabs.CurrentTab == tab)
            Refresh(tab);
        else
            _tabs.CurrentTab = tab;
    }

    static Button Small(string text, Action action, bool enabled = true)
    {
        var b = Ui.Ui.Button(text, action, 14);
        b.CustomMinimumSize = new Vector2(0, 28);
        b.Disabled = !enabled;
        return b;
    }

    static ScrollContainer Scroll(Control inside)
    {
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        inside.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(inside);
        return scroll;
    }

    static SpinBox Number(int min, int max, int value, int width = 110) =>
        new() { MinValue = min, MaxValue = max, Value = value, CustomMinimumSize = new Vector2(width, 0) };

    static Label Wrapped(string text, int size = 15, Color? color = null)
    {
        var label = Ui.Ui.Text(text, size, color, wrap: true);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.CustomMinimumSize = new Vector2(200, 0);
        return label;
    }

    static string ItemName(int id) => Data?.ItemName(id) ?? $"Item {id}";

    static string SpeciesName(int id) => Data?.HasSpecies(id) == true ? Data.Species(id).Name : $"#{id}";

    static string Mon(PokemonSave p) =>
        $"{(p.Nickname is { } n ? $"{n} ({SpeciesName(p.Species)})" : SpeciesName(p.Species))} Lv{p.Level}{(p.Shiny ? " ✦" : "")}";

    static string Mon(string? json) => json is not null && SaveGame.PokemonFromJson(json) is { } p ? Mon(p) : "a Pokémon";

    static string Goods(int money, int item, int count, string? pokemon)
    {
        var parts = new List<string>();
        if (money > 0)
            parts.Add($"₽{money:N0}");
        if (item > 0 && count > 0)
            parts.Add($"{ItemName(item)} ×{count}");
        if (pokemon is not null)
            parts.Add(Mon(pokemon));
        return parts.Count == 0 ? "nothing" : string.Join(" + ", parts);
    }

    static string Ago(DateTime utc)
    {
        var t = DateTime.UtcNow - utc;
        return t.TotalMinutes < 1 ? "just now" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min ago" : t.TotalDays < 1 ? $"{(int)t.TotalHours} h ago" : $"{(int)t.TotalDays} d ago";
    }

    static string Left(DateTime utc)
    {
        var t = utc - DateTime.UtcNow;
        return t.TotalHours < 1 ? $"{Math.Max(0, (int)t.TotalMinutes)} min left" : t.TotalDays < 1 ? $"{(int)t.TotalHours} h left" : $"{(int)t.TotalDays} d left";
    }

    /// <summary>The party then the PC, for picking a Pokémon to sell, trade or send: (label, where, index).</summary>
    List<(string Label, string Where, int Index)> MyPokemon()
    {
        var list = new List<(string, string, int)>();
        if (_state() is not { } s)
            return list;
        for (var i = 0; i < s.Party.Count; i++)
            list.Add(($"Party {i + 1}: {Mon(s.Party[i])}", "party", i));
        for (var i = 0; i < s.Box.Count; i++)
            list.Add(($"PC {i + 1}: {Mon(s.Box[i])}", "box", i));
        return list;
    }

    // ---- Server answers ---------------------------------------------------------------------------------------------

    public void ShowReply(SocialReply reply)
    {
        _status.Text = reply.Text;
        _status.AddThemeColorOverride("font_color", reply.Ok ? Ui.Ui.Good : Ui.Ui.Bad);
        if (!reply.Ok)
            return;
        // Something changed: the lists the server doesn't send by itself are asked for again.
        if (_tabs.CurrentTab == MarketTab && _market.CurrentTab == Buy)
            Search();
        else if (_tabs.CurrentTab == MailTab && reply.Topic != "mail")
            Do("mails");
    }

    public void SetBadges(SocialBadges badges)
    {
        _badges = badges;
        UpdateTitles();
    }

    void UpdateTitles()
    {
        static string Title(string name, int n) => n > 0 ? $"{name} ({n})" : name;
        _tabs.SetTabTitle(GameTab, "Pokémon & Bag");
        _tabs.SetTabTitle(QuestsTab, "Quests");
        _tabs.SetTabTitle(MarketTab, Title("Market", _badges.Trades));
        _tabs.SetTabTitle(FriendsTab, Title("Friends", _badges.Requests));
        _tabs.SetTabTitle(MailTab, Title("Mail", _badges.Mail));
        _tabs.SetTabTitle(MessagesTab, Title("Messages", _badges.Messages));
        _tabs.SetTabTitle(ShopTab, "InfiniteLoots Shop");
        _market.SetTabTitle(Trade, Title("Trade", _badges.Trades));
    }

    /// <summary>The game changed (the server sent it): the tabs built from it are redrawn.</summary>
    public void StateChanged()
    {
        if (_tabs.CurrentTab == GameTab)
            ShowGame();
        else if (_tabs.CurrentTab == QuestsTab)
            ShowQuests();
        else if (_tabs.CurrentTab == MarketTab && _market.CurrentTab == Sell)
            ShowSell();
    }

    // ---- Pokémon & Bag ----------------------------------------------------------------------------------------------

    Control GameTabPage()
    {
        _game = Ui.Ui.VBox(8);
        var page = Scroll(_game);
        page.Name = "Pokémon & Bag";
        ShowGame();
        return page;
    }

    void ShowGame()
    {
        Ui.Ui.ClearChildren(_game);
        if (_state() is not { } state)
        {
            _game.AddChild(Ui.Ui.Text("This server doesn't keep a game for you (it has no battle data).", 16, Ui.Ui.Muted));
            return;
        }
        var unlocked = state.Progress.Caught >= GameState.BuddyCatches;
        _game.AddChild(Ui.Ui.HBox(12, Ui.Ui.Text("Party", 17, Ui.Ui.Muted), Ui.Ui.Text(unlocked
            ? "Active Buddy: one party Pokémon can follow you around."
            : $"Catch {GameState.BuddyCatches} Pokémon to unlock Active Buddy ({state.Progress.Caught}/{GameState.BuddyCatches}).", 14, Ui.Ui.Muted)));
        var game = Game();
        var assets = ClientServices.UnovaAssets;
        for (var i = 0; i < state.Party.Count; i++)
        {
            var p = state.Party[i];
            var index = i;
            var buddy = state.Buddy == i;
            Control shown;
            if (game is not null && i < game.Party.Count)
            {
                var mon = game.Party[i];
                var moves = string.Join("   ", mon.Moves.Select(m => $"{m.Move.Name} {m.Pp}/{m.MaxPp}"));
                shown = MonUi.Card(assets, mon, moves, mon.HeldItem != 0 ? ItemName(mon.HeldItem) : null, lead: i == 0, buddy: buddy);
            }
            else
                shown = Ui.Ui.Text($"{(buddy ? "♥ " : "")}{Mon(p)}   HP {p.Hp}{(p.Status != Core.Data.Ailment.None ? "  " + p.Status : "")}", 16);
            shown.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            card.AddThemeStyleboxOverride("panel", new StyleBoxFlat
            {
                BgColor = new Color(0.13f, 0.16f, 0.21f), BorderColor = i == 0 ? Ui.Ui.Accent : new Color(0.25f, 0.3f, 0.38f),
                BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
                CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
                ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 6, ContentMarginBottom = 6,
            });
            card.AddChild(shown);
            var row = Ui.Ui.HBox(10, card);
            row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            if (i > 0)
                row.AddChild(Small("Make lead", () => _send(new SetLead(index))));
            row.AddChild(buddy
                ? Small("Put away", () => _send(new SetBuddy(-1)))
                : Small("Active Buddy", () => _send(new SetBuddy(index)), unlocked && p.Hp > 0));
            _game.AddChild(row);
        }
        _game.AddChild(Ui.Ui.Text($"PC: {state.Box.Count} Pokémon", 15, Ui.Ui.Muted));
        _game.AddChild(Ui.Ui.Text("Bag", 17, Ui.Ui.Muted));
        if (state.Bag.Count == 0)
            _game.AddChild(Wrapped("Empty"));
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 18);
        foreach (var b in state.Bag)
            grid.AddChild(Ui.Ui.HBox(6, MonUi.ItemIcon(assets, b.Item, 32), Ui.Ui.Text($"{ItemName(b.Item)}  ×{b.Count}", 15)));
        _game.AddChild(grid);
        _game.AddChild(Ui.Ui.Text("Trainer card", 17, Ui.Ui.Muted));
        _game.AddChild(Ui.Ui.Text($"Money ₽{state.Progress.Money:N0} · badges {state.Progress.Badges.Count} · caught {state.Progress.Caught} · wild wins {state.Progress.WildWins}", 15));
    }

    // ---- Smoke-test hooks: drive the window the way a person would ------------------------------------------------

    Node TestScope => _tabs.CurrentTab == MarketTab ? _market.GetCurrentTabControl() : _tabs.GetCurrentTabControl();

    static IEnumerable<T> All<T>(Node node) where T : Node
    {
        foreach (var child in node.GetChildren())
        {
            if (child is T t)
                yield return t;
            foreach (var inner in All<T>(child))
                yield return inner;
        }
    }

    public string TestPress(string text)
    {
        var button = All<Button>(TestScope).FirstOrDefault(b => !b.Disabled && b.Text.Contains(text, StringComparison.Ordinal));
        if (button is null)
            return $"no enabled button \"{text}\"";
        button.EmitSignal(BaseButton.SignalName.Pressed);
        return $"pressed \"{button.Text}\"";
    }

    public string TestText(int index, string value)
    {
        var edits = All<LineEdit>(TestScope).Where(e => e.GetParent() is not SpinBox).ToList();
        if (index >= edits.Count)
            return $"no text box {index} (of {edits.Count})";
        edits[index].Text = value;
        return $"text box {index} = {value}";
    }

    public string TestSpin(int index, double value)
    {
        var spins = All<SpinBox>(TestScope).ToList();
        if (index >= spins.Count)
            return $"no number box {index} (of {spins.Count})";
        spins[index].Value = value;
        return $"number box {index} = {value}";
    }

    public string TestLabels() => string.Join(" / ", All<Control>(TestScope).Select(c => c switch { Label l => l.Text, Button b => $"[{b.Text}]", _ => "" })
        .Where(t => t.Length > 0));

    public void TestMarket(int sub) => _market.CurrentTab = sub;

    // ---- InfiniteLoots Shop ---------------------------------------------------------------------------------------------

    static Control ShopTabPage()
    {
        var page = Ui.Ui.VBox(10, Ui.Ui.Text("InfiniteLoots Shop", 22, Ui.Ui.Accent),
            Ui.Ui.Text("Coming later: a shop for game items and more, bought with real money.", 16, Ui.Ui.Muted));
        page.Name = "InfiniteLoots Shop";
        return page;
    }
}
