using Godot;
using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// The overworld X menu: the party (sprite, level, HP, status, moves) on the left and the Bag on the right. Picking
/// a medicine and then a Pokémon uses it; picking a Pokémon on its own moves it to the front of the party.
/// X or Esc closes the menu.
/// </summary>
public partial class PartyMenu : Control
{
    [Signal]
    public delegate void ClosedEventHandler();

    readonly GameAssets _assets;
    VBoxContainer _party = null!, _bag = null!;
    Label _hint = null!;
    int _selectedItem;

    public PartyMenu() : this(null!) { }

    public PartyMenu(GameAssets assets) => _assets = assets;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(dim);
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.1f, 0.12f, 0.17f, 0.97f), BorderColor = new Color(0.9f, 0.55f, 0.25f),
            BorderWidthLeft = 3, BorderWidthTop = 3, BorderWidthRight = 3, BorderWidthBottom = 3,
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            ContentMarginLeft = 20, ContentMarginRight = 20, ContentMarginTop = 16, ContentMarginBottom = 16,
        });
        AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        panel.GrowHorizontal = panel.GrowVertical = GrowDirection.Both;

        _party = Ui.Ui.VBox(6);
        _party.CustomMinimumSize = new Vector2(560, 0);
        _bag = Ui.Ui.VBox(6);
        _bag.CustomMinimumSize = new Vector2(280, 0);
        _hint = Ui.Ui.Text("", 16, Ui.Ui.Muted);
        panel.AddChild(Ui.Ui.VBox(12,
            Ui.Ui.HBox(24,
                Ui.Ui.VBox(8, Ui.Ui.Text("Party", 22, Ui.Ui.Accent), _party),
                Ui.Ui.VBox(8, Ui.Ui.Text($"Bag     ₽{OfflineSession.Money}", 22, Ui.Ui.Accent), _bag)),
            _hint));
        Refresh("Pick a medicine, then a Pokémon to use it on. Pick a Pokémon to make it lead.  X / Esc: close");
    }

    void Refresh(string hint)
    {
        _hint.Text = hint;
        Ui.Ui.ClearChildren(_party);
        Ui.Ui.ClearChildren(_bag);
        var data = OfflineSession.Data;
        var party = OfflineSession.Party;
        for (var i = 0; i < party.Count; i++)
        {
            var mon = party[i];
            var index = i;
            var status = mon.Fainted ? "FNT" : mon.Status == Ailment.None ? "" : mon.Status.ToString();
            var moves = string.Join(", ", mon.Moves.Select(m => $"{m.Move.Name} {m.Pp}/{m.MaxPp}"));
            var button = Ui.Ui.Button($"{mon.Name}{Gender(mon)}  Lv{mon.Level}   HP {mon.Hp}/{mon.MaxHp}  {status}\n{moves}", () => PickPokemon(index), 17);
            button.Alignment = HorizontalAlignment.Left;
            button.Icon = Icon(mon);
            button.ExpandIcon = false;
            button.AddThemeConstantOverride("icon_max_width", 56);
            _party.AddChild(button);
        }
        if (OfflineSession.Box.Count > 0)
            _party.AddChild(Ui.Ui.Text($"In the PC: {string.Join(", ", OfflineSession.Box.Select(m => $"{m.Name} Lv{m.Level}"))}", 15, Ui.Ui.Muted, wrap: true));

        foreach (var (item, count) in OfflineSession.Bag.Items)
        {
            var id = item;
            var medicine = ItemEffect.For(item) is ItemEffect.Heal or ItemEffect.Cure or ItemEffect.Revive;
            var button = Ui.Ui.Button($"{data.ItemName(item)}   ×{count}", () => PickItem(id), 17);
            button.Alignment = HorizontalAlignment.Left;
            button.Disabled = !medicine;
            if (id == _selectedItem)
                button.AddThemeColorOverride("font_color", Ui.Ui.Accent);
            _bag.AddChild(button);
        }
        if (_party.GetChildren().OfType<Button>().FirstOrDefault() is { } first)
            first.GrabFocus();
    }

    static string Gender(Pokemon mon) => mon.Gender switch { Core.Battle.Gender.Male => " ♂", Core.Battle.Gender.Female => " ♀", _ => "" };

    Texture2D? Icon(Pokemon mon)
    {
        var path = _assets.BattleSpritePath(mon.Species.Id, mon.Shiny ? "front-shiny" : "front");
        if (!File.Exists(path) || Image.LoadFromFile(path) is not { } image)
            return null;
        image.Resize(56, 56, Image.Interpolation.Nearest);
        return ImageTexture.CreateFromImage(image);
    }

    void PickItem(int item)
    {
        _selectedItem = item;
        Refresh($"Use the {OfflineSession.Data.ItemName(item)} on which Pokémon?  Esc: cancel");
    }

    void PickPokemon(int index)
    {
        var party = OfflineSession.Party;
        if (_selectedItem != 0)
        {
            var item = _selectedItem;
            _selectedItem = 0;
            var message = ItemEffect.For(item) is { } effect ? ItemUse.Apply(effect, party[index]) : null;
            if (message is null)
            {
                Refresh("It won't have any effect.");
                return;
            }
            OfflineSession.Bag.Take(item);
            Refresh(message);
            return;
        }
        if (index > 0)
        {
            (party[0], party[index]) = (party[index], party[0]);
            Refresh($"{party[0].Name} now leads the party.");
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        var close = e.IsActionPressed("ui_cancel") || e is InputEventKey { Pressed: true, Keycode: Key.X, Echo: false };
        if (!close)
            return;
        GetViewport().SetInputAsHandled();
        if (_selectedItem != 0 && e.IsActionPressed("ui_cancel"))
        {
            _selectedItem = 0;
            Refresh("Pick a medicine, then a Pokémon to use it on. Pick a Pokémon to make it lead.  X / Esc: close");
            return;
        }
        EmitSignal(SignalName.Closed);
        QueueFree();
    }
}
