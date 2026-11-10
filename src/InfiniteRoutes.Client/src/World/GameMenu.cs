using Godot;
using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Core.Player;
using InfiniteRoutes.Extractor.Extraction;
using U = InfiniteRoutes.Client.Ui.Ui;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// The overworld X menu, in tabs: Party, Bag, Badges, Quests, Mail, Friends, Market and the Trainer Card, plus a PC
/// or Poké Mart tab when opened at a Pokémon Center's PC or shop counter.
/// Q / E (or Page Up / Down) switch tabs; Esc steps back out of a choice, then closes; X closes.
/// Friends and the market run on sample data offline (the server brings the real ones in M4/M5).
/// </summary>
public partial class GameMenu : Control
{
    public enum Tab { Party, Bag, Badges, Quests, Mail, Friends, Market, Card, Pc, Mart }

    enum MarketView { Buy, Mine, Sell }

    [Signal]
    public delegate void ClosedEventHandler();

    /// <summary>The player threw their save away; the world sends them home.</summary>
    [Signal]
    public delegate void StartedOverEventHandler();

    static readonly Dictionary<string, Texture2D?> Icons = new();

    readonly GameAssets _assets;
    /// <summary>The counter the menu was opened at (<see cref="Tab.Pc"/> or <see cref="Tab.Mart"/>): only then is its tab there.</summary>
    readonly Tab? _counter;
    /// <summary>The town the player is in, for the Mart's specialty shelf.</summary>
    readonly string _town;
    Tab _tab;
    readonly Dictionary<Tab, Button> _tabButtons = new();
    VBoxContainer _body = null!;
    Label _hint = null!, _wallet = null!;

    // Choices in progress, kept across rebuilds of the tab.
    int _useItem;
    string? _questId;
    MailMessage? _mail;
    MarketView _marketView;
    MarketQuery _query = new();
    (ListingKind Kind, int Item, Pokemon? Mon)? _selling;
    bool _confirmStartOver;
    int _box, _pcPick = -1;
    bool _confirmRelease;

    public GameMenu() : this(null!) { }

    public GameMenu(GameAssets assets, Tab tab = Tab.Party, Tab? counter = null, string town = "")
    {
        _assets = assets;
        _counter = counter;
        _town = town;
        _tab = tab is Tab.Pc or Tab.Mart && tab != counter ? Tab.Party : tab;
    }

    Tab[] Tabs => Enum.GetValues<Tab>().Where(t => t is not (Tab.Pc or Tab.Mart) || t == _counter).ToArray();

