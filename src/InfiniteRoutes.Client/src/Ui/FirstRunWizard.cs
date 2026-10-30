using Godot;

namespace InfiniteRoutes.Client.Ui;

/// <summary>Welcome → legal notice → ROM Library → summary (BUILD_SPEC §2.3).</summary>
public partial class FirstRunWizard : MarginContainer
{
    readonly Action _onFinished;
    readonly Control _pageHost = new() { SizeFlagsVertical = SizeFlags.ExpandFill };
    Button _back = null!;
    Button _next = null!;
    Label _hint = null!;
    Label _stepLabel = null!;
    int _page;
    bool _ownsGamesConfirmed;

    const int PageCount = 4;

    public FirstRunWizard(Action onFinished) => _onFinished = onFinished;

    /// <summary>For the engine (script reload); screens are always created with the other constructor.</summary>
    public FirstRunWizard() : this(() => { }) { }

    public override void _Ready()
    {
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            AddThemeConstantOverride(side, 40);

        _stepLabel = Ui.Text("", 14, Ui.Muted);
        _back = Ui.Button("Back", () => GoTo(_page - 1));
        _next = Ui.Button("Next", Next);
        _hint = Ui.Text("", 14, Ui.Warn, wrap: true);

        var nav = Ui.HBox(12, _hint, _back, _next);
        AddChild(Ui.VBox(20, _stepLabel, _pageHost, nav));
        GoTo(0);
    }

    void Next()
    {
        if (_page == PageCount - 1)
        {
            ClientServices.Library.WizardCompleted = true;
            ClientServices.SaveLibrary();
            _onFinished();
            return;
        }
        GoTo(_page + 1);
    }

    void GoTo(int page)
    {
        _page = Math.Clamp(page, 0, PageCount - 1);
        Ui.ClearChildren(_pageHost);
        var content = _page switch
        {
            0 => WelcomePage(),
            1 => LegalPage(),
            2 => RomPage(),
            _ => SummaryPage(),
        };
        _pageHost.AddChild(content);
        content.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _stepLabel.Text = $"Setup · step {_page + 1} of {PageCount}";
        _back.Visible = _page > 0;
        _next.Text = _page == PageCount - 1 ? "Finish" : "Next";
        UpdateNav();
    }

    void UpdateNav()
    {
        (_next.Disabled, _hint.Text) = _page switch
        {
            1 when !_ownsGamesConfirmed => (true, "Tick the box to continue."),
            2 when !ClientServices.Library.CanPlay => (true, "Pokémon Black or White is required — every battle uses it."),
            _ => (false, ""),
        };
    }

    static Control WelcomePage() => Ui.VBox(16,
        Ui.Text("Pokémon: Infinite Routes", 40, Ui.Accent),
        Ui.Text("An online world joining Unova, Sinnoh, Johto and Kanto.", 20),
        Ui.Text(
            "Infinite Routes doesn't ship any game graphics, maps, music or text. Instead it reads them from your own " +
            "copies of the Nintendo DS Pokémon games. The next steps will ask you where those ROM files are. " +
            "Nothing from them is ever uploaded or shared.",
            17, wrap: true));

    Control LegalPage()
    {
        var check = new CheckBox { Text = "I own the games I'm going to use with Infinite Routes.", ButtonPressed = _ownsGamesConfirmed };
        check.AddThemeFontSizeOverride("font_size", 17);
        check.Toggled += on =>
        {
            _ownsGamesConfirmed = on;
            UpdateNav();
        };

        return Ui.VBox(16,
            Ui.Text("Before you continue", 28, Ui.Accent),
            Ui.Text(
                "You may only use ROM files dumped from cartridges you own. Infinite Routes never links to, hosts, or " +
                "helps you get ROMs, and it reads them only on this computer.",
                17, wrap: true),
            Ui.Text("Pokémon and all related names are trademarks of their respective owners. Infinite Routes is an unofficial fan project.",
                14, Ui.Muted, wrap: true),
            check);
    }

    Control RomPage()
    {
        var panel = new RomLibraryPanel();
        panel.LibraryChanged += UpdateNav;
        return Ui.VBox(12,
            Ui.Text("Your games", 28, Ui.Accent),
            Ui.Text(
                "Black or White is required. Platinum unlocks Sinnoh; HeartGold or SoulSilver unlocks Johto & Kanto. " +
                "Use “Scan folder…” to find them all at once, or “Browse…” per game. You can change these later in Settings.",
                16, wrap: true),
            panel);
    }

    static Control SummaryPage()
    {
        var box = Ui.VBox(10, Ui.Text("You're set", 28, Ui.Accent));
        foreach (var r in ClientServices.Library.Regions().Where(r => r.Region.InBaseGame))
            box.AddChild(Ui.Text(
                r.Available ? $"✓  {r.Region.Name}" : $"🔒  {r.Region.Name} — add its game in Settings → ROM Library to unlock",
                18, r.Available ? Ui.Good : Ui.Muted));
        box.AddChild(Ui.Text(
            "In the full game the next step extracts graphics, maps and music from your ROMs into a local cache. " +
            "That arrives in Milestone 1.",
            15, Ui.Muted, wrap: true));
        return box;
    }
}
