using Godot;
using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.Ui;

/// <summary>
/// The pieces the party and Bag screens are built from: a Pokémon's party icon from the ROM, type and status badges,
/// an HP bar, item icons, and a card that turns any of them into one clickable row.
/// </summary>
public static class MonUi
{
    static readonly Dictionary<string, Texture2D?> Textures = new();

    /// <summary>The ROM's 32×32 icon at <paramref name="path"/>, scaled up crisply, or null when it isn't extracted.</summary>
    public static Texture2D? Texture(string path)
    {
        if (Textures.TryGetValue(path, out var cached))
            return cached;
        Texture2D? texture = null;
        if (File.Exists(path) && Image.LoadFromFile(path) is { } image)
            texture = ImageTexture.CreateFromImage(image);
        return Textures[path] = texture;
    }

    static TextureRect Picture(Texture2D? texture, int size) => new()
    {
        Texture = texture, CustomMinimumSize = new Vector2(size, size), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    /// <summary>A Pokémon's party icon (a ✦ marks a shiny one, since the icons have no shiny colours).</summary>
    public static Control Avatar(GameAssets? assets, int species, bool shiny = false, int size = 64)
    {
        var box = new Control { CustomMinimumSize = new Vector2(size, size), MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(Picture(assets is null ? null : Texture(assets.PartyIconPath(species)), size));
        if (shiny)
            box.AddChild(Ui.Text("✦", 14, new Color(1, 0.85f, 0.3f)));
        return box;
    }

    public static Control ItemIcon(GameAssets? assets, int item, int size = 32) =>
        Picture(assets is null || item <= 0 ? null : Texture(assets.ItemIconPath(item)), size);

    public static Texture2D? ItemTexture(GameAssets? assets, int item) => assets is null || item <= 0 ? null : Texture(assets.ItemIconPath(item));

    /// <summary>The games' type colours.</summary>
    public static Color TypeColor(string type) => type switch
    {
        "Normal" => new(0.66f, 0.66f, 0.47f), "Fire" => new(0.94f, 0.5f, 0.19f), "Water" => new(0.41f, 0.56f, 0.94f),
        "Electric" => new(0.97f, 0.82f, 0.19f), "Grass" => new(0.47f, 0.78f, 0.31f), "Ice" => new(0.6f, 0.85f, 0.85f),
        "Fighting" => new(0.75f, 0.19f, 0.16f), "Poison" => new(0.63f, 0.25f, 0.63f), "Ground" => new(0.88f, 0.75f, 0.41f),
        "Flying" => new(0.66f, 0.56f, 0.94f), "Psychic" => new(0.97f, 0.35f, 0.53f), "Bug" => new(0.66f, 0.72f, 0.13f),
        "Rock" => new(0.72f, 0.63f, 0.22f), "Ghost" => new(0.44f, 0.35f, 0.6f), "Dragon" => new(0.44f, 0.22f, 0.97f),
        "Dark" => new(0.44f, 0.35f, 0.28f), "Steel" => new(0.72f, 0.72f, 0.82f),
        _ => new(0.5f, 0.5f, 0.5f),
    };

    /// <summary>A rounded badge with white capitals, like the games' type and status labels.</summary>
    public static Control Badge(string text, Color color, int size = 12)
    {
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = color, BorderColor = color.Darkened(0.35f), BorderWidthBottom = 2, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 7, ContentMarginRight = 7, ContentMarginTop = 1, ContentMarginBottom = 1,
        });
        var label = Ui.Text(text.ToUpperInvariant(), size, Colors.White);
        label.AddThemeConstantOverride("outline_size", 3);
        label.AddThemeColorOverride("font_outline_color", color.Darkened(0.55f));
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        panel.AddChild(label);
        panel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        return panel;
    }

    public static Control TypeBadge(string type) => Badge(type, TypeColor(type));

    /// <summary>The status label shown beside a Pokémon (PSN, PAR …), or null when it's healthy.</summary>
    public static Control? StatusBadge(Ailment status, bool fainted) =>
        fainted ? Badge("FNT", new Color(0.55f, 0.15f, 0.15f))
        : status switch
        {
            Ailment.Poison => Badge("PSN", new Color(0.63f, 0.25f, 0.63f)),
            Ailment.Burn => Badge("BRN", new Color(0.94f, 0.4f, 0.15f)),
            Ailment.Paralysis => Badge("PAR", new Color(0.85f, 0.7f, 0.1f)),
            Ailment.Sleep => Badge("SLP", new Color(0.5f, 0.5f, 0.6f)),
            Ailment.Freeze => Badge("FRZ", new Color(0.35f, 0.75f, 0.9f)),
            _ => null,
        };