    public Tab Current => _tab;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.5f), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(1120, 640) };
        panel.AddThemeStyleboxOverride("panel", Box(new Color(0.1f, 0.12f, 0.17f, 0.97f), new Color(0.9f, 0.55f, 0.25f), 3, 10, 18));
        AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        panel.GrowHorizontal = panel.GrowVertical = GrowDirection.Both;

        var tabs = U.HBox(4);
        var group = new ButtonGroup();
        foreach (var tab in Tabs)
        {
            var t = tab;
            var button = U.Button(TabTitle(tab), () => Show(t), 17);
            button.ToggleMode = true;
            button.ButtonGroup = group;
            button.FocusMode = FocusModeEnum.None;
            _tabButtons[tab] = button;
            tabs.AddChild(button);
        }
        tabs.AddChild(U.Spacer());
        _wallet = U.Text("", 18, U.Accent);
        tabs.AddChild(_wallet);

        _body = U.VBox(8);
        _body.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _body.SizeFlagsVertical = SizeFlags.ExpandFill;
        _hint = U.Text("", 15, U.Muted, wrap: true);
        var root = U.VBox(10, tabs, new HSeparator(), _body, new HSeparator(), _hint);
        panel.AddChild(root);
        Show(_tab);
    }

    static string TabTitle(Tab tab) => tab switch
    {
        Tab.Mail when OfflineSession.Mail.Unread > 0 => $"Mail ({OfflineSession.Mail.Unread})",
        Tab.Friends when OfflineSession.Friends.OnlineCount > 0 => $"Friends ({OfflineSession.Friends.OnlineCount})",
        Tab.Card => "Trainer Card",
        Tab.Pc => "PC",
        Tab.Mart => "Poké Mart",
        _ => tab.ToString(),
    };

    /// <summary>Switches to <paramref name="tab"/>. Public so smoke tests can visit each tab.</summary>
    public void Show(Tab tab)
    {
        if (!Tabs.Contains(tab))
            return;
        if (tab != _tab)
        {
            _useItem = 0;
            _confirmStartOver = false;
            _pcPick = -1;
            _confirmRelease = false;
        }
        _tab = tab;
        Rebuild(null);
    }

    /// <summary>Redraws the current tab; <paramref name="message"/> replaces the hint line when given.</summary>
    void Rebuild(string? message)
    {
        U.ClearChildren(_body);
        foreach (var (tab, button) in _tabButtons)
        {
            button.Text = TabTitle(tab);
            button.SetPressedNoSignal(tab == _tab);
        }
        _wallet.Text = $"₽{OfflineSession.Money:N0}";
        var hint = _tab switch
        {
            Tab.Party => BuildParty(),
            Tab.Bag => BuildBag(),
            Tab.Badges => BuildBadges(),
            Tab.Quests => BuildQuests(),
            Tab.Mail => BuildMail(),
            Tab.Friends => BuildFriends(),
            Tab.Market => BuildMarket(),
            Tab.Pc => BuildPc(),
            Tab.Mart => BuildMart(),
            _ => BuildCard(),
        };
        _hint.Text = (message is null ? "" : message + "\n") + hint + "   ·   Q / E: switch tabs   ·   X / Esc: close";
        CallDeferred(MethodName.FocusFirst);
    }

    void FocusFirst()
    {
        if (FindButton(_body) is { } b)
            b.GrabFocus();
    }

    static Button? FindButton(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Button { Disabled: false, Visible: true } b)
                return b;
            if (FindButton(child) is { } found)
                return found;
        }
        return null;
    }

    // ── Party ─────────────────────────────────────────────────────────────────────────────────────────────────

    string BuildParty()
    {
        var list = Scroll(U.VBox(6));
        var party = OfflineSession.Party;
        for (var i = 0; i < party.Count; i++)
        {
            var mon = party[i];
            var index = i;
            var status = mon.Fainted ? "FNT" : mon.Status == Ailment.None ? "" : mon.Status.ToString();
            var moves = string.Join(", ", mon.Moves.Select(m => $"{m.Move.Name} {m.Pp}/{m.MaxPp}"));
            var stats = $"Atk {mon.StatValue(Stat.Attack)} · Def {mon.StatValue(Stat.Defense)} · SpA {mon.StatValue(Stat.SpAttack)} · SpD {mon.StatValue(Stat.SpDefense)} · Spe {mon.StatValue(Stat.Speed)} · {mon.Nature} · {mon.AbilityName}";
            var button = Row($"{mon.Name}{GenderMark(mon)}  Lv{mon.Level}   HP {mon.Hp}/{mon.MaxHp}  {status}\n{moves}\n{stats}", () => PickPokemon(index), Icon(mon));
            list.AddChild(button);
        }
        if (OfflineSession.Box.Count > 0)
            list.AddChild(U.Text($"{OfflineSession.Box.Count} Pokémon in the PC: use the PC in any Pokémon Center to swap them in.", 15, U.Muted, wrap: true));
        return _useItem != 0
            ? $"Use the {OfflineSession.Data.ItemName(_useItem)} on which Pokémon?  Esc: cancel"
            : "Pick a Pokémon to make it lead. To use medicine, pick it in the Bag.";
    }

    void PickPokemon(int index)
    {
        var party = OfflineSession.Party;
        if (_useItem != 0)
        {
            var item = _useItem;
            _useItem = 0;
            var message = ItemEffect.For(item) is { } effect ? ItemUse.Apply(effect, party[index]) : null;
            if (message is not null)
                OfflineSession.Bag.Take(item);
            Rebuild(message ?? "It won't have any effect.");
            return;
        }
        if (index > 0)
            (party[0], party[index]) = (party[index], party[0]);
        Rebuild(index > 0 ? $"{party[0].Name} now leads the party." : null);
    }

    // ── PC ────────────────────────────────────────────────────────────────────────────────────────────────────

    string BuildPc()
    {
        var state = OfflineSession.State;
        var columns = U.HBox(24);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        _body.AddChild(columns);

        var party = U.VBox(6, U.Text($"Party   {state.Party.Count} / {GameState.PartySize}", 19, U.Accent));
        party.CustomMinimumSize = new Vector2(330, 0);
        for (var i = 0; i < state.Party.Count; i++)
        {
            var index = i;
            var mon = state.Party[i];
            var status = mon.Fainted ? "  FNT" : mon.Status == Ailment.None ? "" : $"  {mon.Status}";
            party.AddChild(Row($"{mon.Name}{GenderMark(mon)}  Lv{mon.Level}\nHP {mon.Hp}/{mon.MaxHp}{status}", () => PcDone(state.Deposit(index)), Icon(mon), 16));
        }
        columns.AddChild(party);

        var box = U.VBox(8);
        box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        columns.AddChild(box);
        var start = _box * GameState.BoxSize;
        box.AddChild(U.HBox(10,
            U.Button("◀", () => TurnBox(-1)),
            U.Text($"Box {_box + 1}   {Math.Clamp(state.Box.Count - start, 0, GameState.BoxSize)} / {GameState.BoxSize}", 19, U.Accent),
            U.Button("▶", () => TurnBox(1)),
            U.Spacer(),
            U.Text($"{state.Box.Count} stored in {GameState.BoxCount} boxes", 15, U.Muted)));
        var grid = new GridContainer { Columns = 6 };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 6);
        for (var slot = 0; slot < GameState.BoxSize; slot++)
        {
            var index = start + slot;
            if (index >= state.Box.Count)
            {
                var empty = new Panel { CustomMinimumSize = new Vector2(112, 62) };
                empty.AddThemeStyleboxOverride("panel", Box(new Color(1, 1, 1, 0.03f), new Color(1, 1, 1, 0.08f), 1, 6, 0));
                grid.AddChild(empty);
                continue;
            }
            var mon = state.Box[index];
            var button = U.Button($"Lv{mon.Level}", () =>
            {
                _pcPick = index;
                _confirmRelease = false;
                Rebuild(null);
            }, 14);
            button.CustomMinimumSize = new Vector2(112, 62);
            button.Icon = Icon(mon);
            button.ExpandIcon = false;
            button.AddThemeConstantOverride("icon_max_width", 52);
            button.Alignment = HorizontalAlignment.Left;
            button.TooltipText = $"{mon.Name}{GenderMark(mon)} Lv{mon.Level}";
            if (index == _pcPick)
                button.Modulate = new Color(1.25f, 1.15f, 0.8f);
            grid.AddChild(button);
        }
        box.AddChild(grid);

        if (_pcPick >= 0 && _pcPick < state.Box.Count)
        {
            var mon = state.Box[_pcPick];
            var pick = _pcPick;
            var about = U.Text($"{mon.Name}{GenderMark(mon)}  Lv{mon.Level} · {mon.Nature}\n{string.Join(", ", mon.Moves.Select(m => m.Move.Name))}", 16, wrap: true);
            about.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            var withdraw = U.Button("Withdraw", () => PcDone(state.Withdraw(pick)));
            withdraw.Disabled = state.Party.Count >= GameState.PartySize;
            var release = U.Button(_confirmRelease ? $"Really release {mon.Name}?" : "Release…", () =>
            {
                if (!_confirmRelease)
                {
                    _confirmRelease = true;
                    Rebuild($"Released Pokémon don't come back. Press again to release {mon.Name}.");
                    return;
                }
                PcDone(state.Release(pick));
            });
            if (_confirmRelease)
                release.Modulate = new Color(1.3f, 0.7f, 0.7f);
            box.AddChild(U.HBox(10, about, withdraw, release));
        }
        return _pcPick >= 0
            ? "Withdraw it to your party, or release it.  Esc: cancel"
            : "Pick a party Pokémon to deposit it (the PC heals it), or a box Pokémon to withdraw or release it.";
    }

    /// <summary>Selects a PC Pokémon by its place in the PC. Public so smoke tests can show the choice.</summary>
    public void PickPc(int index)
    {
        if (_counter != Tab.Pc)
            return;
        _box = Math.Clamp(index / GameState.BoxSize, 0, GameState.BoxCount - 1);
        _pcPick = index;
        _confirmRelease = false;
        _tab = Tab.Pc;
        Rebuild(null);
    }

    void TurnBox(int step)
    {
        _box = (_box + step + GameState.BoxCount) % GameState.BoxCount;
        _pcPick = -1;
        _confirmRelease = false;
        Rebuild(null);
    }

    void PcDone((bool Ok, string Message) result)
    {
        if (result.Ok)
        {
            _pcPick = -1;
            _confirmRelease = false;
        }
        Rebuild(result.Message);
    }

    // ── Poké Mart ─────────────────────────────────────────────────────────────────────────────────────────────

    string BuildMart()
    {
        var data = OfflineSession.Data;
        var state = OfflineSession.State;
        var columns = U.HBox(24);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        _body.AddChild(columns);

        var buy = U.VBox(6);
        void Shelf(string title, IEnumerable<int> items)
        {
            buy.AddChild(U.Text(title, 19, U.Accent));
            foreach (var item in items.Where(i => data.ItemPrice(i) > 0))
            {
                var id = item;
                var name = U.Text(data.ItemName(item), 17);
                name.CustomMinimumSize = new Vector2(150, 0);
                var row = U.HBox(8, name, U.Text($"₽{data.ItemPrice(item):N0}", 17, U.Accent), U.Spacer(),
                    U.Text($"have {state.Bag.Count(item)}", 15, U.Muted));
                foreach (var n in new[] { 1, 5, 10 })
                {
                    var count = n;
                    var button = U.Button($"×{n}", () => Rebuild(PokeMart.Buy(state, data, id, count).Message), 15);
                    button.Disabled = state.Progress.Money < data.ItemPrice(item) * n;
                    row.AddChild(button);
                }
                buy.AddChild(row);
            }
        }
        Shelf("For sale", PokeMart.StandardStock(state.Progress.Badges.Count));
        buy.AddChild(new HSeparator());
        Shelf(_town == "" ? "Specialties" : $"{_town} specialties", PokeMart.SpecialtyStock(_town));
        var left = WrapScroll(buy);
        left.SizeFlagsStretchRatio = 1.3f;
        columns.AddChild(left);

        var sell = U.VBox(6, U.Text("Sell from your Bag", 19, U.Accent));
        foreach (var (item, count) in state.Bag.Items.ToList())
        {
            var id = item;
            var have = count;
            var each = PokeMart.SellPrice(data, item);
            var row = U.HBox(8, U.Text($"{data.ItemName(item)}  ×{count}", 17), U.Spacer(),
                U.Text(each > 0 ? $"₽{each:N0} each" : "can't be sold", 15, each > 0 ? U.Accent : U.Muted));
            if (each > 0)
            {
                row.AddChild(U.Button("Sell 1", () => Rebuild(PokeMart.Sell(state, data, id, 1).Message), 15));
                row.AddChild(U.Button("All", () => Rebuild(PokeMart.Sell(state, data, id, have).Message), 15));
            }
            sell.AddChild(row);
        }
        if (!state.Bag.Items.Any())
            sell.AddChild(U.Text("Your Bag is empty.", 15, U.Muted));
        columns.AddChild(WrapScroll(sell));
        return $"Ten Poké Balls come with a free Premier Ball. Items sell for half. Badges unlock stock ({state.Progress.Badges.Count}/8).";
    }

    // ── Bag ───────────────────────────────────────────────────────────────────────────────────────────────────

    string BuildBag()
    {
        var pockets = U.HBox(24);
        _body.AddChild(pockets);
        var data = OfflineSession.Data;
        foreach (var (title, filter) in new (string, Func<int, bool>)[]
                 {
                     ("Poké Balls", i => ItemEffect.For(i) is ItemEffect.Ball),
                     ("Medicine", i => ItemEffect.For(i) is ItemEffect.Heal or ItemEffect.Cure or ItemEffect.Revive),
                     ("Other", i => ItemEffect.For(i) is null),
                 })
        {
            var column = U.VBox(6, U.Text(title, 19, U.Accent));
            column.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            foreach (var (item, count) in OfflineSession.Bag.Items.Where(i => filter(i.Item)))
            {
                var id = item;
                var medicine = ItemEffect.For(item) is ItemEffect.Heal or ItemEffect.Cure or ItemEffect.Revive;
                var button = Row($"{data.ItemName(item)}   ×{count}", () =>
                {
                    _useItem = id;
                    _tab = Tab.Party;
                    Rebuild(null);
                });
                button.Disabled = !medicine;
                column.AddChild(button);
            }
            if (column.GetChildCount() == 1)
                column.AddChild(U.Text("Empty", 15, U.Muted));
            pockets.AddChild(column);
        }
        return "Pick a medicine, then a Pokémon to use it on. Balls are thrown in battle; sell items in the Market.";
    }

    // ── Badges ────────────────────────────────────────────────────────────────────────────────────────────────

    string BuildBadges()
    {
        var progress = OfflineSession.Progress;
        _body.AddChild(U.Text($"Unova Badges   {progress.Badges.Count} / {Badges.Unova.Count}", 22, U.Accent));
        var grid = new GridContainer { Columns = 4 };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 18);
        foreach (var badge in Badges.Unova)
        {
            var earned = progress.Badges.TryGetValue(badge.Index, out var when);
            var leaders = string.Join(" / ", badge.Leaders.Where(n => OfflineSession.Trainers.Any(t => t.Name == n && Badges.EarnedBy(t) == badge)));
            var card = U.VBox(4,
                new BadgeIcon(badge, earned) { CustomMinimumSize = new Vector2(84, 84), SizeFlagsHorizontal = SizeFlags.ShrinkCenter },
                Centered(U.Text(badge.Name, 17, earned ? U.Accent : U.Muted)),
                Centered(U.Text($"{badge.City}\nLeader {(leaders.Length > 0 ? leaders : badge.Leaders[0])} · {badge.Type}", 13, U.Muted)),
                Centered(U.Text(earned ? $"Earned {when:d MMM yyyy}" : "Not earned yet", 13, earned ? U.Good : U.Muted)));
            card.CustomMinimumSize = new Vector2(250, 0);
            grid.AddChild(card);
        }
        _body.AddChild(grid);
        var next = Badges.Unova.FirstOrDefault(b => !progress.Badges.ContainsKey(b.Index));
        return next is null
            ? "All eight badges! The Pokémon League awaits."
            : $"Next: the {next.Name} in {next.City}. Badges unlock field moves and, with all 8, the other regions.";
    }

    static Label Centered(Label label)
    {
        label.HorizontalAlignment = HorizontalAlignment.Center;
        return label;
    }

    // ── Quests ────────────────────────────────────────────────────────────────────────────────────────────────

    string BuildQuests()
    {
        var log = OfflineSession.Quests;
        var progress = OfflineSession.Progress;
        var columns = U.HBox(18);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        _body.AddChild(columns);

        var list = U.VBox(4);
        foreach (var category in Enum.GetValues<QuestCategory>())
        {
            var quests = log.Active.Where(q => q.Info.Category == category).ToList();
            if (quests.Count == 0)
                continue;
            list.AddChild(U.Text(category == QuestCategory.Daily ? "Daily" : category.ToString(), 16, U.Accent));
            foreach (var quest in quests)
            {
                var id = quest.Info.Id;
                var tracked = log.Tracked == quest ? "◆ " : "";
                var button = Row($"{tracked}{quest.Info.Title}", () =>
                {
                    _questId = id;
                    Rebuild(null);
                }, size: 16);
                if (id == (_questId ?? log.Tracked?.Info.Id))
                    button.AddThemeColorOverride("font_color", U.Accent);
                list.AddChild(button);
            }
        }
        var completed = log.Completed.ToList();
        if (completed.Count > 0)
        {
            list.AddChild(U.Text($"Completed ({completed.Count})", 16, U.Good));
            foreach (var (quest, _) in completed.Take(8))
                list.AddChild(U.Text($"   ✓ {quest.Title}", 14, U.Muted));
        }
        var scroll = WrapScroll(list);
        scroll.CustomMinimumSize = new Vector2(360, 0);
        columns.AddChild(scroll);

        var selected = log.Active.FirstOrDefault(q => q.Info.Id == _questId) ?? log.Tracked;
        var detail = U.VBox(8);
        detail.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        columns.AddChild(detail);
        if (selected is null)
        {
            detail.AddChild(U.Text("No active quests.", 18, U.Muted));
            return "Quests come from the story, the Quest Board and daily tasks.";
        }
        var info = selected.Info;
        detail.AddChild(U.Text(info.Title, 24, U.Accent));
        detail.AddChild(U.Text($"{info.Category} quest · started {selected.Started:t}", 14, U.Muted));
        detail.AddChild(U.Text(info.Description, 17, wrap: true));
        detail.AddChild(U.Text("Objectives", 17, U.Accent));
        for (var i = 0; i < info.Objectives.Count; i++)
        {
            var o = info.Objectives[i];
            var n = selected.Progress(i, progress);
            var bar = new ProgressBar { MaxValue = o.Count, Value = n, ShowPercentage = false, CustomMinimumSize = new Vector2(220, 14) };
            bar.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            detail.AddChild(U.HBox(12, U.Text($"{(n >= o.Count ? "✓" : "○")} {o.Text}", 16), U.Spacer(), U.Text($"{n} / {o.Count}", 15, U.Muted), bar));
        }
        var rewards = new List<string>();
        if (info.Reward.Money > 0)
            rewards.Add($"₽{info.Reward.Money:N0}");
        rewards.AddRange(info.Reward.ItemList.Select(r => $"{OfflineSession.Data.ItemName(r.Item)} ×{r.Count}"));
        detail.AddChild(U.Text("Reward (sent by mail): " + (rewards.Count > 0 ? string.Join(", ", rewards) : "—"), 16, U.Good, wrap: true));
        if (info.Next is not null)
            detail.AddChild(U.Text("Leads on to the next story quest.", 14, U.Muted));
        var actions = U.HBox(10);
        var track = U.Button(log.Tracked == selected ? "Tracked on HUD" : "Track on HUD", () =>
        {
            log.TrackedId = info.Id;
            Rebuild($"Tracking \"{info.Title}\".");
        });
        track.Disabled = log.Tracked == selected;
        actions.AddChild(track);
        if (info.Category != QuestCategory.Story)
            actions.AddChild(U.Button("Abandon", () =>
            {
                log.Abandon(info.Id);
                _questId = null;
                Rebuild($"Abandoned \"{info.Title}\".");
            }));
        detail.AddChild(actions);
        return "Pick a quest to see it; the tracked one shows at the top right of the screen.";
    }

    // ── Mail ──────────────────────────────────────────────────────────────────────────────────────────────────

    string BuildMail()
    {
        var mail = OfflineSession.Mail;
        var columns = U.HBox(18);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        _body.AddChild(columns);

        var list = U.VBox(4);
        foreach (var message in mail.Messages)
        {
            var m = message;
            var marks = (m.Read ? "  " : "● ") + (m.HasUnclaimed ? "[+] " : "");
            var button = Row($"{marks}{m.Subject}\n     {m.From} · {When(m.Sent)}", () =>
            {
                _mail = m;
                m.Read = true;
                Rebuild(null);
            }, size: 15);
            if (!m.Read)
                button.AddThemeColorOverride("font_color", U.Accent);
            list.AddChild(button);
        }
        if (mail.Messages.Count == 0)
            list.AddChild(U.Text("Your mailbox is empty.", 16, U.Muted));
        var compose = U.Button("Write a letter (needs the server)", () => { });
        compose.Disabled = true;
        var left = U.VBox(8, U.Text($"Inbox   {mail.Messages.Count} letters · {mail.Unread} unread", 17, U.Accent), WrapScroll(list), compose);
        left.CustomMinimumSize = new Vector2(420, 0);
        columns.AddChild(left);

        var reading = U.VBox(8);
        reading.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        columns.AddChild(reading);
        if (_mail is not { } open || !mail.Messages.Contains(open))
        {
            reading.AddChild(U.Text("Pick a letter to read it.", 18, U.Muted));
            return "Quest rewards, market purchases and sales arrive here. [+] marks letters with something attached.";
        }
        reading.AddChild(U.Text(open.Subject, 22, U.Accent));
        reading.AddChild(U.Text($"From {open.From} · {open.Sent:g}", 14, U.Muted));
        reading.AddChild(U.Text(open.Body, 17, wrap: true));
        if (open.Attachments.Count > 0)
        {
            reading.AddChild(U.Text(open.Claimed ? "Attachments (collected)" : "Attachments", 17, open.Claimed ? U.Muted : U.Good));
            foreach (var a in open.Attachments)
                reading.AddChild(U.Text("   • " + Describe(a), 16, open.Claimed ? U.Muted : null));
        }
        var actions = U.HBox(10);
        if (open.HasUnclaimed)
            actions.AddChild(U.Button("Collect", () => Rebuild(OfflineSession.Claim(open))));
        var delete = U.Button("Delete", () =>
        {
            mail.Delete(open);
            _mail = null;
            Rebuild("Letter deleted.");
        });
        delete.Disabled = open.HasUnclaimed;
        actions.AddChild(delete);
        reading.AddChild(actions);
        return "Collect attachments before deleting a letter.";
    }

    static string Describe(MailAttachment a) => a switch
    {
        { Pokemon: { } mon } => $"{mon.Name} Lv{mon.Level}",
        { Money: > 0 } => $"₽{a.Money:N0}",
        _ => $"{OfflineSession.Data.ItemName(a.Item)} ×{a.Count}",
    };

    // ── Friends ───────────────────────────────────────────────────────────────────────────────────────────────

    string BuildFriends()
    {
        var friends = OfflineSession.Friends;
        var name = new LineEdit { PlaceholderText = "Trainer name", CustomMinimumSize = new Vector2(240, 0), MaxLength = 16 };
        var add = U.Button("Send friend request", () => Rebuild(friends.Request(name.Text, OfflineSession.PlayerName)
            ?? $"Request sent to {name.Text.Trim()}. Offline it stays pending until the server is up."));
        _body.AddChild(U.HBox(10, U.Text($"Friends   {friends.OnlineCount} online", 19, U.Accent), U.Spacer(), name, add));

        var list = U.VBox(6);
        foreach (var friend in friends.Friends)
        {
            var f = friend;
            var (dot, color) = f.State switch
            {
                FriendState.Online => ("●", U.Good),
                FriendState.Away => ("●", U.Warn),
                FriendState.Offline => ("●", U.Muted),
                _ => ("○", U.Accent),
            };
            var where = f.State switch
            {
                FriendState.Online or FriendState.Away => $"{f.Location} · {f.Region} ch.{f.Channel}" + (f.State == FriendState.Away ? " · away" : ""),
                FriendState.Offline => f.LastSeen is { } seen ? $"Offline · last seen {When(seen)}" : "Offline",
                FriendState.RequestSent => "Request sent · waiting",
                _ => "Wants to be your friend",
            };
            var row = U.HBox(10, U.Text(dot, 18, color), U.Text(f.Name, 18), U.Text(where, 15, U.Muted));
            if (f.Note.Length > 0)
                row.AddChild(U.Text($"({f.Note})", 13, U.Muted));
            row.AddChild(U.Spacer());
            if (f.State == FriendState.RequestReceived)
            {
                row.AddChild(U.Button("Accept", () => { friends.Accept(f.Name); Rebuild($"{f.Name} is now your friend."); }));
                row.AddChild(U.Button("Decline", () => { friends.Remove(f.Name); Rebuild($"Declined {f.Name}."); }));
            }
            else
            {
                foreach (var (label, enabled) in new[] { ("Whisper", false), ("Trade", false), ("Battle", false) })
                {
                    var b = U.Button(label, () => { });
                    b.Disabled = !enabled;
                    b.TooltipText = "Needs the server (M4)";
                    row.AddChild(b);
                }
                row.AddChild(U.Button(f.IsFriend ? "Remove" : "Cancel", () => { friends.Remove(f.Name); Rebuild($"Removed {f.Name}."); }));
            }
            row.AddChild(U.Button("Ignore", () => { friends.Ignore(f.Name); Rebuild($"Ignoring {f.Name}."); }));
            list.AddChild(row);
        }
        if (!friends.Friends.Any())
            list.AddChild(U.Text("No friends yet. Send a request by name.", 16, U.Muted));
        if (friends.Ignored.Count > 0)
        {
            list.AddChild(new HSeparator());
            list.AddChild(U.Text("Ignored", 17, U.Accent));
            foreach (var ignored in friends.Ignored.ToList())
            {
                var n = ignored;
                list.AddChild(U.HBox(10, U.Text(n, 16, U.Muted), U.Spacer(), U.Button("Unignore", () => { friends.Unignore(n); Rebuild($"No longer ignoring {n}."); })));
            }
        }
        _body.AddChild(WrapScroll(list));
        return "Sample friends while offline. Whisper, trade and battle invites arrive with the server.";
    }

    // ── Market ────────────────────────────────────────────────────────────────────────────────────────────────

    string BuildMarket()
    {
        var views = U.HBox(6);
        foreach (var (view, title) in new[] { (MarketView.Buy, "Browse"), (MarketView.Mine, "My listings"), (MarketView.Sell, "Sell") })
        {
            var v = view;
            var b = U.Button(title, () =>
            {
                _marketView = v;
                _selling = null;
                Rebuild(null);
            });
            b.ToggleMode = true;
            b.SetPressedNoSignal(_marketView == view);
            views.AddChild(b);
        }
        views.AddChild(U.Spacer());
        views.AddChild(U.Text($"Fee {Market.ListingFeeRate:P0} to list · {Market.SalesTaxRate:P0} sales tax · deliveries by mail", 14, U.Muted));
        _body.AddChild(views);
        return _marketView switch
        {
            MarketView.Mine => BuildMyListings(),
            MarketView.Sell => BuildSell(),
            _ => BuildBrowse(),
        };
    }

    string BuildBrowse()
    {
        var kind = new OptionButton();
        foreach (var k in new[] { "All", "Items", "Pokémon" })
            kind.AddItem(k);
        kind.Selected = _query.Kind switch { ListingKind.Item => 1, ListingKind.Pokemon => 2, _ => 0 };
        kind.ItemSelected += i =>
        {
            _query = _query with { Kind = i switch { 1 => ListingKind.Item, 2 => ListingKind.Pokemon, _ => null } };
            Rebuild(null);
        };
        var sort = new OptionButton();
        foreach (var s in Enum.GetValues<MarketSort>())
            sort.AddItem(s switch { MarketSort.PriceLow => "Price ↑", MarketSort.PriceHigh => "Price ↓", MarketSort.EndingSoon => "Ending soon", _ => "Newest" });
        sort.Selected = (int)_query.Sort;
        sort.ItemSelected += i =>
        {
            _query = _query with { Sort = (MarketSort)(int)i };
            Rebuild(null);
        };
        var search = new LineEdit { Text = _query.Text, PlaceholderText = "Search name…", CustomMinimumSize = new Vector2(220, 0) };
        search.TextSubmitted += text =>
        {
            _query = _query with { Text = text.Trim() };
            Rebuild(null);
        };
        _body.AddChild(U.HBox(10, U.Text("Show", 15, U.Muted), kind, U.Text("Sort", 15, U.Muted), sort, search,
            U.Button("Search", () => { _query = _query with { Text = search.Text.Trim() }; Rebuild(null); })));

        var now = DateTime.Now;
        var results = OfflineSession.Market.Search(_query, OfflineSession.ListingName, now).ToList();
        var list = U.VBox(4);
        foreach (var listing in results)
        {
            var l = listing;
            var mine = l.Seller == OfflineSession.PlayerName;
            var detail = l.Pokemon is { } mon ? $"   {mon.Nature}{(mon.Gender == Gender.Genderless ? "" : " ·" + GenderMark(mon))} · IVs {string.Join("/", mon.Ivs)}" : "";
            var row = U.HBox(10);
            if (l.Pokemon is { } m)
                row.AddChild(new TextureRect { Texture = Icon(m), CustomMinimumSize = new Vector2(48, 48), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered });
            row.AddChild(U.Text($"{OfflineSession.ListingName(l)}{detail}\n{l.Seller} · ends in {Left(l.Expires - now)}", 15));
            row.AddChild(U.Spacer());
            row.AddChild(U.Text($"₽{l.Price:N0}", 18, OfflineSession.Money >= l.Price ? U.Accent : U.Bad));
            var buy = U.Button(mine ? "Yours" : "Buy", () => Rebuild(OfflineSession.Buy(l)));
            buy.Disabled = mine || OfflineSession.Money < l.Price;
            row.AddChild(buy);
            list.AddChild(row);
        }
        if (results.Count == 0)
            list.AddChild(U.Text("No listings match.", 16, U.Muted));
        _body.AddChild(WrapScroll(list));
        return $"{results.Count} listings from sample sellers. Price history and IV/EV filters come with the server market.";
    }

    string BuildMyListings()
    {
        var now = DateTime.Now;
        var mine = OfflineSession.Market.Listings.Where(l => l.Seller == OfflineSession.PlayerName).OrderBy(l => l.Expires).ToList();
        var list = U.VBox(6);
        foreach (var listing in mine)
        {
            var l = listing;
            list.AddChild(U.HBox(10,
                U.Text($"{OfflineSession.ListingName(l)}\nListed {When(l.Listed)} · ends in {Left(l.Expires - now)} · you'd receive ₽{Market.Proceeds(l.Price):N0}", 15),
                U.Spacer(), U.Text($"₽{l.Price:N0}", 18, U.Accent), U.Button("Cancel", () => Rebuild(OfflineSession.CancelListing(l)))));
        }
        if (mine.Count == 0)
            list.AddChild(U.Text("You have nothing for sale.", 16, U.Muted));
        _body.AddChild(WrapScroll(list));
        return $"{mine.Count} / {Market.MaxListingsPerSeller} listings. Offline, a sample buyer takes fairly priced listings after a couple of minutes.";
    }

    string BuildSell()
    {
        var columns = U.HBox(18);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        _body.AddChild(columns);
        var source = U.VBox(4, U.Text("From your Bag", 16, U.Accent));
        foreach (var (item, held) in OfflineSession.Bag.Items)
        {
            var id = item;
            source.AddChild(Row($"{OfflineSession.Data.ItemName(item)}   ×{held}", () =>
            {
                _selling = (ListingKind.Item, id, null);
                Rebuild(null);
            }, size: 15));
        }
        source.AddChild(U.Text("From your party", 16, U.Accent));
        foreach (var partyMon in OfflineSession.Party)
        {
            var m = partyMon;
            var b = Row($"{m.Name} Lv{m.Level}", () =>
            {
                _selling = (ListingKind.Pokemon, 0, m);
                Rebuild(null);
            }, Icon(m), 15);
            b.Disabled = OfflineSession.Party.Count <= 1;
            source.AddChild(b);
        }
        var scroll = WrapScroll(source);
        scroll.CustomMinimumSize = new Vector2(360, 0);
        columns.AddChild(scroll);

        var form = U.VBox(10);
        form.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        columns.AddChild(form);
        if (_selling is not { } sell)
        {
            form.AddChild(U.Text("Pick something to sell.", 18, U.Muted));
            return "Listing takes the item or Pokémon out of your Bag or party until it sells, is cancelled or expires.";
        }
        var have = sell.Kind == ListingKind.Item ? OfflineSession.Bag.Count(sell.Item) : 1;
        var count = new SpinBox { MinValue = 1, MaxValue = Math.Max(1, have), Value = Math.Max(1, have), Editable = sell.Kind == ListingKind.Item };
        var price = new SpinBox { MinValue = 1, MaxValue = Market.MaxPrice, Step = 10, Prefix = "₽" };
        var days = new SpinBox { MinValue = Market.MinDays, MaxValue = Market.MaxDays, Value = 3, Suffix = "days" };
        var summary = U.Text("", 15, U.Muted, wrap: true);
        void Suggest() => price.Value = OfflineSession.SuggestedPrice(sell.Kind, sell.Item, (int)count.Value, sell.Mon);
        void Update() => summary.Text = $"Listing fee ₽{Market.ListingFee((int)price.Value):N0} now · you receive ₽{Market.Proceeds((int)price.Value):N0} after tax"
                                        + $" · suggested ₽{OfflineSession.SuggestedPrice(sell.Kind, sell.Item, (int)count.Value, sell.Mon):N0}";
        Suggest();
        Update();
        count.ValueChanged += _ => { Suggest(); Update(); };
        price.ValueChanged += _ => Update();
        var name = sell.Mon is { } mon ? $"{mon.Name} Lv{mon.Level}" : OfflineSession.Data.ItemName(sell.Item);
        form.AddChild(U.Text($"Sell {name}", 22, U.Accent));
        form.AddChild(U.HBox(10, U.Text("Quantity", 16), count));
        form.AddChild(U.HBox(10, U.Text("Price", 16), price));
        form.AddChild(U.HBox(10, U.Text("Duration", 16), days));
        form.AddChild(summary);
        form.AddChild(U.Button("List it", () =>
        {
            var message = sell.Mon is { } m
                ? OfflineSession.SellPokemon(m, (int)price.Value, (int)days.Value)
                : OfflineSession.SellItem(sell.Item, (int)count.Value, (int)price.Value, (int)days.Value);
            _selling = null;
            Rebuild(message);
        }));
        return "The listing fee isn't refunded. Unsold goods come back by mail when the listing ends.";
    }

    // ── Trainer card ──────────────────────────────────────────────────────────────────────────────────────────

    string BuildCard()
    {
        var p = OfflineSession.Progress;
        var played = OfflineSession.PlayTime;
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", Box(new Color(0.16f, 0.26f, 0.42f), new Color(0.55f, 0.75f, 1f), 3, 14, 22));
        var info = U.VBox(6,
            U.Text(OfflineSession.PlayerName, 28, U.Accent),
            U.Text("Unova · Offline Test World", 15, U.Muted),
            U.Text($"Money          ₽{p.Money:N0}", 18),
            U.Text($"Badges         {p.Badges.Count} / 8", 18),
            U.Text($"Pokémon caught {p.Caught}", 18),
            U.Text($"Trainers beaten {p.DefeatedTrainers.Count} · wild wins {p.WildWins}", 18),
            U.Text($"Quests done    {OfflineSession.Quests.Completed.Count()}", 18),
            U.Text($"Play time      {(int)played.TotalHours}:{played.Minutes:00}   ·   started {p.Started:d}", 18));
        var badges = U.HBox(6);
        foreach (var badge in Badges.Unova)
            badges.AddChild(new BadgeIcon(badge, p.Badges.ContainsKey(badge.Index)) { CustomMinimumSize = new Vector2(40, 40) });
        var team = U.HBox(6);
        foreach (var mon in OfflineSession.Party)
            team.AddChild(new TextureRect { Texture = Icon(mon), CustomMinimumSize = new Vector2(72, 72), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered });
        card.AddChild(U.VBox(14, info, badges, team));
        card.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        _body.AddChild(card);

        var saved = OfflineSession.LastSaved is { } when ? $"Last saved {When(when)} ({when:g})" : "Not saved yet";
        var startOver = U.Button(_confirmStartOver ? "Really start over? Everything is lost" : "Start over…", () =>
        {
            if (!_confirmStartOver)
            {
                _confirmStartOver = true;
                Rebuild("Press again to delete your save and start a new game. Esc to keep playing.");
                return;
            }
            _confirmStartOver = false;
            OfflineSession.StartOver();
            EmitSignal(SignalName.StartedOver);
            Rebuild("A new game begins. Your starter is waiting at home.");
        });
        if (_confirmStartOver)
            startOver.AddThemeColorOverride("font_color", U.Bad);
        var actions = U.HBox(12, U.Text(saved, 15, U.Muted), U.Spacer(),
            U.Button("Save now", () => Rebuild(OfflineSession.Save() ? "Game saved." : "Couldn't save — see the log.")), startOver);
        _body.AddChild(actions);
        return "Your trainer card. Online, other players see it when they inspect you (PvP rating, guild and showcase come later).";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    Control Scroll(VBoxContainer content)
    {
        _body.AddChild(WrapScroll(content));
        return content;
    }

    static ScrollContainer WrapScroll(Control content)
    {
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        scroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(content);
        return scroll;
    }

    static Button Row(string text, Action pressed, Texture2D? icon = null, int size = 17)
    {
        var button = U.Button(text, pressed, size);
        button.Alignment = HorizontalAlignment.Left;
        if (icon is not null)
        {
            button.Icon = icon;
            button.ExpandIcon = false;
            button.AddThemeConstantOverride("icon_max_width", 56);
        }
        return button;
    }

    static StyleBoxFlat Box(Color bg, Color border, int borderWidth, int radius, int margin) => new()
    {
        BgColor = bg, BorderColor = border,
        BorderWidthLeft = borderWidth, BorderWidthTop = borderWidth, BorderWidthRight = borderWidth, BorderWidthBottom = borderWidth,
        CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
        ContentMarginLeft = margin, ContentMarginRight = margin, ContentMarginTop = margin - 2, ContentMarginBottom = margin - 2,
    };

    static string GenderMark(Pokemon mon) => mon.Gender switch { Gender.Male => " ♂", Gender.Female => " ♀", _ => "" };

    static string When(DateTime t)
    {
        var ago = DateTime.Now - t;
        return ago.TotalMinutes < 1 ? "just now" : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes} min ago"
            : ago.TotalDays < 1 ? $"{(int)ago.TotalHours} h ago" : $"{(int)ago.TotalDays} d ago";
    }

    static string Left(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h" : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(0, span.Minutes)}m";

    Texture2D? Icon(Pokemon mon)
    {
        var path = _assets.BattleSpritePath(mon.Species.Id, mon.Shiny ? "front-shiny" : "front");
        if (Icons.TryGetValue(path, out var cached))
            return cached;
        Texture2D? texture = null;
        if (File.Exists(path) && Image.LoadFromFile(path) is { } image)
        {
            image.Resize(56, 56, Image.Interpolation.Nearest);
            texture = ImageTexture.CreateFromImage(image);
        }
        return Icons[path] = texture;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } key && GetViewport().GuiGetFocusOwner() is not LineEdit)
        {
            var step = key.Keycode switch { Key.Q or Key.Pageup => -1, Key.E or Key.Pagedown => 1, _ => 0 };
            if (step != 0)
            {
                var tabs = Tabs;
                Show(tabs[(Array.IndexOf(tabs, _tab) + step + tabs.Length) % tabs.Length]);
                GetViewport().SetInputAsHandled();
                return;
            }
        }
        var cancel = e.IsActionPressed("ui_cancel");
        var close = cancel || (e is InputEventKey { Pressed: true, Keycode: Key.X, Echo: false } && GetViewport().GuiGetFocusOwner() is not LineEdit);
        if (!close)
            return;
        GetViewport().SetInputAsHandled();
        if (cancel && (_useItem != 0 || _selling is not null || _mail is not null || _confirmStartOver || _pcPick >= 0))
        {
            _confirmStartOver = false;
            _pcPick = -1;
            _confirmRelease = false;
            _useItem = 0;
            _selling = null;
            _mail = null;
            Rebuild(null);
            return;
        }
        EmitSignal(SignalName.Closed);
        QueueFree();
    }
}

