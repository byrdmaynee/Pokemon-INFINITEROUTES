using Godot;
using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// The admin window (F9 in Admin Mode; BUILD_SPEC §11.3), a front end for the server's admin actions — the server
/// checks every one again, and shows only what the role allows here:
/// <list type="bullet">
/// <item>Players: who's online, or a search over every character (online or not); Open shows one in the editor.</item>
/// <item>Player: the account (role, ban, mute, kick), where they are (go to, summon, spectate), and their game — money,
/// the Bag (give and take items), party and PC (edit a Pokémon's level, nature, IVs, EVs, moves, ability, item,
/// nickname and shininess; give, heal, release). A reason is needed for every change and goes in the audit log.
/// Pokémon made or changed by staff are marked ★ (staff only).</item>
/// <item>Roles: every role's rank and permissions; Owners tick permissions, move ranks, make and delete roles.</item>
/// <item>Audit: the latest entries; player changes can be reverted (with a warning if the game changed since).</item>
/// </list>
/// </summary>
public sealed partial class AdminWindow : PanelContainer
{
    readonly Action<Message> _send;
    readonly Func<AdminStatus?> _status;
    TabContainer _tabs = null!;
    Label _statusLine = null!;
    LineEdit _reason = null!;

    VBoxContainer _playerRows = null!;
    HBoxContainer _toolbar = null!;
    LineEdit _search = null!;
    VBoxContainer _detailBox = null!;
    VBoxContainer _roleList = null!, _roleDetail = null!;
    VBoxContainer _auditRows = null!;

    AdminPlayerDetail? _detail;
    AdminRoles? _roles;
    string? _selectedRole;
    long _pendingForce;

    /// <summary>What the window shows now; for smoke tests.</summary>
    public AdminPlayerDetail? Detail => _detail;
    public AdminRoles? RolesShown => _roles;
    public string StatusText => _statusLine.Text;
    public int CurrentTab => _tabs.CurrentTab;

    public AdminWindow() : this(_ => { }, () => null) { }

    public AdminWindow(Action<Message> send, Func<AdminStatus?> status)
    {
        _send = send;
        _status = status;
    }

    static BattleData? Data => OfflineSession.Data;

    bool Can(string permission) => _status() is { Elevated: true } s && s.Permissions.Contains(permission);

    bool IsOwner => _roles?.Roles.FirstOrDefault(r => r.Name == _status()?.Role) is { Rank: >= 100 };

    void Act(string verb, string target, params (string Key, string Value)[] args) =>
        _send(new AdminAction(verb, target, args.ToDictionary(a => a.Key, a => a.Value), _reason.Text.Trim()));

    void Command(string text) => _send(new AdminCommand(text));

