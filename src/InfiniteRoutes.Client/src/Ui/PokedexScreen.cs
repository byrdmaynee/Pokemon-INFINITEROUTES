using Godot;
using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Client.Ui;

/// <summary>
/// Browses species data and battle sprites extracted from the player's Black/White ROM.
/// Doubles as a visual check that extraction worked.
/// </summary>
public partial class PokedexScreen : Control
{
    IReadOnlyList<SpeciesInfo> _species = [];
    readonly List<SpeciesInfo> _shown = [];
    ItemList _list = null!;
    TextureRect _sprite = null!;
    Label _name = null!;
    Label _info = null!;
    VBoxContainer _stats = null!;
    bool _shiny;
    bool _back;
    SpeciesInfo? _current;

    public override void _Ready()
    {
        var assets = ClientServices.UnovaAssets;
        if (assets is null)
        {
            var missing = Page.Create("Pokédex Viewer", Ui.Text(
                "No extracted data yet. Add Pokémon Black or White in Settings → ROM Library and press Extract.", 18, Ui.Warn, wrap: true));
            AddChild(missing);
            missing.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            return;
        }
        _species = assets.LoadSpecies();

        var search = new LineEdit { PlaceholderText = "Search name or number…", ClearButtonEnabled = true };
        _list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(280, 0) };
        _list.AddThemeFontSizeOverride("font_size", 17);
        _list.ItemSelected += i => Show(_shown[(int)i]);
        search.TextChanged += Filter;
        var left = Ui.VBox(8, search, _list);

        _sprite = new TextureRect
        {
            CustomMinimumSize = new Vector2(288, 288),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = TextureFilterEnum.Nearest,
        };
        var shiny = new CheckButton { Text = "Shiny" };
        shiny.Toggled += on => { _shiny = on; Refresh(); };
        var back = new CheckButton { Text = "Back sprite" };
        back.Toggled += on => { _back = on; Refresh(); };

        _name = Ui.Text("", 30, Ui.Accent);
        _info = Ui.Text("", 16, wrap: true);
        _stats = Ui.VBox(4);

        var spritePanel = new PanelContainer();
        spritePanel.AddChild(_sprite);
        var details = Ui.VBox(10, _name, _info, _stats);
        details.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var right = Ui.HBox(24, Ui.VBox(10, spritePanel, Ui.HBox(16, shiny, back)), details);
        right.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var content = Ui.HBox(24, left, right);
        var page = Page.Create("Pokédex Viewer", content,
            $"Read from your {ClientServices.Manifest.FindById(assets.Stamp.GameId)!.Name} ROM · {_species.Count} species");
        AddChild(page);
        page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        Filter("");
        if (_list.ItemCount > 0)
        {
            _list.Select(0);
            Show(_species[0]);
        }
    }

    void Filter(string query)
    {
        _list.Clear();
        _shown.Clear();
        query = query.Trim();
        foreach (var s in _species)
        {
            if (query.Length > 0 && !s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && !s.Id.ToString().Contains(query))
                continue;
            _list.AddItem($"#{s.Id:D3}  {s.Name}");
            _shown.Add(s);
        }
    }

    void Show(SpeciesInfo s)
    {
        _current = s;
        Refresh();
    }

    void Refresh()
    {
        if (_current is not { } s || ClientServices.UnovaAssets is not { } assets)
            return;

        var view = (_back ? "back" : "front") + (_shiny ? "-shiny" : "");
        var image = Image.LoadFromFile(assets.BattleSpritePath(s.Id, view));
        _sprite.Texture = image is null ? null : ImageTexture.CreateFromImage(image);

        _name.Text = $"#{s.Id:D3}  {s.Name}";
        var gender = s.GenderRatio switch
        {
            255 => "Genderless",
            254 => "Always female",
            0 => "Always male",
            _ => $"{100 - s.GenderRatio * 100 / 254}% male",
        };
        _info.Text =
            $"Type: {string.Join(" / ", s.Types)}\n" +
            $"Abilities: {string.Join(", ", s.Abilities)}{(s.HiddenAbility is { } h ? $"  ·  Hidden: {h}" : "")}\n" +
            $"Height {s.HeightDecimetres / 10.0:0.0} m  ·  Weight {s.WeightHectograms / 10.0:0.0} kg  ·  {gender}\n" +
            $"Catch rate {s.CatchRate}  ·  Base exp {s.BaseExp}  ·  Egg cycles {s.HatchCycles}";

        Ui.ClearChildren(_stats);
        foreach (var (label, value) in new[]
                 {
                     ("HP", s.Stats.Hp), ("Attack", s.Stats.Attack), ("Defense", s.Stats.Defense),
                     ("Sp. Atk", s.Stats.SpAttack), ("Sp. Def", s.Stats.SpDefense), ("Speed", s.Stats.Speed),
                 })
            _stats.AddChild(StatRow(label, value));
        _stats.AddChild(Ui.Text($"Total {s.Stats.Total}", 15, Ui.Muted));
    }

    static Control StatRow(string label, int value)
    {
        var name = Ui.Text(label, 15);
        name.CustomMinimumSize = new Vector2(80, 0);
        var number = Ui.Text(value.ToString(), 15);
        number.CustomMinimumSize = new Vector2(40, 0);
        var bar = new ProgressBar
        {
            MaxValue = 255, Value = value, ShowPercentage = false,
            CustomMinimumSize = new Vector2(260, 12), SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        var hue = Mathf.Clamp(value / 180f, 0, 1) * 0.33f;
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = Color.FromHsv(hue, 0.65f, 0.9f) });
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.08f) });
        return Ui.HBox(10, name, number, bar);
    }
}
