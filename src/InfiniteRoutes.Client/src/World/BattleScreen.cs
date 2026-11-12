using Godot;
using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// A battle drawn over the overworld: the foe's Pokémon on the far platform, the player's from behind on the near
/// one (two or three a side in double and triple battles, placed where the DS puts them), info boxes with HP (and EXP
/// for the player's), a message box, and the Fight / Bag / Pokémon / Run menu, asked of each of the player's Pokémon
/// in turn (with a target list when a move could hit more than one). Bag throws Poké Balls or uses medicine; Pokémon
/// switches (rotates, in a rotation battle) and picks the next one when one faints. Turns run in
/// <see cref="SingleBattle"/>; this screen plays back its events in order.
/// </summary>
public partial class BattleScreen : Control
{
    const float MessageSeconds = 1.1f;
    const float HpSeconds = 0.55f;

    [Signal]
    public delegate void FinishedEventHandler(int outcome);

    readonly SingleBattle _battle;
    readonly GameAssets _assets;
    readonly IReadOnlyList<string> _typeNames;

    /// <summary>Each side's battle sprites and info boxes by slot: [0] the player's, [1] the foe's.</summary>
    readonly TextureRect[][] _sprites = [[], []];
    readonly InfoBox[][] _boxes = [[], []];
    TextureRect _foeSprite => _sprites[1][0];
    TextureRect _playerSprite => _sprites[0][0];
    InfoBox _foeBox => _boxes[1][0];
    InfoBox _playerBox => _boxes[0][0];
    /// <summary>The player's choices so far this turn (double and triple battles) and whose turn it is to choose.</summary>
    BattleChoice?[] _choices = [];
    int _chooser;
    /// <summary>The trainer's sprite in a trainer battle (null in a wild one).</summary>
    TextureRect? _trainerSprite;
    WeatherLayer _weather = null!;
    Label _message = null!;
    GridContainer _commands = null!, _moves = null!;
    PanelContainer _picker = null!;
    Label _pickerTitle = null!;
    VBoxContainer _pickerList = null!;
    Action? _pickerCancel;
    bool _busy;
    bool _skip;

    public SingleBattle Battle => _battle;

    /// <summary>True while events are playing back (the menu is hidden).</summary>
    public bool Busy => _busy;

    /// <summary>True while the player is choosing a move to forget for a new one. Public so smoke tests can answer.</summary>
    public bool Asking { get; private set; }

    /// <summary>True while the player is choosing a nickname for a Pokémon they just caught.</summary>
    public bool Naming { get; private set; }

    TaskCompletionSource<string?>? _nickname;

    /// <summary>Answers the nickname prompt (null: no nickname); for smoke tests.</summary>
    public void SubmitNickname(string? name) => _nickname?.TrySetResult(name);

    /// <summary>The last ability banner shown ("Sandile's Intimidate"), for smoke tests.</summary>
    public string? LastAbility { get; private set; }

    // Godot needs a parameterless constructor to recreate script instances.
    public BattleScreen() : this(null!, null!) { }

    /// <summary>The server this battle is played on (online), or null offline.</summary>
    IBattleLink? _link;

    /// <summary>Plays the battle on a server: each action is the server's to play first (see <see cref="IBattleLink"/>).</summary>
    public BattleScreen Online(IBattleLink link)
    {
        _link = link;
        return this;
    }

    /// <summary>A battle shown over the ROM's <paramref name="scenery"/> (backdrop and platform model names).</summary>
    public BattleScreen(SingleBattle battle, GameAssets assets, (string Backdrop, string Stage)? scenery = null)
    {
        _battle = battle;
        _assets = assets;
        _typeNames = assets?.LoadText("types") ?? [];
        _scenery = scenery ?? ("batt_bg01", "batt_stage01");
    }

    readonly (string Backdrop, string Stage) _scenery;
    BattleBackdrop _backdrop = null!;
    ColorRect _dim = null!;
    BattleEffects _effects = null!;
    Vector2 _playerFeet, _foeFeet;

    TextureRect SpriteAt(BattleSide side, int slot) => _sprites[(int)side][Math.Clamp(slot, 0, _sprites[(int)side].Length - 1)];
    InfoBox BoxAt(BattleSide side, int slot) => _boxes[(int)side][Math.Clamp(slot, 0, _boxes[(int)side].Length - 1)];

    /// <summary>The move animation player; smoke tests read what it played.</summary>
    public BattleEffects Effects => _effects;

    /// <summary>The backdrop and platforms on screen; smoke tests swap them to check each one.</summary>
    public string Scenery => $"{_backdrop.Backdrop} + {_backdrop.Stage}";

    public void SetScenery(string backdrop, string stage)
    {
        _backdrop.Load(backdrop, stage);
        QueueRedraw();
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        _backdrop = new BattleBackdrop(_assets, _scenery.Backdrop, _scenery.Stage);
        AddChild(_backdrop);
        _backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // Move animations dim the background behind the Pokémon.
        _dim = new ColorRect { Color = Colors.Transparent, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_dim);
        _dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // Sprites are added far to near so the nearer ones overlap: the foe's side, then the player's.
        var width = _battle.Width;
        _sprites[0] = new TextureRect[width];
        _sprites[1] = new TextureRect[width];
        foreach (var side in new[] { BattleSide.Foe, BattleSide.Player })
            foreach (var slot in Enumerable.Range(0, width).OrderBy(i => BattleEffects.Home(side, i, width).Z))
            {
                var mon = (side == BattleSide.Player ? _battle.PlayerField : _battle.FoeField)[slot].Mon;
                var scale = BattleEffects.Place(Vector2.Zero, Vector2.One, side, slot, width).Scale * (side == BattleSide.Player ? 3.2f : 2.6f);
                _sprites[(int)side][slot] = Sprite(SpritePath(mon, side), scale);
            }
        if (_battle.Trainer is { } trainer)
            _trainerSprite = Sprite(_assets.TrainerSpritePath(trainer.Class), 3f);

        _effects = new BattleEffects(_assets, () => (_playerFeet, _foeFeet), SpriteAt, _dim);
        AddChild(_effects);
        _effects.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _weather = new WeatherLayer();
        AddChild(_weather);
        _weather.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _boxes[0] = new InfoBox[width];
        _boxes[1] = new InfoBox[width];
        for (var i = 0; i < width; i++)
        {
            AddChild(_boxes[1][i] = new InfoBox(_battle.FoeField[i].Mon, showNumbers: false, compact: width > 1));
            AddChild(_boxes[0][i] = new InfoBox(_battle.PlayerField[i].Mon, showNumbers: true, compact: width > 1));
        }

        var bottom = new PanelContainer();
        bottom.AddThemeStyleboxOverride("panel", Box(new Color(0.12f, 0.16f, 0.22f), new Color(0.9f, 0.55f, 0.25f), 3));
        AddChild(bottom);
        bottom.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        bottom.OffsetTop = -150;
        var row = Ui.Ui.HBox(16);
        bottom.AddChild(row);
        _message = Ui.Ui.Text("", 24, wrap: true);
        _message.CustomMinimumSize = new Vector2(0, 110);
        row.AddChild(_message);

        _commands = new GridContainer { Columns = 2, Visible = false };
        _commands.AddThemeConstantOverride("h_separation", 8);
        _commands.AddThemeConstantOverride("v_separation", 8);
        _commands.AddChild(Command("Fight", new Color(0.85f, 0.3f, 0.25f), ShowMoves));
        _commands.AddChild(Command("Bag", new Color(0.85f, 0.6f, 0.2f), ShowBag));
        _commands.AddChild(Command("Pokémon", new Color(0.3f, 0.65f, 0.3f), () => ShowParty(forced: false)));
        _commands.AddChild(Command("Run", new Color(0.3f, 0.45f, 0.85f), () => Act(new BattleChoice.Run())));
        row.AddChild(_commands);

        _moves = new GridContainer { Columns = 2, Visible = false };
        _moves.AddThemeConstantOverride("h_separation", 8);
        _moves.AddThemeConstantOverride("v_separation", 8);
        row.AddChild(_moves);

        // Bag and party lists open in a panel above the message box.
        _picker = new PanelContainer { Visible = false };
        _picker.AddThemeStyleboxOverride("panel", Box(new Color(0.12f, 0.16f, 0.22f, 0.97f), new Color(0.9f, 0.55f, 0.25f), 3));
        _pickerTitle = Ui.Ui.Text("", 20, Ui.Ui.Accent);
        _pickerList = Ui.Ui.VBox(6);
        _picker.AddChild(Ui.Ui.VBox(10, _pickerTitle, _pickerList));
        AddChild(_picker);

        Resized += Layout;
        Layout();
        Intro();
    }