    public override void _Ready()
    {
        AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.13f, 0.97f), BorderColor = new Color(0.98f, 0.6f, 0.35f), BorderWidthLeft = 3, BorderWidthTop = 3,
            BorderWidthRight = 3, BorderWidthBottom = 3, ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 10, ContentMarginBottom = 10,
        });
        CustomMinimumSize = new Vector2(1120, 640);
        _statusLine = Ui.Ui.Text("", 15, Ui.Ui.Muted, wrap: true);
        _statusLine.CustomMinimumSize = new Vector2(700, 0);
        _reason = new LineEdit { PlaceholderText = "Reason (required for changes — it goes in the audit log)", CustomMinimumSize = new Vector2(520, 0) };
        var header = Ui.Ui.HBox(12, Ui.Ui.Text($"Admin — {_status()?.Role}", 20, Ui.Ui.Accent), _reason, Ui.Ui.Spacer(), Ui.Ui.Button("Close (F9)", () => QueueFree()));

        _tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 520) };
        _tabs.AddChild(PlayersTab());
        _tabs.AddChild(PlayerTab());
        _tabs.AddChild(RolesTab());
        _tabs.AddChild(AuditTab());
        _tabs.TabChanged += tab =>
        {
            if (tab == 2)
                Act("roles", "");
            else if (tab == 3 && Can("audit.view"))
                Act("audit", "", ("count", "80"));
        };
        AddChild(Ui.Ui.VBox(8, header, _tabs, _statusLine));
        Command("players");
        Act("roles", "");
    }

    static ScrollContainer Scroll(Control inside)
    {
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        inside.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(inside);
        return scroll;
    }

    static Button Small(string text, Action action, bool enabled = true)
    {
        var b = Ui.Ui.Button(text, action, 13);
        b.CustomMinimumSize = new Vector2(0, 26);
        b.Disabled = !enabled;
        return b;
    }

    // ---- Players --------------------------------------------------------------------------------------------------

    Control PlayersTab()
    {
        _search = new LineEdit { PlaceholderText = "Character or account name (any part)", CustomMinimumSize = new Vector2(360, 0) };
        _search.TextSubmitted += _ => Search();
        _playerRows = Ui.Ui.VBox(4);
        var status = _status();
        var announce = new LineEdit { PlaceholderText = "Announcement to everyone", CustomMinimumSize = new Vector2(380, 0) };
        _toolbar = Ui.Ui.HBox(8,
            Small(status?.Invisible == true ? "Invisible: on" : "Invisible: off", () => Command($"invisible {(_status()?.Invisible == true ? "off" : "on")}"), Can("admin.invisible")),
            Small(status?.Noclip == true ? "Noclip: on" : "Noclip: off", () => Command($"noclip {(_status()?.Noclip == true ? "off" : "on")}"), Can("admin.noclip")),
            announce,
            Small("Announce", () =>
            {
                if (announce.Text.Trim().Length > 0)
                    Command($"announce {announce.Text.Trim()}");
                announce.Text = "";
            }, Can("server.announce")),
            Ui.Ui.Spacer(),
            Small("Leave Admin Mode", () => Command("exit")));
        var tab = Ui.Ui.VBox(8,
            _toolbar,
            Ui.Ui.HBox(8, _search, Ui.Ui.Button("Search everyone", Search), Ui.Ui.Button("Online now", () => Command("players"))),
            Scroll(_playerRows));
        tab.Name = "Players";
        return tab;
    }

    void Search()
    {
        if (_search.Text.Trim().Length > 0)
            Act("search", _search.Text.Trim());
    }

    public void SetRows(AdminPlayers players)
    {
        Ui.Ui.ClearChildren(_playerRows);
        if (players.Players.Count == 0)
            _playerRows.AddChild(Ui.Ui.Text("Nobody found.", 15, Ui.Ui.Muted));
        foreach (var row in players.Players)
        {
            var name = row.Name;
            var where = row.Online ? $"channel {row.Channel}, zone {row.Location.Zone} at {row.Location.X},{row.Location.Y}" : $"offline (zone {row.Location.Zone})";
            var flags = (row.Role != "Player" ? $" · {row.Role}" : "") + (row.Elevated ? " · admin" : "") + (row.Invisible ? " · invisible" : "")
                + (row.MutedUntil is { } m ? $" · muted to {m}" : "");
            var line = Ui.Ui.HBox(6, Ui.Ui.Text($"{(row.Online ? "●" : "○")} {row.Name} ({row.Account}) — {where}{flags}", 14));
            line.GetChild<Label>(0).SizeFlagsHorizontal = SizeFlags.ExpandFill;
            line.AddChild(Small("Open", () => Open(name), Can("player.view")));
            if (row.Online)
            {
                line.AddChild(Small("Go to", () => Command($"tp {name}"), Can("world.teleport")));
                line.AddChild(Small("Summon", () => Command($"summon {name}"), Can("player.summon")));
                line.AddChild(Small("Spectate", () => Command($"spectate {name}"), Can("admin.spectate")));
            }
            _playerRows.AddChild(line);
        }
    }

    /// <summary>Opens a character in the Player tab.</summary>
    public void Open(string name)
    {
        Act("detail", name);
        _tabs.CurrentTab = 1;
    }

    // ---- Player ----------------------------------------------------------------------------------------------------

    Control PlayerTab()
    {
        _detailBox = Ui.Ui.VBox(8, Ui.Ui.Text("Open a player from the Players tab (or right-click one in the world).", 15, Ui.Ui.Muted));
        var tab = Scroll(_detailBox);
        tab.Name = "Player";
        return tab;
    }

    public void ShowDetail(AdminPlayerDetail detail)
    {
        _detail = detail;
        var name = detail.Character;
        Ui.Ui.ClearChildren(_detailBox);
        var status = detail.Online ? "online" : $"offline (last seen {detail.LastSeen})";
        _detailBox.AddChild(Ui.Ui.Text($"{name} — account {detail.Account} · {detail.Role} · {status}", 19, Ui.Ui.Accent));
        _detailBox.AddChild(Ui.Ui.Text($"Zone {detail.Location.Zone} at {detail.Location.X},{detail.Location.Y}"
            + (detail.MutedUntil is { } m ? $" · muted until {m}" : "") + (detail.BannedUntil is { } b ? $" · BANNED {b}: {detail.BanReason}" : ""), 15));

        // The account: role, ban, mute, kick; where they are.
        var roles = new OptionButton();
        foreach (var r in _roles?.Roles ?? [])
        {
            roles.AddItem(r.Name);
            if (r.Name == detail.Role)
                roles.Selected = roles.ItemCount - 1;
        }
        var banLength = new LineEdit { Text = "1440", CustomMinimumSize = new Vector2(90, 0), TooltipText = "Minutes, or perm" };
        var account = Ui.Ui.HBox(6,
            Ui.Ui.Text("Role", 14, Ui.Ui.Muted), roles,
            Small("Set role", () => Act("set-role", name, ("role", roles.GetItemText(roles.Selected))), Can("roles.grant") && roles.ItemCount > 0),
            Ui.Ui.Text("   Ban minutes (or perm)", 14, Ui.Ui.Muted), banLength,
            Small("Ban", () => Act("ban", name, ("minutes", banLength.Text.Trim())), Can("player.ban")),
            Small("Unban", () => Act("unban", name), Can("player.ban") && detail.BannedUntil is not null));
        _detailBox.AddChild(account);
        var reason = () => _reason.Text.Trim();
        var moderation = Ui.Ui.HBox(6,
            Small("Mute 10 min", () => Command($"mute {name} 10 {reason()}"), Can("chat.mute") && detail.Online),
            Small("Unmute", () => Command($"unmute {name}"), Can("chat.mute") && detail.Online),
            Small("Kick", () => Command($"kick {name} {reason()}"), Can("player.kick") && detail.Online),
            Small("Go to", () => Command($"tp {name}"), Can("world.teleport") && detail.Online),
            Small("Summon", () => Command($"summon {name}"), Can("player.summon") && detail.Online),
            Small("Spectate", () => Command($"spectate {name}"), Can("admin.spectate") && detail.Online),
            Small("Refresh", () => Act("detail", name)));
        _detailBox.AddChild(moderation);

        if (detail.StateJson is null || SaveGame.FromJson(detail.StateJson) is not { } state)
        {
            _detailBox.AddChild(Ui.Ui.Text("This server keeps no game for this character.", 15, Ui.Ui.Muted));
            return;
        }
        var data = Data;
        var inventory = Can("player.inventory.write");
        var pokemon = Can("pokemon.edit");

        // Money.
        var money = new SpinBox { MaxValue = 9_999_999, Value = state.Progress.Money, CustomMinimumSize = new Vector2(140, 0) };
        _detailBox.AddChild(Ui.Ui.HBox(6, Ui.Ui.Text($"Money ₽{state.Progress.Money:N0}", 16), money,
            Small("Set money", () => Act("money", name, ("amount", ((int)money.Value).ToString())), inventory),
            Ui.Ui.Text($"   Badges {state.Progress.Badges.Count} · caught {state.Progress.Caught} · play time {state.Progress.PlayTime:h\\:mm}", 14, Ui.Ui.Muted)));

        // The Bag.
        _detailBox.AddChild(Ui.Ui.Text("Bag", 16, Ui.Ui.Muted));
        var bag = new HFlowContainer();
        foreach (var item in state.Bag)
        {
            var id = item.Item;
            bag.AddChild(Ui.Ui.HBox(2, Ui.Ui.Text($"{ItemName(id)} ×{item.Count}", 14), Small("−1", () => Act("take-item", name, ("item", id.ToString()), ("count", "1")), inventory)));
        }
        _detailBox.AddChild(bag);
        var itemPick = new SpinBox { MinValue = 1, MaxValue = 700, Value = 17, CustomMinimumSize = new Vector2(100, 0) };
        var itemName = Ui.Ui.Text(ItemName(17), 14);
        itemPick.ValueChanged += v => itemName.Text = ItemName((int)v);
        var itemCount = new SpinBox { MinValue = 1, MaxValue = 999, Value = 1, CustomMinimumSize = new Vector2(80, 0) };
        _detailBox.AddChild(Ui.Ui.HBox(6, Ui.Ui.Text("Give item #", 14, Ui.Ui.Muted), itemPick, itemName, Ui.Ui.Text("×", 14), itemCount,
            Small("Give", () => Act("give-item", name, ("item", ((int)itemPick.Value).ToString()), ("count", ((int)itemCount.Value).ToString())), inventory)));

        // Party and PC.
        _detailBox.AddChild(Ui.Ui.HBox(8, Ui.Ui.Text("Party", 16, Ui.Ui.Muted), Small("Heal party", () => Act("heal", name), pokemon)));
        for (var i = 0; i < state.Party.Count; i++)
            _detailBox.AddChild(PokemonRow(name, state.Party[i], "party", i, pokemon));
        if (state.Box.Count > 0)
        {
            _detailBox.AddChild(Ui.Ui.Text($"PC ({state.Box.Count})", 16, Ui.Ui.Muted));
            for (var i = 0; i < state.Box.Count; i++)
                _detailBox.AddChild(PokemonRow(name, state.Box[i], "box", i, pokemon));
        }
        var species = new SpinBox { MinValue = 1, MaxValue = 649, Value = 495, CustomMinimumSize = new Vector2(100, 0) };
        var speciesName = Ui.Ui.Text(SpeciesName(495), 14);
        species.ValueChanged += v => speciesName.Text = SpeciesName((int)v);
        var level = new SpinBox { MinValue = 1, MaxValue = 100, Value = 5, CustomMinimumSize = new Vector2(80, 0) };
        _detailBox.AddChild(Ui.Ui.HBox(6, Ui.Ui.Text("Give Pokémon #", 14, Ui.Ui.Muted), species, speciesName, Ui.Ui.Text("Lv", 14), level,
            Small("Give", () => Act("pokemon-give", name, ("species", ((int)species.Value).ToString()), ("level", ((int)level.Value).ToString())), pokemon)));
    }

    static string ItemName(int id) => Data?.ItemName(id) ?? $"Item {id}";

    static string SpeciesName(int id) => Data?.HasSpecies(id) == true ? Data.Species(id).Name : "?";

    static string MoveName(int id) => Data?.Move(id)?.Name ?? $"#{id}";

    Control PokemonRow(string character, PokemonSave p, string where, int index, bool canEdit)
    {
        var name = p.Nickname ?? SpeciesName(p.Species);
        var moves = string.Join(", ", p.Moves.Select(m => MoveName(m.Move)));
        var row = Ui.Ui.HBox(6, Ui.Ui.Text($"{(p.AdminOrigin ? "★ " : "")}{name} ({SpeciesName(p.Species)}) Lv{p.Level}{(p.Shiny ? " ✦shiny" : "")} · HP {p.Hp} · {p.Nature} · {moves}", 14));
        row.GetChild<Label>(0).SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(Small("Edit", () => EditPokemon(character, p, where, index), canEdit));
        row.AddChild(Small("Release", () => Act("pokemon-release", character, ("where", where), ("index", index.ToString())), canEdit));
        return row;
    }

    /// <summary>The Pokémon editor: everything about one Pokémon; Save sends it to the server, which checks legality.</summary>
    void EditPokemon(string character, PokemonSave p, string where, int index)
    {
        var data = Data;
        var dialog = new PanelContainer();
        dialog.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color(0.1f, 0.11f, 0.16f, 0.99f), BorderColor = Ui.Ui.Accent,
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2, ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 10, ContentMarginBottom = 10 });
        var level = new SpinBox { MinValue = 1, MaxValue = 100, Value = p.Level };
        var nature = new OptionButton();
        foreach (var n in Enum.GetValues<Nature>())
            nature.AddItem(n.ToString());
        nature.Selected = (int)p.Nature;
        var shiny = new CheckBox { Text = "Shiny", ButtonPressed = p.Shiny };
        var nickname = new LineEdit { Text = p.Nickname ?? "", PlaceholderText = "(no nickname)", MaxLength = Pokemon.MaxNicknameLength };
        var ability = new OptionButton();
        var abilities = data?.HasSpecies(p.Species) == true ? data.Species(p.Species).Abilities : [];
        for (var i = 0; i < 3; i++)
            ability.AddItem(i == 2 ? $"Hidden: {(data?.HasSpecies(p.Species) == true ? data.Species(p.Species).HiddenAbility ?? "—" : "—")}" : i < abilities.Count ? abilities[i] : "—");
        ability.Selected = Math.Clamp(p.AbilitySlot, 0, 2);
        var held = new SpinBox { MinValue = 0, MaxValue = 700, Value = p.HeldItem };
        var heldName = Ui.Ui.Text(p.HeldItem > 0 ? ItemName(p.HeldItem) : "nothing", 14);
        held.ValueChanged += v => heldName.Text = v > 0 ? ItemName((int)v) : "nothing";
        string[] stats = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];
        var ivs = stats.Select((_, i) => new SpinBox { MinValue = 0, MaxValue = 31, Value = p.Ivs.ElementAtOrDefault(i) }).ToArray();
        var evs = stats.Select((_, i) => new SpinBox { MinValue = 0, MaxValue = 255, Value = p.Evs.ElementAtOrDefault(i) }).ToArray();
        // Moves: what the species learns by level-up (and whatever it knows now), or none.
        var choices = new List<int> { 0 };
        if (data is not null)
        {
            choices.AddRange(data.Learnset(p.Species).Select(l => l.Move).Distinct());
            choices.AddRange(p.Moves.Select(m => m.Move).Where(m => !choices.Contains(m)));
        }
        var moves = Enumerable.Range(0, 4).Select(i =>
        {
            var o = new OptionButton();
            foreach (var m in choices)
                o.AddItem(m == 0 ? "—" : MoveName(m));
            o.Selected = Math.Max(0, choices.IndexOf(i < p.Moves.Count ? p.Moves[i].Move : 0));
            return o;
        }).ToArray();

        var ivRow = Ui.Ui.HBox(4, Ui.Ui.Text("IVs", 14, Ui.Ui.Muted));
        var evRow = Ui.Ui.HBox(4, Ui.Ui.Text("EVs", 14, Ui.Ui.Muted));
        for (var i = 0; i < 6; i++)
        {
            ivRow.AddChild(Ui.Ui.Text(stats[i], 13));
            ivRow.AddChild(ivs[i]);
            evRow.AddChild(Ui.Ui.Text(stats[i], 13));
            evRow.AddChild(evs[i]);
        }
        void Save()
        {
            var picked = moves.Select(o => choices[o.Selected]).Where(m => m != 0).Distinct().ToList();
            var edited = p with
            {
                Level = (int)level.Value, Nature = (Nature)nature.Selected, Shiny = shiny.ButtonPressed,
                Nickname = nickname.Text.Trim().Length > 0 ? nickname.Text.Trim() : null, AbilitySlot = ability.Selected, HeldItem = (int)held.Value,
                Ivs = [.. ivs.Select(s => (int)s.Value)], Evs = [.. evs.Select(s => (int)s.Value)],
                Moves = [.. picked.Select(m => new MoveSave(m, Data?.Move(m)?.Pp ?? 10))],
            };
            Act("pokemon-save", character, ("where", where), ("index", index.ToString()), ("pokemon", SaveGame.PokemonJson(edited)));
            dialog.QueueFree();
        }
        dialog.AddChild(Ui.Ui.VBox(8,
            Ui.Ui.Text($"Edit {p.Nickname ?? SpeciesName(p.Species)} ({SpeciesName(p.Species)}) — {(where == "box" ? "PC" : "party")} {index + 1}", 18, Ui.Ui.Accent),
            Ui.Ui.HBox(8, Ui.Ui.Text("Level", 14, Ui.Ui.Muted), level, Ui.Ui.Text("Nature", 14, Ui.Ui.Muted), nature, shiny, Ui.Ui.Text("Nickname", 14, Ui.Ui.Muted), nickname),
            Ui.Ui.HBox(8, Ui.Ui.Text("Ability", 14, Ui.Ui.Muted), ability, Ui.Ui.Text("Held item #", 14, Ui.Ui.Muted), held, heldName),
            ivRow, evRow,
            Ui.Ui.HBox(6, [Ui.Ui.Text("Moves", 14, Ui.Ui.Muted), .. moves]),
            Ui.Ui.Text("The server checks it can be obtained normally; saving one that can't needs that permission and a reason.", 13, Ui.Ui.Muted),
            Ui.Ui.HBox(8, Ui.Ui.Button("Save", Save), Ui.Ui.Button("Cancel", () => dialog.QueueFree()))));
        AddChild(dialog);
        dialog.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        dialog.GrowHorizontal = dialog.GrowVertical = GrowDirection.Both;
        EditorOpen = dialog;
    }

    /// <summary>The Pokémon editor while it's open; for smoke tests.</summary>
    public Control? EditorOpen { get; private set; }

    // ---- Smoke-test hooks: drive the window the way a person would ------------------------------------------------

    public void TestReason(string reason) => _reason.Text = reason;

    public void TestTab(int tab) => _tabs.CurrentTab = tab;

    /// <summary>The open editor, or else the current tab.</summary>
    Node TestScope => EditorOpen is { } e && IsInstanceValid(e) ? e : _tabs.GetCurrentTabControl();

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

    /// <summary>Presses the first enabled button whose text starts with <paramref name="text"/> (a check box toggles).</summary>
    public string TestPress(string text)
    {
        var button = All<BaseButton>(TestScope).FirstOrDefault(b => !b.Disabled && b is Button bt && bt.Text.Contains(text, StringComparison.Ordinal));
        if (button is null)
            return $"no enabled button \"{text}\"";
        if (button.ToggleMode)
            button.ButtonPressed = !button.ButtonPressed;
        else
            button.EmitSignal(BaseButton.SignalName.Pressed);
        return $"pressed \"{((Button)button).Text}\"";
    }

    /// <summary>Sets the <paramref name="index"/>th number box (in tree order) of the editor or current tab.</summary>
    public string TestSpin(int index, double value)
    {
        var spins = All<SpinBox>(TestScope).ToList();
        if (index >= spins.Count)
            return $"no number box {index} (of {spins.Count})";
        spins[index].Value = value;
        return $"number box {index} = {value}";
    }

    /// <summary>Sets the <paramref name="index"/>th text box (in tree order) of the editor or current tab.</summary>
    public string TestText(int index, string value)
    {
        var edits = All<LineEdit>(TestScope).Where(e => e.GetParent() is not SpinBox).ToList();
        if (index >= edits.Count)
            return $"no text box {index} (of {edits.Count})";
        edits[index].Text = value;
        return $"text box {index} = {value}";
    }

    /// <summary>Every label in the current tab, joined; for smoke-test logs.</summary>
    public string TestLabels() => string.Join(" / ", All<Label>(TestScope).Select(l => l.Text).Where(t => t.Length > 0));

    // ---- Roles ------------------------------------------------------------------------------------------------------

    Control RolesTab()
    {
        _roleList = Ui.Ui.VBox(4);
        _roleDetail = Ui.Ui.VBox(6);
        var left = Ui.Ui.VBox(6, Ui.Ui.Text("Roles (rank)", 15, Ui.Ui.Muted), _roleList);
        left.CustomMinimumSize = new Vector2(250, 0);
        var tab = Ui.Ui.HBox(16, left, Scroll(_roleDetail));
        tab.Name = "Roles";
        return tab;
    }

    public void SetRoles(AdminRoles roles)
    {
        _roles = roles;
        _selectedRole ??= roles.Roles.FirstOrDefault(r => r.Rank > 0)?.Name;
        Ui.Ui.ClearChildren(_roleList);
        foreach (var r in roles.Roles.OrderByDescending(r => r.Rank))
        {
            var name = r.Name;
            var b = Small($"{r.Name} ({r.Rank}) · {r.Accounts} account(s)", () =>
            {
                _selectedRole = name;
                SetRoles(_roles!);
            });
            b.Alignment = HorizontalAlignment.Left;
            if (r.Name == _selectedRole)
                b.Modulate = Ui.Ui.Accent;
            _roleList.AddChild(b);
        }
        // Making a role (Owners).
        var newName = new LineEdit { PlaceholderText = "New role name", CustomMinimumSize = new Vector2(150, 0) };
        var newRank = new SpinBox { MinValue = 1, MaxValue = 99, Value = 15 };
        var copy = new OptionButton();
        copy.AddItem("(no permissions)");
        foreach (var r in roles.Roles)
            copy.AddItem(r.Name);
        _roleList.AddChild(Ui.Ui.Text("New role", 14, Ui.Ui.Muted));
        _roleList.AddChild(newName);
        _roleList.AddChild(Ui.Ui.HBox(4, Ui.Ui.Text("Rank", 13), newRank));
        _roleList.AddChild(Ui.Ui.HBox(4, Ui.Ui.Text("Copy", 13), copy));
        _roleList.AddChild(Small("Create", () =>
        {
            _selectedRole = newName.Text.Trim();
            Act("role-create", newName.Text.Trim(), ("rank", ((int)newRank.Value).ToString()), ("copy", copy.Selected > 0 ? copy.GetItemText(copy.Selected) : ""));
        }, IsOwner));
        ShowRole();
    }

    void ShowRole()
    {
        Ui.Ui.ClearChildren(_roleDetail);
        if (_roles?.Roles.FirstOrDefault(r => r.Name == _selectedRole) is not { } role)
            return;
        var owner = IsOwner;
        var fixedRank = role.Rank is 0 or >= 100;
        _roleDetail.AddChild(Ui.Ui.Text($"{role.Name} — rank {role.Rank}{(role.Builtin ? " (built in)" : "")}", 18, Ui.Ui.Accent));
        if (!owner)
            _roleDetail.AddChild(Ui.Ui.Text("Only Owners change roles; this is what the role can do.", 14, Ui.Ui.Muted));
        var rank = new SpinBox { MinValue = 1, MaxValue = 99, Value = Math.Clamp(role.Rank, 1, 99) };
        var name = role.Name;
        _roleDetail.AddChild(Ui.Ui.HBox(6, Ui.Ui.Text("Rank", 14, Ui.Ui.Muted), rank,
            Small("Move", () => Act("role-rank", name, ("rank", ((int)rank.Value).ToString())), owner && !fixedRank),
            Small("Delete role", () => Act("role-delete", name), owner && !role.Builtin)));
        foreach (var permission in _roles.Catalog)
        {
            var key = permission.Key;
            var box = new CheckBox
            {
                Text = $"{permission.Description}  ({key})", ButtonPressed = role.Permissions.Contains(key),
                Disabled = !owner || role.Rank >= 100 || role.Rank == 0,
            };
            box.Toggled += on => Act("role-perm", name, ("permission", key), ("on", on ? "yes" : "no"));
            _roleDetail.AddChild(box);
        }
        if (role.Rank >= 100)
            _roleDetail.AddChild(Ui.Ui.Text("Owners always have every permission.", 14, Ui.Ui.Muted));
    }

    // ---- Audit ------------------------------------------------------------------------------------------------------

    Control AuditTab()
    {
        _auditRows = Ui.Ui.VBox(3);
        var tab = Ui.Ui.VBox(8, Ui.Ui.HBox(8, Ui.Ui.Button("Refresh", () => Act("audit", "", ("count", "80")))), Scroll(_auditRows));
        tab.Name = "Audit";
        return tab;
    }

    public void SetAudit(AdminAudit audit)
    {
        Ui.Ui.ClearChildren(_auditRows);
        foreach (var e in audit.Entries)
        {
            var row = Ui.Ui.HBox(6, Ui.Ui.Text($"{e.Time}  {e.Actor} ({e.Role})  {e.Action}  {e.Target}  {e.Details}", 13, wrap: true));
            row.GetChild<Label>(0).SizeFlagsHorizontal = SizeFlags.ExpandFill;
            if (e.Change > 0)
            {
                var change = e.Change;
                row.AddChild(Small(_pendingForce == change ? "Force revert" : "Revert", () =>
                    Act("revert", "", ("change", change.ToString()), ("force", _pendingForce == change ? "yes" : "no")), Can("audit.revert")));
            }
            _auditRows.AddChild(row);
        }
    }

    /// <summary>Admin Mode's toggles changed: the toolbar shows them as they are now.</summary>
    public void StatusChanged()
    {
        if (_status() is not { } status || _toolbar.GetChildCount() < 2)
            return;
        _toolbar.GetChild<Button>(0).Text = status.Invisible ? "Invisible: on" : "Invisible: off";
        _toolbar.GetChild<Button>(1).Text = status.Noclip ? "Noclip: on" : "Noclip: off";
    }

    /// <summary>The server's answer to the last action: shown at the bottom (a revert that needs forcing offers it).</summary>
    public void ShowReply(AdminReply reply)
    {
        _statusLine.Text = string.Join("  ", reply.Lines);
        _statusLine.AddThemeColorOverride("font_color", reply.Ok ? Ui.Ui.Good : Ui.Ui.Bad);
        var conflict = reply.Lines.FirstOrDefault(l => l.Contains("changed since change ")) is { } line
            && long.TryParse(new string(line[(line.IndexOf("change ", StringComparison.Ordinal) + 7)..].TakeWhile(char.IsDigit).ToArray()), out var id) ? id : 0;
        if (conflict > 0)
        {
            _pendingForce = conflict;
            Act("audit", "", ("count", "80"));
        }
        if (reply.Ok && _tabs.CurrentTab == 3)
            Act("audit", "", ("count", "80"));
    }
}
