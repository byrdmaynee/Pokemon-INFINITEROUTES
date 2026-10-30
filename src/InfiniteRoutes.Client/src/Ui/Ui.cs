using Godot;

namespace InfiniteRoutes.Client.Ui;

/// <summary>Small helpers for building UI in code until there's a proper theme.</summary>
public static class Ui
{
    public static readonly Color Good = new(0.45f, 0.85f, 0.5f);
    public static readonly Color Warn = new(0.95f, 0.75f, 0.3f);
    public static readonly Color Bad = new(0.95f, 0.4f, 0.4f);
    public static readonly Color Muted = new(0.65f, 0.68f, 0.72f);
    public static readonly Color Accent = new(0.95f, 0.76f, 0.31f);

    public static Label Text(string text, int size = 16, Color? color = null, bool wrap = false)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", size);
        if (color is { } c)
            label.AddThemeColorOverride("font_color", c);
        if (wrap)
        {
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        }
        return label;
    }

    public static Button Button(string text, Action onPressed, int size = 16)
    {
        var button = new Button { Text = text };
        button.AddThemeFontSizeOverride("font_size", size);
        button.Pressed += onPressed;
        return button;
    }

    public static MarginContainer Margin(Control child, int all)
    {
        var m = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            m.AddThemeConstantOverride(side, all);
        m.AddChild(child);
        return m;
    }

    public static VBoxContainer VBox(int separation = 8, params Control[] children)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", separation);
        foreach (var c in children)
            box.AddChild(c);
        return box;
    }

    public static HBoxContainer HBox(int separation = 8, params Control[] children)
    {
        var box = new HBoxContainer();
        box.AddThemeConstantOverride("separation", separation);
        foreach (var c in children)
            box.AddChild(c);
        return box;
    }

    public static Control Spacer()
    {
        var c = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        return c;
    }

    public static void ClearChildren(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }
}