    async void Intro()
    {
        _busy = true;
        // Let the info boxes take their size before placing everything.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!IsInstanceValid(this))
            return;
        Layout();
        // The wild Pokémon (or the trainer, whose Pokémon comes out later) slides in from the left, the player's from the right.
        var foes = _trainerSprite is { } trainer ? [trainer] : _sprites[1];
        if (_trainerSprite is not null)
            foreach (var sprite in _sprites[1])
                sprite.Modulate = Colors.Transparent;
        foreach (var box in _boxes.SelectMany(b => b))
            box.Modulate = Colors.Transparent;
        var tween = CreateTween().SetParallel();
        foreach (var (sprite, from) in foes.Select(f => (f, -0.7f)).Concat(_sprites[0].Select(p => (p, 0.7f))))
        {
            var end = sprite.Position;
            sprite.Position = end + new Vector2(Size.X * from, 0);
            tween.TweenProperty(sprite, "position", end, 0.7).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        }
        await ToSignal(tween, Tween.SignalName.Finished);
        if (!IsInstanceValid(this))
            return;
        foreach (var box in (_trainerSprite is null ? _boxes[1] : []).Concat(_boxes[0]))
            CreateTween().TweenProperty(box, "modulate", Colors.White, 0.25);
        Play(_battle.Start());
    }

    void Layout()
    {
        var w = Size.X;
        var h = Size.Y - 150;
        var width = _battle.Width;
        // The single-battle spots the camera is fitted to (a little closer together for three a side, so all fit).
        _foeFeet = width >= 3 ? new Vector2(w * 0.68f, h * 0.5f) : new Vector2(w * 0.72f, h * 0.52f);
        _playerFeet = width >= 3 ? new Vector2(w * 0.35f, h - 6) : new Vector2(w * 0.27f, h + 8);
        for (var i = 0; i < width; i++)
        {
            PlaceSprite(_sprites[1][i], BattleEffects.Place(_playerFeet, _foeFeet, BattleSide.Foe, i, width).Feet);
            PlaceSprite(_sprites[0][i], BattleEffects.Place(_playerFeet, _foeFeet, BattleSide.Player, i, width).Feet);
        }
        if (width == 1)
            _backdrop.PlaceStages(new Vector2(w * 0.72f, h * 0.5f), w * 0.34f, new Vector2(w * 0.27f, h * 0.98f), w * 0.44f);
        else
        {
            // One platform under each side's group, wide enough for all of them.
            (Vector2 Centre, float Width) Group(BattleSide side, float single, float drop)
            {
                var feet = Enumerable.Range(0, width).Select(i => BattleEffects.Place(_playerFeet, _foeFeet, side, i, width).Feet).ToList();
                var centre = new Vector2((feet.Min(f => f.X) + feet.Max(f => f.X)) / 2, feet.Average(f => f.Y) + drop);
                return (centre, feet.Max(f => f.X) - feet.Min(f => f.X) + single);
            }
            var foe = Group(BattleSide.Foe, w * 0.2f, -h * 0.02f);
            var mine = Group(BattleSide.Player, w * 0.22f, -h * 0.03f);
            _backdrop.PlaceStages(foe.Centre, foe.Width, mine.Centre, mine.Width);
        }
        if (_trainerSprite is not null)
            PlaceSprite(_trainerSprite, new Vector2(w * 0.72f, h * 0.52f));
        // Info boxes: the foe's down the top left, the player's up the right (in slot order, left to right).
        for (var i = 0; i < width; i++)
        {
            var foeBox = _boxes[1][width - 1 - i];
            foeBox.Position = new Vector2(w * 0.06f, h * 0.08f + i * (foeBox.Size.Y + 6));
            var mine = _boxes[0][i];
            mine.Position = new Vector2(w * 0.94f - mine.Size.X, h * (width > 1 ? 0.5f : 0.62f) + i * (mine.Size.Y + 6));
        }
        _picker.Size = new Vector2(440, 0);
        _picker.Position = new Vector2(w - _picker.Size.X - 16, h - _picker.GetCombinedMinimumSize().Y - 12);
        QueueRedraw();
    }

    /// <summary>Keeps a sprite's bottom centre on <paramref name="feet"/> (battle sprites stand on the bottom of their frame).</summary>
    static void PlaceSprite(TextureRect sprite, Vector2 feet) =>
        sprite.Position = feet - new Vector2(sprite.Size.X / 2, sprite.Size.Y);

    public override void _Draw()
    {
        // The ROM's scenery replaces this plain field when it's there.
        if (_backdrop.Loaded)
            return;
        var w = Size.X;
        var h = Size.Y - 150;
        // Sky and field, then the two platforms.
        DrawRect(new Rect2(0, 0, w, h * 0.45f), new Color(0.62f, 0.83f, 0.95f));
        DrawRect(new Rect2(0, h * 0.45f, w, Size.Y - h * 0.45f), new Color(0.56f, 0.8f, 0.5f));
        DrawEllipse(new Vector2(w * 0.72f, h * 0.5f), new Vector2(w * 0.17f, h * 0.07f), new Color(0.42f, 0.66f, 0.36f));
        DrawEllipse(new Vector2(w * 0.27f, h * 0.98f), new Vector2(w * 0.22f, h * 0.09f), new Color(0.42f, 0.66f, 0.36f));
    }

    void DrawEllipse(Vector2 centre, Vector2 radius, Color color)
    {
        var points = new Vector2[48];
        for (var i = 0; i < points.Length; i++)
        {
            var a = Mathf.Tau * i / points.Length;
            points[i] = centre + new Vector2(Mathf.Cos(a) * radius.X, Mathf.Sin(a) * radius.Y);
        }
        DrawColoredPolygon(points, color);
        DrawPolyline([.. points, points[0]], color.Darkened(0.25f), 3);
    }