    public static string StatusName(Ailment status) => status switch
    {
        Ailment.Poison => "Poisoned", Ailment.Burn => "Burned", Ailment.Paralysis => "Paralyzed", Ailment.Sleep => "Asleep",
        Ailment.Freeze => "Frozen", _ => "",
    };

    /// <summary>An HP bar, green above half, yellow above a fifth, red below.</summary>
    public static Control HpBar(int hp, int max, int width = 180)
    {
        var fraction = max > 0 ? Math.Clamp(hp / (float)max, 0, 1) : 0;
        var color = fraction > 0.5f ? new Color(0.3f, 0.85f, 0.4f) : fraction > 0.2f ? new Color(0.95f, 0.8f, 0.2f) : new Color(0.9f, 0.3f, 0.25f);
        var bar = new ProgressBar
        {
            MaxValue = Math.Max(1, max), Value = hp, ShowPercentage = false, CustomMinimumSize = new Vector2(width, 10),
            MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = color, CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 });
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0.12f, 0.13f, 0.16f), CornerRadiusTopLeft = 4, CornerRadiusTopRight = 4, CornerRadiusBottomLeft = 4, CornerRadiusBottomRight = 4 });
        var label = Ui.Text($"HP {hp}/{max}", 14, Ui.Muted);
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        return Row(8, label, bar);
    }

    static HBoxContainer Row(int separation, params Control[] children)
    {
        var row = Ui.HBox(separation, children);
        row.MouseFilter = Control.MouseFilterEnum.Ignore;
        return row;
    }

    /// <summary>
    /// A Pokémon's card: icon, name with gender and level, type badges, status badge and held item, HP bar, and an
    /// optional extra line (moves, stats).
    /// </summary>
    public static Control Card(GameAssets? assets, Pokemon mon, string? extra = null, string? heldName = null, bool lead = false, bool buddy = false)
    {
        var gender = mon.Gender switch { Gender.Male => " ♂", Gender.Female => " ♀", _ => "" };
        var title = Row(8, Ui.Text($"{(buddy ? "♥ " : "")}{mon.Name}{gender}", 19, lead ? Ui.Accent : null), Ui.Text($"Lv {mon.Level}", 16, Ui.Muted));
        foreach (var type in mon.Species.Types.Distinct())
            title.AddChild(TypeBadge(type));
        if (StatusBadge(mon.Status, mon.Fainted) is { } status)
        {
            title.AddChild(status);
            if (!mon.Fainted && StatusName(mon.Status) is { Length: > 0 } name)
                title.AddChild(Ui.Text(name, 14, Ui.Warn));
        }
        if (mon.HeldItem != 0)
        {
            var held = Row(4, ItemIcon(assets, mon.HeldItem, 24), Ui.Text(heldName ?? $"Item {mon.HeldItem}", 14, Ui.Muted));
            title.AddChild(held);
        }
        var lines = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        lines.AddThemeConstantOverride("separation", 3);
        lines.AddChild(title);
        lines.AddChild(HpBar(mon.Hp, mon.MaxHp));
        if (!string.IsNullOrEmpty(extra))
        {
            var text = Ui.Text(extra, 13, Ui.Muted, wrap: true);
            text.MouseFilter = Control.MouseFilterEnum.Ignore;
            lines.AddChild(text);
        }
        lines.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return Row(10, Avatar(assets, mon.Species.Id, mon.Shiny), lines);
    }

    /// <summary>Makes <paramref name="content"/> one clickable button-styled row.</summary>
    public static Button Clickable(Control content, Action pressed, bool disabled = false)
    {
        var button = new Button { Disabled = disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", side is "left" or "right" ? 10 : 6);
        margin.AddChild(content);
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        button.AddChild(margin);
        button.Pressed += pressed;
        // Size the button to what it holds.
        content.Resized += () => button.CustomMinimumSize = new Vector2(0, content.Size.Y + 12);
        button.Ready += () => button.CustomMinimumSize = new Vector2(0, Math.Max(76, content.GetCombinedMinimumSize().Y + 12));
        return button;
    }
}
