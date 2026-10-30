using Godot;

namespace InfiniteRoutes.Client.Ui;

/// <summary>
/// Settings tabs. ROM Library is fully working; window mode, vsync, master volume and UI scale
/// apply immediately but aren't saved between runs yet.
/// </summary>
public partial class SettingsScreen : Control
{
    public override void _Ready()
    {
        var tabs = new TabContainer();
        AddTab(tabs, "ROM Library", new RomLibraryPanel());
        AddTab(tabs, "Graphics", Graphics());
        AddTab(tabs, "Audio", Audio());
        AddTab(tabs, "Controls", Controls());
        AddTab(tabs, "Accessibility", Accessibility());

        var page = Page.Create("Settings", tabs, "Changes apply right away but aren't saved between runs yet (except the ROM Library).");
        AddChild(page);
        page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    static void AddTab(TabContainer tabs, string name, Control content)
    {
        var margin = Ui.Margin(content, 16);
        margin.Name = name;
        tabs.AddChild(margin);
    }

    static Control Row(string label, Control control)
    {
        var l = Ui.Text(label, 16);
        l.CustomMinimumSize = new Vector2(220, 0);
        return Ui.HBox(16, l, control);
    }

    static Control Graphics()
    {
        var mode = new OptionButton();
        mode.AddItem("Windowed");
        mode.AddItem("Fullscreen");
        mode.AddItem("Exclusive fullscreen");
        mode.Selected = DisplayServer.WindowGetMode() switch
        {
            DisplayServer.WindowMode.Fullscreen => 1,
            DisplayServer.WindowMode.ExclusiveFullscreen => 2,
            _ => 0,
        };
        mode.ItemSelected += i => DisplayServer.WindowSetMode(i switch
        {
            1 => DisplayServer.WindowMode.Fullscreen,
            2 => DisplayServer.WindowMode.ExclusiveFullscreen,
            _ => DisplayServer.WindowMode.Windowed,
        });

        var vsync = new CheckBox { Text = "Enabled", ButtonPressed = DisplayServer.WindowGetVsyncMode() != DisplayServer.VSyncMode.Disabled };
        vsync.Toggled += on => DisplayServer.WindowSetVsyncMode(on ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);

        var fps = new OptionButton { Disabled = true, TooltipText = "Coming later." };
        fps.AddItem("Unlimited");

        return Ui.VBox(12, Row("Window mode", mode), Row("V-Sync", vsync), Row("Frame cap", fps));
    }

    static Control Audio()
    {
        var master = Slider(Mathf.DbToLinear(AudioServer.GetBusVolumeDb(0)),
            v => AudioServer.SetBusVolumeDb(0, Mathf.LinearToDb((float)Math.Max(v, 0.0001))));
        var music = Slider(0.8, _ => { });
        music.Editable = false;
        var sfx = Slider(0.8, _ => { });
        sfx.Editable = false;

        return Ui.VBox(12,
            Row("Master volume", master),
            Row("Music (no music yet)", music),
            Row("Sound effects (none yet)", sfx));
    }

    static Control Controls()
    {
        var box = Ui.VBox(8, Ui.Text("Default keys (rebinding comes later)", 16, Ui.Muted));
        foreach (var (action, keys) in new[]
                 {
                     ("Move", "Arrow keys / WASD"), ("Confirm / interact", "Enter / Z / Space"),
                     ("Cancel / back", "Esc / X"), ("Run (hold)", "Shift"), ("Menu", "Tab"),
                     ("Chat", "T"), ("Admin mode (staff)", "F10"),
                 })
            box.AddChild(Row(action, Ui.Text(keys, 16, Ui.Muted)));
        return box;
    }

    Control Accessibility()
    {
        var scale = new HSlider
        {
            MinValue = 0.75, MaxValue = 2.0, Step = 0.05, Value = GetTree().Root.ContentScaleFactor,
            CustomMinimumSize = new Vector2(280, 0),
        };
        var label = Ui.Text($"{scale.Value:P0}", 16);
        // Apply on release so the slider doesn't jump under the cursor while dragging.
        scale.ValueChanged += v => label.Text = $"{v:P0}";
        scale.DragEnded += _ => GetTree().Root.ContentScaleFactor = (float)scale.Value;

        var textSpeed = new OptionButton();
        foreach (var s in new[] { "Slow", "Normal", "Fast", "Instant" })
            textSpeed.AddItem(s);
        textSpeed.Selected = 2;

        return Ui.VBox(12,
            Row("UI scale", Ui.HBox(12, scale, label)),
            Row("Text speed", textSpeed),
            Row("Reduced motion", new CheckBox { Text = "Enabled" }));
    }

    static HSlider Slider(double value, Action<double> onChanged)
    {
        var s = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.01, Value = value, CustomMinimumSize = new Vector2(280, 0) };
        s.ValueChanged += v => onChanged(v);
        return s;
    }
}