    static ImageTexture? LoadTexture(string path) =>
        File.Exists(path) && Image.LoadFromFile(path) is { } image ? ImageTexture.CreateFromImage(image) : null;

    string SpritePath(Pokemon mon, BattleSide side) =>
        _assets.BattleSpritePath(mon.Species.Id, side == BattleSide.Player ? (mon.Shiny ? "back-shiny" : "back") : (mon.Shiny ? "front-shiny" : "front"));

    TextureRect Sprite(string path, float scale)
    {
        var texture = LoadTexture(path);
        var rect = new TextureRect
        {
            Texture = texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            TextureFilter = TextureFilterEnum.Nearest,
            Size = (texture?.GetSize() ?? new Vector2(96, 96)) * scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(rect);
        return rect;
    }

    static Button Command(string text, Color color, Action onPressed)
    {
        var button = Ui.Ui.Button(text, onPressed, 22);
        button.CustomMinimumSize = new Vector2(170, 52);
        button.AddThemeStyleboxOverride("normal", Box(color.Darkened(0.15f), color.Lightened(0.3f), 2));
        button.AddThemeStyleboxOverride("hover", Box(color, Colors.White, 2));
        button.AddThemeStyleboxOverride("focus", Box(color, Colors.White, 3));
        button.AddThemeStyleboxOverride("pressed", Box(color.Darkened(0.3f), Colors.White, 2));
        return button;
    }

    static StyleBoxFlat Box(Color fill, Color border, int borderWidth) => new()
    {
        BgColor = fill, BorderColor = border,
        BorderWidthLeft = borderWidth, BorderWidthTop = borderWidth, BorderWidthRight = borderWidth, BorderWidthBottom = borderWidth,
        CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
        ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 8, ContentMarginBottom = 8,
    };

    /// <summary>The Pokémon choosing now (the only one in a single battle).</summary>
    Battler Chooser => _battle.Width > 1 ? _battle.PlayerField[_chooser] : _battle.Player;

    /// <summary>True while the menu waits for the player's choice; for smoke tests.</summary>
    public bool Choosing => !_busy && (_commands.Visible || _moves.Visible || _picker.Visible) && !Asking && !Naming;

    /// <summary>Sends out party member <paramref name="index"/> after a faint, as if picked from the list; for smoke tests.</summary>
    public void Replace(int index)
    {
        _picker.Visible = false;
        Do("send-out", () => _battle.SendOut(index), a: index);
    }

    /// <summary>Makes the current Pokémon's choice as if picked from the menu; for smoke tests.</summary>
    public void Choose(BattleChoice choice)
    {
        _picker.Visible = _moves.Visible = false;
        Act(choice);
    }

    /// <summary>Starts the turn's choices: one Pokémon at a time in double and triple battles.</summary>
    void ShowCommands()
    {
        if (_battle.Width > 1)
        {
            _choices = new BattleChoice?[_battle.Width];
            _chooser = -1;
            NextChooser();
            return;
        }
        // Charging, recharging or rampaging: the move is already decided.
        if (_battle.PlayerLocked)
        {
            Do("locked", () => _battle.Fight(0));
            return;
        }
        AskChooser();
    }

    void AskChooser()
    {
        _moves.Visible = false;
        _picker.Visible = false;
        _commands.Visible = true;
        var rotate = _battle.CanRotate ? "\n(Pokémon: rotate for free)" : "";
        _message.Text = $"What will {Chooser.Mon.Name} do?{rotate}";
        _commands.GetChild<Button>(0).GrabFocus();
    }

    /// <summary>Moves on to the next of the player's Pokémon that can choose, or plays the turn once all have.</summary>
    void NextChooser()
    {
        while (++_chooser < _battle.Width)
        {
            var b = _battle.PlayerField[_chooser];
            if (!b.Mon.Fainted && !SingleBattle.Locked(b))
            {
                AskChooser();
                return;
            }
        }
        PlayTurn([.. _choices]);
    }

    /// <summary>The chooser's action: the turn itself in a single battle, else noted until everyone has chosen.</summary>
    void Act(BattleChoice choice)
    {
        if (_battle.Width == 1)
        {
            PlayTurn([choice]);
            return;
        }
        _choices[_chooser] = choice;
        if (choice is BattleChoice.Run)
        {
            PlayTurn([.. _choices]);
            return;
        }
        NextChooser();
    }

    /// <summary>Esc on the command menu: back to the previous Pokémon's choice in a double or triple battle.</summary>
    void PreviousChooser()
    {
        for (var i = _chooser - 1; i >= 0; i--)
        {
            var b = _battle.PlayerField[i];
            if (!b.Mon.Fainted && !SingleBattle.Locked(b))
            {
                _chooser = i;
                _choices[i] = null;
                AskChooser();
                return;
            }
        }
    }

    /// <summary>A move from the move list: in a double or triple battle, ask for its target when there's a choice.</summary>
    void PickMove(int index)
    {
        var user = Chooser;
        var move = user.Mon.Moves[index].Move;
        if (!_battle.NeedsTarget(user, move))
        {
            Act(new BattleChoice.Fight(index));
            return;
        }
        var options = new List<(string, bool, Action)>();
        foreach (var side in new[] { BattleSide.Foe, BattleSide.Player })
        {
            var field = side == BattleSide.Player ? _battle.PlayerField : _battle.FoeField;
            for (var i = 0; i < field.Count; i++)
            {
                var b = field[i];
                if (b == user || b.Mon.Fainted)
                    continue;
                var reach = _battle.Targets(user, move, side, i).Contains(b);
                var slot = i;
                var label = side == BattleSide.Foe ? $"Foe's {b.Mon.Name}" : $"Ally {b.Mon.Name}";
                options.Add(($"{label}   HP {Math.Max(1, b.Mon.Hp * 100 / Math.Max(1, b.Mon.MaxHp))}%", reach,
                    () => Act(new BattleChoice.Fight(index, side, slot))));
            }
        }
        ShowPicker($"{move.Name}: at which Pokémon?", options, ShowMoves);
        _message.Text = $"Use {move.Name} on which Pokémon?";
    }

    void ShowMoves()
    {
        var mon = Chooser.Mon;
        if (mon.Moves.All(m => m.Pp <= 0))
        {
            Act(new BattleChoice.Fight(0));
            return;
        }
        Ui.Ui.ClearChildren(_moves);
        for (var i = 0; i < 4; i++)
        {
            if (i >= mon.Moves.Count)
            {
                _moves.AddChild(new Control { CustomMinimumSize = new Vector2(230, 52) });
                continue;
            }
            var slot = mon.Moves[i];
            var index = i;
            var type = slot.Move.Type < _typeNames.Count ? _typeNames[slot.Move.Type] : "";
            var button = Command($"{slot.Move.Name}\n{type}   PP {slot.Pp}/{slot.MaxPp}", TypeColor(slot.Move.Type), () => PickMove(index));
            button.AddThemeFontSizeOverride("font_size", 17);
            button.CustomMinimumSize = new Vector2(230, 52);
            button.Disabled = slot.Pp <= 0;
            _moves.AddChild(button);
        }
        _commands.Visible = false;
        _moves.Visible = true;
        _message.Text = "Choose a move.  (Esc: back)";
        if (_moves.GetChildren().OfType<Button>().FirstOrDefault(b => !b.Disabled) is { } first)
            first.GrabFocus();
    }

    /// <summary>Opens the list panel with <paramref name="options"/>; <paramref name="onCancel"/> null means it can't be closed.</summary>
    void ShowPicker(string title, IEnumerable<(string Text, bool Enabled, Action OnPick)> options, Action? onCancel)
    {
        _commands.Visible = _moves.Visible = false;
        Ui.Ui.ClearChildren(_pickerList);
        foreach (var (text, enabled, onPick) in options)
        {
            var button = Ui.Ui.Button(text, () =>
            {
                _picker.Visible = false;
                onPick();
            }, 18);
            button.Alignment = HorizontalAlignment.Left;
            button.Disabled = !enabled;
            _pickerList.AddChild(button);
        }
        if (onCancel is not null)
            _pickerList.AddChild(Ui.Ui.Button("Cancel  (Esc)", ClosePicker, 18));
        _pickerTitle.Text = title;
        _pickerCancel = onCancel;
        _picker.Visible = true;
        _picker.ResetSize();
        Layout();
        if (_pickerList.GetChildren().OfType<Button>().FirstOrDefault(b => !b.Disabled) is { } first)
            first.GrabFocus();
    }

    void ClosePicker()
    {
        _picker.Visible = false;
        var cancel = _pickerCancel;
        _pickerCancel = null;
        cancel?.Invoke();
    }

    void ShowBag()
    {
        var data = OfflineSession.Data;
        var options = _battle.Bag.Balls.Concat(_battle.Bag.Medicine).Select(i =>
        {
            var item = i.Item;
            Action pick = ItemEffect.For(item) is ItemEffect.Ball
                ? () => Act(new BattleChoice.Item(item))
                : () => ShowParty(forced: false, useItem: item);
            return ($"{data.ItemName(item)}   ×{i.Count}", true, pick);
        }).ToList();
        if (options.Count == 0)
        {
            Say("The Bag is empty.");
            return;
        }
        ShowPicker("Bag", options, AskChooser);
        _message.Text = "Use which item?";
    }

    /// <summary>The party list: to switch, to pick the next Pokémon after a faint (<paramref name="forced"/>), or to use an item on.</summary>
    void ShowParty(bool forced, int useItem = 0)
    {
        var party = _battle.Party;
        var rotating = !forced && useItem == 0 && _battle.CanRotate;
        // Already chosen to come in this turn (double and triple battles).
        var incoming = _choices.OfType<BattleChoice.Switch>().Select(c => c.PartyIndex).ToHashSet();
        var options = party.Select((mon, i) =>
        {
            var status = mon.Fainted ? "  FNT" : mon.Status != Ailment.None ? $"  {StatusTag(mon.Status)}" : "";
            var text = $"{mon.Name}  Lv{mon.Level}   HP {mon.Hp}/{mon.MaxHp}{status}{(_battle.IsOut(i) ? "   (in battle)" : "")}";
            var index = i;
            if (useItem != 0)
                return (text, true, (Action)(() => Act(new BattleChoice.Item(useItem, index))));
            if (rotating)
                return (text, _battle.RotationTeam.Contains(mon) && _battle.CanSendOut(i) && !_battle.IsOut(i),
                    (Action)(() => Do("rotate", () => _battle.Rotate(index), a: index)));
            var can = _battle.CanSendOut(i) && !_battle.IsOut(i) && !incoming.Contains(i);
            return (text, can, forced ? () => Do("send-out", () => _battle.SendOut(index), a: index) : (Action)(() => Act(new BattleChoice.Switch(index))));
        });
        var title = useItem != 0 ? $"Use the {OfflineSession.Data.ItemName(useItem)} on which Pokémon?"
            : forced ? "Choose the next Pokémon." : rotating ? "Rotate to which Pokémon? (free, once a turn)" : "Switch to which Pokémon?";
        ShowPicker(title, options, forced ? null : AskChooser);
        _message.Text = title;
    }

    /// <summary>A quick message that keeps the menu open.</summary>
    async void Say(string text)
    {
        _commands.Visible = false;
        _message.Text = text;
        await Wait(MessageSeconds);
        if (IsInstanceValid(this) && !_battle.Over)
            AskChooser();
    }

    void PlayTurn(BattleChoice?[] choices) => Do("turn", () => _battle.Turn(choices), choices);

    static NetChoice? ToNet(BattleChoice? choice) => choice switch
    {
        BattleChoice.Fight f => new NetChoice("fight", f.Slot, (int)f.TargetSide, f.TargetSlot),
        BattleChoice.Switch s => new NetChoice("switch", s.PartyIndex),
        BattleChoice.Item i => new NetChoice("item", i.ItemId, i.PartyIndex),
        BattleChoice.Run => new NetChoice("run"),
        _ => null,
    };

    /// <summary>
    /// Takes an action and plays what happens. Online the server plays it first and hands over the dice it rolled, so
    /// this replays the same turn; if it refuses, the menu comes back.
    /// </summary>
    async void Do(string kind, Func<IReadOnlyList<BattleEvent>> action, BattleChoice?[]? choices = null, int a = 0)
    {
        if (_link is not null)
        {
            _busy = true;
            _commands.Visible = _moves.Visible = _picker.Visible = false;
            var refused = await _link.Act(_battle, kind, choices?.Select(ToNet).ToList(), a);
            if (!IsInstanceValid(this))
                return;
            _busy = false;
            if (refused is not null)
            {
                _message.Text = refused;
                await Wait(MessageSeconds);
                if (!IsInstanceValid(this))
                    return;
                if (_battle.NeedsSwitch)
                    ShowParty(forced: true);
                else
                    ShowCommands();
                return;
            }
        }
        var events = action();
        _link?.Played(_battle);
        Play(events);
    }

    /// <summary>Plays a turn's events, then shows the menu again, asks for the next Pokémon, or ends the battle.</summary>
    async void Play(IReadOnlyList<BattleEvent> events)
    {
        if (events.Count == 0)
        {
            ShowCommands();
            return;
        }
        _busy = true;
        _commands.Visible = _moves.Visible = _picker.Visible = false;
        foreach (var e in events)
        {
            if (!IsInstanceValid(this))
                return;
            switch (e)
            {
                case MessageEvent m:
                    _message.Text = m.Text;
                    await Wait(MessageSeconds);
                    break;
                case HpEvent hp:
                    var box = BoxAt(hp.Side, hp.Slot);
                    var sprite = SpriteAt(hp.Side, hp.Slot);
                    if (hp.Hp < box.Hp)
                        Blink(sprite);
                    await ToSignal(box.AnimateHp(hp.Hp, hp.MaxHp, HpSeconds), Tween.SignalName.Finished);
                    break;
                case StatusEvent s:
                    BoxAt(s.Side, s.Slot).SetStatus(s.Status);
                    break;
                case FaintEvent f:
                    var fainted = SpriteAt(f.Side, f.Slot);
                    var fall = CreateTween().SetParallel();
                    fall.TweenProperty(fainted, "position", fainted.Position + new Vector2(0, fainted.Size.Y * 0.6f), 0.4);
                    fall.TweenProperty(fainted, "modulate", Colors.Transparent, 0.4);
                    await ToSignal(fall, Tween.SignalName.Finished);
                    break;
                case ExpEvent exp when _boxes[0].FirstOrDefault(b => b.Shows(_battle.Party[exp.PartyIndex])) is { } expBox:
                    await ToSignal(expBox.AnimateExp(0.6), Tween.SignalName.Finished);
                    break;
                case LevelUpEvent up when _boxes[0].FirstOrDefault(b => b.Shows(_battle.Party[up.PartyIndex])) is { } upBox:
                    upBox.Refresh();
                    break;
                case SwitchEvent { Side: BattleSide.Foe } sw:
                    await SendOutFoe(_battle.FoeParty[sw.PartyIndex], sw.Slot);
                    break;
                case SwitchEvent sw:
                    await SendOut(_battle.Party[sw.PartyIndex], sw.Slot);
                    break;
                case CatchEvent c:
                    await ThrowBall(c);
                    break;
                case LearnMoveEvent learn:
                    await AskToLearn(_battle.Party[learn.PartyIndex], learn.Move, learn.PartyIndex);
                    break;
                case AbilityEvent ability:
                    await ShowAbility(ability);
                    break;
                case TrainerEvent t:
                    await ShowTrainer(t.Visible);
                    break;
                case WeatherEvent w:
                    _weather.Weather = w.Weather;
                    break;
                case MoveAnimationEvent anim:
                    await _effects.Play(anim.Move, anim.User, anim.Slot, anim.Target, anim.TargetSlot, _battle.Width);
                    break;
            }
        }
        if (!IsInstanceValid(this))
            return;
        if (_battle.Outcome == BattleOutcome.Caught)
        {
            await AskNickname(_battle.Caught ?? _battle.Foe.Mon);
            if (!IsInstanceValid(this))
                return;
        }
        _busy = false;
        if (_battle.Over)
        {
            await Wait(0.3f);
            EmitSignal(SignalName.Finished, (int)_battle.Outcome);
        }
        else if (_battle.NeedsSwitch)
            ShowParty(forced: true);
        else
            ShowCommands();
    }

    /// <summary>
    /// The trainer steps aside to the right as their first Pokémon appears, or slides back in after their last one
    /// faints to say their line.
    /// </summary>
    async Task ShowTrainer(bool visible)
    {
        if (_trainerSprite is null)
            return;
        var home = _trainerSprite.Position;
        var tween = CreateTween().SetParallel();
        if (visible)
        {
            _trainerSprite.Position = home + new Vector2(Size.X * 0.3f, 0);
            _trainerSprite.Modulate = Colors.Transparent;
            tween.TweenProperty(_trainerSprite, "position", home, 0.4).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
            tween.TweenProperty(_trainerSprite, "modulate", Colors.White, 0.3);
            await ToSignal(tween, Tween.SignalName.Finished);
            return;
        }
        tween.TweenProperty(_trainerSprite, "position", home + new Vector2(Size.X * 0.3f, 0), 0.4).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        tween.TweenProperty(_trainerSprite, "modulate", Colors.Transparent, 0.4);
        for (var i = 0; i < _sprites[1].Length; i++)
        {
            tween.TweenProperty(_sprites[1][i], "modulate", Colors.White, 0.3).SetDelay(0.2);
            tween.TweenProperty(_boxes[1][i], "modulate", Colors.White, 0.3).SetDelay(0.3);
        }
        await ToSignal(tween, Tween.SignalName.Finished);
        if (IsInstanceValid(_trainerSprite))
            _trainerSprite.Position = home;
    }

    /// <summary>
    /// A banner by the side's info box naming the ability that just took effect ("Snivy's Overgrow"); it slides in,
    /// holds while the next messages play, then fades.
    /// </summary>
    async Task ShowAbility(AbilityEvent e)
    {
        var player = e.Side == BattleSide.Player;
        var field = player ? _battle.PlayerField : _battle.FoeField;
        var mon = field[Math.Clamp(e.Slot, 0, field.Count - 1)].Mon;
        var box = BoxAt(e.Side, e.Slot);
        LastAbility = $"{mon.Name}'s {e.Ability}";
        var banner = new PanelContainer { Modulate = Colors.Transparent, MouseFilter = MouseFilterEnum.Ignore };
        banner.AddThemeStyleboxOverride("panel", Box(new Color(0.12f, 0.16f, 0.22f, 0.95f), Ui.Ui.Accent, 2));
        banner.AddChild(Ui.Ui.VBox(0, Ui.Ui.Text($"{mon.Name}'s", 15), Ui.Ui.Text(e.Ability, 21, Ui.Ui.Accent)));
        AddChild(banner);
        var end = player ? new Vector2(box.Position.X, box.Position.Y - 78) : new Vector2(box.Position.X, box.Position.Y + box.Size.Y + 10);
        banner.Position = end + new Vector2(player ? 40 : -40, 0);
        var slide = CreateTween().SetParallel();
        slide.TweenProperty(banner, "position", end, 0.2);
        slide.TweenProperty(banner, "modulate", Colors.White, 0.2);
        await Wait(0.6f);
        if (!IsInstanceValid(banner))
            return;
        var fade = banner.CreateTween();
        fade.TweenInterval(1.2);
        fade.TweenProperty(banner, "modulate", Colors.Transparent, 0.3);
        fade.TweenCallback(Callable.From(banner.QueueFree));
    }

    /// <summary>"Give a nickname to the caught X?": a name field, Enter or OK to keep it, Esc or Skip for none.</summary>
    async Task AskNickname(Pokemon mon)
    {
        _nickname = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _message.Text = $"Give a nickname to the caught {mon.Species.Name}?";
        var field = new LineEdit { PlaceholderText = mon.Species.Name, MaxLength = Pokemon.MaxNicknameLength, CustomMinimumSize = new Vector2(240, 0) };
        field.TextSubmitted += text => _nickname?.TrySetResult(text);
        field.GuiInput += e =>
        {
            if (e.IsActionPressed("ui_cancel"))
                _nickname?.TrySetResult(null);
        };
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", Box(new Color(0.12f, 0.16f, 0.22f, 0.97f), new Color(0.9f, 0.55f, 0.25f), 3));
        panel.AddChild(Ui.Ui.VBox(10, Ui.Ui.Text($"Nickname for {mon.Species.Name}", 20, Ui.Ui.Accent), field,
            Ui.Ui.HBox(8, Ui.Ui.Button("OK", () => _nickname?.TrySetResult(field.Text)), Ui.Ui.Button("Skip", () => _nickname?.TrySetResult(null)))));
        AddChild(panel);
        panel.Position = new Vector2(Size.X / 2 - 160, Size.Y - 150 - 170);
        field.GrabFocus();
        Naming = true;
        var name = await _nickname.Task;
        Naming = false;
        _nickname = null;
        if (!IsInstanceValid(this))
            return;
        panel.QueueFree();
        mon.Rename(name);
        _link?.Tell("nickname", text: name ?? "");
        if (mon.Nickname is not null)
        {
            _message.Text = $"{mon.Species.Name} is now called {mon.Nickname}!";
            await Wait(MessageSeconds);
        }
    }

    /// <summary>
    /// A Pokémon that knows four moves reached a new one: the player picks a move to forget, or keeps the four. As in the
    /// games, the choice can't be backed out of; "Don't learn" is the way out.
    /// </summary>
    async Task AskToLearn(Pokemon mon, MoveInfo move, int partyIndex)
    {
        _message.Text = $"{mon.Name} wants to learn the move {move.Name}.\nBut {mon.Name} can't learn more than four moves.";
        await Wait(MessageSeconds);
        if (!IsInstanceValid(this))
            return;
        var choice = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = mon.Moves.Select((slot, i) =>
                ($"Forget {slot.Move.Name}   {TypeName(slot.Move.Type)}  PP {slot.Pp}/{slot.MaxPp}", true, (Action)(() => choice.TrySetResult(i))))
            .Append(($"Don't learn {move.Name}", true, () => choice.TrySetResult(-1)));
        var power = move.Power > 1 ? $"power {move.Power}, " : "";
        Asking = true;
        ShowPicker($"{move.Name}: {TypeName(move.Type)}, {power}PP {move.Pp}. Forget which move?", options, null);
        _message.Text = $"Should a move be forgotten to make space for {move.Name}?";
        var forget = await choice.Task;
        Asking = false;
        if (!IsInstanceValid(this))
            return;
        _link?.Tell("learn", partyIndex, move.Id, forget);
        if (forget >= 0 && mon.LearnMove(move, forget) is { } old)
        {
            _message.Text = $"1, 2, and… … Poof!\n{mon.Name} forgot {old.Name}.";
            await Wait(MessageSeconds);
            _message.Text = $"And… {mon.Name} learned {move.Name}!";
        }
        else
            _message.Text = $"{mon.Name} did not learn {move.Name}.";
        await Wait(MessageSeconds);
    }

    string TypeName(int type) => type < _typeNames.Count ? _typeNames[type] : "";

    /// <summary>Recalls the Pokémon on the near platform (if still there) and sends <paramref name="mon"/> out in its place.</summary>
    async Task SendOut(Pokemon mon, int slot = 0)
    {
        Layout();
        var sprite = SpriteAt(BattleSide.Player, slot);
        var home = sprite.Position;
        if (sprite.Modulate.A > 0.1f)
        {
            var back = CreateTween().SetParallel();
            back.TweenProperty(sprite, "position", home - new Vector2(Size.X * 0.5f, 0), 0.3);
            back.TweenProperty(BoxAt(BattleSide.Player, slot), "modulate", Colors.Transparent, 0.2);
            await ToSignal(back, Tween.SignalName.Finished);
        }
        sprite.Texture = LoadTexture(SpritePath(mon, BattleSide.Player));
        var box = ReplaceBox(BattleSide.Player, slot, mon);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Layout();
        home = sprite.Position;
        sprite.Position = home - new Vector2(Size.X * 0.5f, 0);
        sprite.Modulate = Colors.White;
        var enter = CreateTween().SetParallel();
        enter.TweenProperty(sprite, "position", home, 0.35).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        enter.TweenProperty(box, "modulate", Colors.White, 0.3);
        await ToSignal(enter, Tween.SignalName.Finished);
    }

    /// <summary>A fresh (hidden) info box for <paramref name="mon"/> in a side's slot.</summary>
    InfoBox ReplaceBox(BattleSide side, int slot, Pokemon mon)
    {
        var row = _boxes[(int)side];
        slot = Math.Clamp(slot, 0, row.Length - 1);
        row[slot].QueueFree();
        var box = row[slot] = new InfoBox(mon, showNumbers: side == BattleSide.Player, compact: row.Length > 1) { Modulate = Colors.Transparent };
        AddChild(box);
        MoveChild(_picker, -1);
        return box;
    }

    /// <summary>A trainer's next Pokémon drops onto the far platform with a fresh info box.</summary>
    async Task SendOutFoe(Pokemon mon, int slot = 0)
    {
        var sprite = SpriteAt(BattleSide.Foe, slot);
        sprite.Texture = LoadTexture(SpritePath(mon, BattleSide.Foe));
        var box = ReplaceBox(BattleSide.Foe, slot, mon);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Layout();
        var home = sprite.Position;
        sprite.Position = home - new Vector2(0, 60);
        sprite.Modulate = Colors.Transparent;
        var enter = CreateTween().SetParallel();
        enter.TweenProperty(sprite, "position", home, 0.35).SetTrans(Tween.TransitionType.Bounce).SetEase(Tween.EaseType.Out);
        enter.TweenProperty(sprite, "modulate", Colors.White, 0.25);
        enter.TweenProperty(box, "modulate", Colors.White, 0.3);
        await ToSignal(enter, Tween.SignalName.Finished);
    }

    /// <summary>The ball flies to the wild Pokémon, swallows it, wobbles once per shake, then clicks shut or bursts open.</summary>
    async Task ThrowBall(CatchEvent c)
    {
        var ball = new BallIcon { Size = new Vector2(34, 34), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(ball);
        // The one wild Pokémon left standing (or the one just caught).
        var wildSlot = Math.Max(0, Enumerable.Range(0, _battle.Width).FirstOrDefault(i =>
            ReferenceEquals(_battle.FoeField[i].Mon, _battle.Caught) || !_battle.FoeField[i].Mon.Fainted));
        var _foeSprite = SpriteAt(BattleSide.Foe, wildSlot);
        var target = _foeSprite.Position + new Vector2(_foeSprite.Size.X / 2 - 17, _foeSprite.Size.Y * 0.55f);
        ball.Position = new Vector2(Size.X * 0.2f, Size.Y * 0.6f);
        var fly = CreateTween();
        fly.TweenProperty(ball, "position", target, 0.45).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        await ToSignal(fly, Tween.SignalName.Finished);
        var absorb = CreateTween().SetParallel();
        absorb.TweenProperty(_foeSprite, "modulate", new Color(1, 0.4f, 0.4f, 0), 0.3);
        await ToSignal(absorb, Tween.SignalName.Finished);
        var drop = CreateTween();
        drop.TweenProperty(ball, "position", target + new Vector2(0, _foeSprite.Size.Y * 0.4f), 0.25).SetTrans(Tween.TransitionType.Bounce).SetEase(Tween.EaseType.Out);
        await ToSignal(drop, Tween.SignalName.Finished);
        ball.PivotOffset = ball.Size / 2;
        for (var i = 0; i < c.Shakes; i++)
        {
            await Wait(0.35f);
            var wobble = CreateTween();
            wobble.TweenProperty(ball, "rotation", -0.4f, 0.1);
            wobble.TweenProperty(ball, "rotation", 0.4f, 0.15);
            wobble.TweenProperty(ball, "rotation", 0f, 0.1);
            await ToSignal(wobble, Tween.SignalName.Finished);
        }
        await Wait(0.4f);
        if (c.Caught)
        {
            ball.Modulate = new Color(0.75f, 0.75f, 0.75f);
            return;
        }
        ball.QueueFree();
        CreateTween().TweenProperty(_foeSprite, "modulate", Colors.White, 0.25);
    }

    /// <summary>A Poké Ball: red top, white bottom, black band and button.</summary>
    sealed partial class BallIcon : Control
    {
        public override void _Draw()
        {
            var c = Size / 2;
            var r = Mathf.Min(Size.X, Size.Y) / 2;
            DrawCircle(c, r, Colors.Black);
            DrawCircle(c, r - 2, new Color(0.96f, 0.96f, 0.96f));
            var top = new List<Vector2> { c + new Vector2(-(r - 2), 0) };
            for (var i = 0; i <= 16; i++)
            {
                var a = Mathf.Pi + Mathf.Pi * i / 16;
                top.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (r - 2));
            }
            DrawColoredPolygon([.. top], new Color(0.9f, 0.2f, 0.2f));
            DrawLine(c - new Vector2(r, 0), c + new Vector2(r, 0), Colors.Black, 3);
            DrawCircle(c, r * 0.3f, Colors.Black);
            DrawCircle(c, r * 0.2f, Colors.White);
        }
    }

    void Blink(TextureRect sprite)
    {
        var tween = CreateTween();
        for (var i = 0; i < 3; i++)
        {
            tween.TweenProperty(sprite, "modulate:a", 0.15f, 0.06);
            tween.TweenProperty(sprite, "modulate:a", 1f, 0.06);
        }
    }

    async Task Wait(float seconds)
    {
        _skip = false;
        for (var t = 0f; t < seconds && !_skip; t += 0.05f)
            await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("ui_cancel"))
        {
            // Esc never leaves the battle: it skips a nickname, backs out of the move list or a list panel, otherwise does nothing.
            if (Naming)
                SubmitNickname(null);
            else if (_picker.Visible && _pickerCancel is not null)
                ClosePicker();
            else if (_moves.Visible)
                AskChooser();
            else if (_commands.Visible && _battle.Width > 1)
                PreviousChooser();
            GetViewport().SetInputAsHandled();
        }
        else if (_busy && (e.IsActionPressed("ui_accept") || e is InputEventKey { Pressed: true, Keycode: Key.Z, Echo: false }))
        {
            _skip = true;
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (_busy && e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            _skip = true;
    }

    static string StatusTag(Ailment status) => status switch
    {
        Ailment.Paralysis => "PAR",
        Ailment.Sleep => "SLP",
        Ailment.Freeze => "FRZ",
        Ailment.Burn => "BRN",
        Ailment.Poison => "PSN",
        _ => "",
    };

    /// <summary>Colours roughly matching each type's usual colour, by ROM type id.</summary>
    static Color TypeColor(int type) => type switch
    {
        TypeChart.Fighting => new Color(0.75f, 0.19f, 0.16f),
        TypeChart.Flying => new Color(0.66f, 0.56f, 0.94f),
        TypeChart.Poison => new Color(0.63f, 0.25f, 0.63f),
        TypeChart.Ground => new Color(0.88f, 0.75f, 0.41f),
        TypeChart.Rock => new Color(0.72f, 0.63f, 0.22f),
        TypeChart.Bug => new Color(0.66f, 0.72f, 0.13f),
        TypeChart.Ghost => new Color(0.44f, 0.35f, 0.6f),
        TypeChart.Steel => new Color(0.72f, 0.72f, 0.82f),
        TypeChart.Fire => new Color(0.94f, 0.5f, 0.19f),
        TypeChart.Water => new Color(0.41f, 0.56f, 0.94f),
        TypeChart.Grass => new Color(0.47f, 0.78f, 0.31f),
        TypeChart.Electric => new Color(0.97f, 0.82f, 0.19f),
        TypeChart.Psychic => new Color(0.97f, 0.35f, 0.53f),
        TypeChart.Ice => new Color(0.6f, 0.85f, 0.85f),
        TypeChart.Dragon => new Color(0.44f, 0.22f, 0.97f),
        TypeChart.Dark => new Color(0.44f, 0.35f, 0.28f),
        _ => new Color(0.66f, 0.66f, 0.47f),
    };

    /// <summary>Name, gender, level, status and HP bar (and HP numbers and EXP bar for the player's side).</summary>
    /// <summary>The weather over the battlefield: falling rain or hail, blowing sand, or a warm sunlit tint.</summary>
    sealed partial class WeatherLayer : Control
    {
        Weather _weather;

        public WeatherLayer() => MouseFilter = MouseFilterEnum.Ignore;

        public Weather Weather
        {
            get => _weather;
            set
            {
                _weather = value;
                QueueRedraw();
            }
        }

        public override void _Process(double delta)
        {
            if (_weather != Weather.None)
                QueueRedraw();
        }

        public override void _Draw()
        {
            if (_weather == Weather.None)
                return;
            var t = (float)Time.GetTicksMsec() / 1000f;
            var w = Size.X;
            var h = Size.Y - 150;
            switch (_weather)
            {
                case Weather.Sun:
                    DrawRect(new Rect2(0, 0, w, h), new Color(1f, 0.85f, 0.4f, 0.16f));
                    for (var i = 0; i < 6; i++)
                    {
                        var x = w * (0.1f + i * 0.17f) + Mathf.Sin(t * 0.7f + i) * 20;
                        DrawLine(new Vector2(x, 0), new Vector2(x - h * 0.35f, h), new Color(1f, 0.95f, 0.7f, 0.10f), 26);
                    }
                    break;
                case Weather.Rain:
                    DrawRect(new Rect2(0, 0, w, h), new Color(0.2f, 0.25f, 0.4f, 0.18f));
                    for (var i = 0; i < 90; i++)
                    {
                        var x = Mathf.PosMod(i * 97.3f + t * 160, w + 80) - 40;
                        var y = Mathf.PosMod(i * 53.7f + t * 900, h);
                        DrawLine(new Vector2(x, y), new Vector2(x - 6, y + 22), new Color(0.75f, 0.85f, 1f, 0.55f), 2);
                    }
                    break;
                case Weather.Sandstorm:
                    DrawRect(new Rect2(0, 0, w, h), new Color(0.8f, 0.65f, 0.4f, 0.28f));
                    for (var i = 0; i < 120; i++)
                    {
                        var x = Mathf.PosMod(i * 71.9f + t * 700, w);
                        var y = Mathf.PosMod(i * 37.1f + Mathf.Sin(t * 2 + i) * 12, h);
                        DrawLine(new Vector2(x, y), new Vector2(x + 14, y + 2), new Color(0.9f, 0.75f, 0.5f, 0.6f), 2);
                    }
                    break;
                case Weather.Hail:
                    DrawRect(new Rect2(0, 0, w, h), new Color(0.7f, 0.85f, 1f, 0.14f));
                    for (var i = 0; i < 60; i++)
                    {
                        var x = Mathf.PosMod(i * 83.1f + t * 90, w);
                        var y = Mathf.PosMod(i * 61.3f + t * 420, h);
                        DrawCircle(new Vector2(x, y), 3.5f, new Color(0.95f, 0.98f, 1f, 0.85f));
                    }
                    break;
            }
        }
    }

    sealed partial class InfoBox : PanelContainer
    {
        readonly Pokemon _mon;
        readonly bool _player;
        readonly Label _name, _level, _status, _hpText;
        readonly ColorRect _hpFill, _expFill;
        float _shownHp;
        float _shownExp;

        public int Hp => Mathf.RoundToInt(_shownHp);

        public bool Shows(Pokemon mon) => ReferenceEquals(mon, _mon);

        public InfoBox() : this(null!, false) { }

        /// <param name="compact">Smaller, for two or three a side.</param>
        public InfoBox(Pokemon mon, bool showNumbers, bool compact = false)
        {
            _mon = mon;
            _player = showNumbers;
            _shownHp = mon.Hp;
            _shownExp = ExpFraction();
            AddThemeStyleboxOverride("panel", Box(new Color(0.97f, 0.96f, 0.9f), new Color(0.25f, 0.27f, 0.3f), 3));
            CustomMinimumSize = new Vector2(compact ? 260 : 330, 0);
            var big = compact ? 17 : 22;

            var dark = new Color(0.15f, 0.16f, 0.18f);
            var gender = mon.Gender switch { Gender.Male => " ♂", Gender.Female => " ♀", _ => "" };
            _name = Ui.Ui.Text(mon.Name + gender, big, dark);
            _level = Ui.Ui.Text("", big - 2, dark);
            _status = Ui.Ui.Text("", 16, Colors.White);
            _status.AddThemeStyleboxOverride("normal", new StyleBoxFlat { BgColor = Colors.Transparent, ContentMarginLeft = 6, ContentMarginRight = 6 });
            var top = Ui.Ui.HBox(8, _name, _status, Ui.Ui.Spacer(), _level);

            var hpLabel = Ui.Ui.Text("HP", 14, new Color(0.95f, 0.75f, 0.2f));
            var track = new ColorRect { Color = new Color(0.25f, 0.27f, 0.3f), CustomMinimumSize = new Vector2(0, 12), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _hpFill = new ColorRect { Color = Colors.Green, MouseFilter = MouseFilterEnum.Ignore };
            track.AddChild(_hpFill);
            var hpRow = Ui.Ui.HBox(6, hpLabel, track);
            hpRow.Alignment = BoxContainer.AlignmentMode.Center;

            _hpText = Ui.Ui.Text("", compact ? 14 : 18, dark);
            _hpText.HorizontalAlignment = HorizontalAlignment.Right;
            var expTrack = new ColorRect { Color = new Color(0.25f, 0.27f, 0.3f), CustomMinimumSize = new Vector2(0, 6) };
            _expFill = new ColorRect { Color = new Color(0.3f, 0.65f, 0.95f), MouseFilter = MouseFilterEnum.Ignore };
            expTrack.AddChild(_expFill);
            _hpText.Visible = expTrack.Visible = showNumbers;

            AddChild(Ui.Ui.VBox(compact ? 1 : 4, top, hpRow, _hpText, expTrack));
            track.Resized += () => DrawBars();
            expTrack.Resized += () => DrawBars();
            Refresh();
        }

        public void Refresh()
        {
            _level.Text = $"Lv{_mon.Level}";
            _shownHp = Math.Min(_shownHp, _mon.MaxHp);
            if (!_mon.Fainted && _shownHp < 1)
                _shownHp = _mon.Hp;
            _shownExp = ExpFraction();
            SetStatus(_mon.Status);
            DrawBars();
        }

        public void SetStatus(Ailment status)
        {
            var (text, color) = status switch
            {
                Ailment.Paralysis => ("PAR", new Color(0.85f, 0.7f, 0.1f)),
                Ailment.Sleep => ("SLP", new Color(0.5f, 0.5f, 0.55f)),
                Ailment.Freeze => ("FRZ", new Color(0.4f, 0.75f, 0.9f)),
                Ailment.Burn => ("BRN", new Color(0.9f, 0.35f, 0.2f)),
                Ailment.Poison => ("PSN", new Color(0.65f, 0.3f, 0.7f)),
                _ => ("", Colors.Transparent),
            };
            _status.Text = text;
            ((StyleBoxFlat)_status.GetThemeStylebox("normal")).BgColor = color;
        }

        public Tween AnimateHp(int hp, int maxHp, float seconds)
        {
            var tween = CreateTween();
            tween.TweenMethod(Callable.From<float>(v => { _shownHp = v; DrawBars(); }), _shownHp, (float)hp, seconds);
            return tween;
        }

        public Tween AnimateExp(double seconds)
        {
            var tween = CreateTween();
            var target = ExpFraction();
            // A level up wraps the bar: fill to the end first.
            if (target < _shownExp)
                tween.TweenMethod(Callable.From<float>(v => { _shownExp = v; DrawBars(); }), _shownExp, 1f, seconds / 2);
            tween.TweenMethod(Callable.From<float>(v => { _shownExp = v; DrawBars(); }), target < _shownExp ? 0f : _shownExp, target, seconds);
            return tween;
        }

        float ExpFraction()
        {
            if (_mon.Level >= 100)
                return 1;
            var from = ExpCurve.ForLevel(_mon.Species.GrowthRate, _mon.Level);
            var to = ExpCurve.ForLevel(_mon.Species.GrowthRate, _mon.Level + 1);
            return Math.Clamp((float)(_mon.Exp - from) / Math.Max(1, to - from), 0, 1);
        }

        void DrawBars()
        {
            var fraction = _mon.MaxHp > 0 ? Math.Clamp(_shownHp / _mon.MaxHp, 0, 1) : 0;
            var track = (Control)_hpFill.GetParent();
            _hpFill.Size = new Vector2(track.Size.X * fraction, track.Size.Y);
            _hpFill.Color = fraction > 0.5f ? new Color(0.3f, 0.85f, 0.4f) : fraction > 0.2f ? new Color(0.95f, 0.75f, 0.2f) : new Color(0.9f, 0.25f, 0.2f);
            _hpText.Text = $"{Mathf.RoundToInt(_shownHp)} / {_mon.MaxHp}";
            var expTrack = (Control)_expFill.GetParent();
            _expFill.Size = new Vector2(expTrack.Size.X * _shownExp, expTrack.Size.Y);
        }
    }
}

/// <summary>
/// A battle played on the server (online): the screen asks it to play each action first and replays the action with
/// the server's dice, and tells it the player's answers that change the game (moves learned, nicknames).
/// </summary>
public interface IBattleLink
{
    /// <summary>
    /// Sends an action; once the server has played it, reseeds <paramref name="battle"/> with its dice and returns null,
    /// or returns why it was refused.
    /// </summary>
    Task<string?> Act(SingleBattle battle, string kind, IReadOnlyList<NetChoice?>? choices, int a);

    /// <summary>After an action was replayed: checks the battle still stands as the server's does.</summary>
    void Played(SingleBattle battle);

    void Tell(string kind, int a = 0, int b = 0, int c = 0, string text = "");
}