/// <summary>A gym badge drawn in its colour (a placeholder until badge art is extracted); grey when not earned.</summary>
public partial class BadgeIcon : Control
{
    readonly BadgeInfo? _badge;
    readonly bool _earned;

    public BadgeIcon() { }

    public BadgeIcon(BadgeInfo badge, bool earned)
    {
        _badge = badge;
        _earned = earned;
    }

    public override void _Draw()
    {
        if (_badge is null)
            return;
        var c = Size / 2;
        var r = Mathf.Min(Size.X, Size.Y) / 2 - 2;
        var color = new Color((_badge.Color >> 16 & 0xFF) / 255f, (_badge.Color >> 8 & 0xFF) / 255f, (_badge.Color & 0xFF) / 255f);
        var fill = _earned ? color : new Color(0.22f, 0.24f, 0.28f);
        var edge = _earned ? color.Lightened(0.5f) : new Color(0.4f, 0.42f, 0.46f);
        // Each badge gets its own outline: 3–8 points, alternating inner radius for a star-like shape.
        var points = 3 + _badge.Index % 6;
        var star = _badge.Index % 2 == 1;
        var poly = new List<Vector2>();
        for (var i = 0; i < points * (star ? 2 : 1); i++)
        {
            var angle = -Mathf.Pi / 2 + i * Mathf.Tau / (points * (star ? 2 : 1));
            var radius = star && i % 2 == 1 ? r * 0.55f : r;
            poly.Add(c + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
        }
        DrawColoredPolygon(poly.ToArray(), fill);
        poly.Add(poly[0]);
        DrawPolyline(poly.ToArray(), edge, 2, true);
        DrawCircle(c, r * 0.22f, _earned ? Colors.White.Lerp(color, 0.3f) : new Color(0.32f, 0.34f, 0.38f));
        if (_earned)
            DrawCircle(c + new Vector2(-r * 0.3f, -r * 0.3f), r * 0.12f, new Color(1, 1, 1, 0.6f));
    }
}
