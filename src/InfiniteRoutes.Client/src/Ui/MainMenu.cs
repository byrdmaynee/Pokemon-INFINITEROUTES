using Godot;

namespace InfiniteRoutes.Client.Ui;

/// <summary>Title screen. Entries that aren't built yet open placeholder screens or show a toast.</summary>
public partial class MainMenu : MarginContainer
{
    public override void _Ready()
    {
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
            AddThemeConstantOverride(side, 56);

        var title = Ui.Text(ClientInfo.Title, 52, Ui.Accent);
        var subtitle = Ui.Text("One world. Every route.", 20, Ui.Muted);

        var buttons = Ui.VBox(10,
            MenuButton("Play Online", () => Main.Instance.Push(() => new LoginScreen())),
            MenuButton("Offline Test World", () => Main.Instance.Toast("The offline test world arrives later in Milestone 1.")),
            MenuButton("Pokédex Viewer", () => Main.Instance.Push(() => new PokedexScreen())),
            MenuButton("Settings", () => Main.Instance.Push(() => new SettingsScreen())),
            MenuButton("Servers", () => Main.Instance.Push(() => new ServersScreen())),
            MenuButton("Credits & Legal", () => Main.Instance.Push(() => new CreditsScreen())),
            MenuButton("Quit", ConfirmQuit));

        var left = Ui.VBox(28, title, subtitle, buttons);
        left.SizeFlagsVertical = SizeFlags.ShrinkCenter;

        var footer = Ui.HBox(24,
            Ui.Text($"v{ClientInfo.Version} · {ClientInfo.Milestone}", 14, Ui.Muted),
            Ui.Spacer(),
            Ui.Text(RegionSummary(), 14, ClientServices.Library.CanPlay ? Ui.Muted : Ui.Warn),
            Ui.Text("Server: offline", 14, Ui.Muted));

        var layout = Ui.VBox(0, Ui.Spacer(), left, Ui.Spacer(), footer);
        AddChild(layout);

        buttons.GetChild<Button>(0).CallDeferred(Control.MethodName.GrabFocus);
    }

    static Button MenuButton(string text, Action onPressed)
    {
        var b = Ui.Button(text, onPressed, 22);
        b.CustomMinimumSize = new Vector2(320, 48);
        b.Alignment = HorizontalAlignment.Left;
        b.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        return b;
    }

    static string RegionSummary()
    {
        var regions = ClientServices.Library.Regions().Where(r => r.Region.InBaseGame).ToList();
        if (!ClientServices.Library.CanPlay)
            return "Black or White missing — see Settings → ROM Library";
        return "Regions: " + string.Join("  ", regions.Select(r => (r.Available ? "✓ " : "🔒 ") + r.Region.Name));
    }

    void ConfirmQuit()
    {
        var dialog = new ConfirmationDialog { DialogText = "Quit Infinite Routes?", Title = "Quit" };
        dialog.OkButtonText = "Quit";
        dialog.Confirmed += () => GetTree().Quit();
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered();
    }
}
