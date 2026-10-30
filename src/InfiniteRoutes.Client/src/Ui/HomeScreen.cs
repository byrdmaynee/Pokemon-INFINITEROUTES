using Godot;

namespace InfiniteRoutes.Client.Ui;

/// <summary>Placeholder home screen until login (M2): region status and access to the ROM Library.</summary>
public partial class HomeScreen : MarginContainer
{
    readonly Action<Control> _showScreen;
    readonly Action _showHome;

    public HomeScreen(Action<Control> showScreen, Action showHome)
    {
        _showScreen = showScreen;
        _showHome = showHome;
    }

    /// <summary>For the engine (script reload); screens are always created with the other constructor.</summary>
    public HomeScreen() : this(_ => { }, () => { }) { }

    public override void _Ready()
    {
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            AddThemeConstantOverride(side, 48);

        var library = ClientServices.Library;
        var box = Ui.VBox(14,
            Ui.Text("Pokémon: Infinite Routes", 44, Ui.Accent),
            Ui.Text("Development build · Milestone 0", 15, Ui.Muted));

        if (!library.CanPlay)
            box.AddChild(Ui.Text("Pokémon Black or White is missing or needs attention. Open the ROM Library to fix it.", 17, Ui.Warn, wrap: true));

        var regions = Ui.VBox(6, Ui.Text("Regions", 22));
        foreach (var r in library.Regions().Where(r => r.Region.InBaseGame))
            regions.AddChild(Ui.Text(r.Available ? $"✓  {r.Region.Name}" : $"🔒  {r.Region.Name}", 18, r.Available ? Ui.Good : Ui.Muted));
        box.AddChild(regions);

        var play = Ui.Button("Play online", () => { }, 20);
        play.Disabled = true;
        play.TooltipText = "Login and the online world arrive in Milestone 2.";

        box.AddChild(Ui.HBox(12,
            play,
            Ui.Button("ROM Library", OpenRomLibrary, 20),
            Ui.Button("Quit", () => GetTree().Quit(), 20)));
        AddChild(box);
    }

    void OpenRomLibrary()
    {
        var panel = new RomLibraryPanel();
        var page = Ui.VBox(12,
            Ui.HBox(12, Ui.Button("← Back", _showHome), Ui.Text("Settings · ROM Library", 26, Ui.Accent)),
            panel);
        _showScreen(Ui.Margin(page, 32));
    }
}
