using Godot;

namespace InfiniteRoutes.Client.Ui;

/// <summary>Server list layout (BUILD_SPEC §4.5). Saving to servers.pkir arrives with the server in M2.</summary>
public partial class ServersScreen : Control
{
    public override void _Ready()
    {
        var row = new PanelContainer();
        row.AddChild(Ui.Margin(Ui.HBox(16,
            Ui.VBox(2,
                Ui.Text("Local test server", 18),
                Ui.Text("127.0.0.1:7450 · self-signed", 14, Ui.Muted)),
            Ui.Spacer(),
            Ui.Text("offline", 16, Ui.Muted),
            Ui.Button("Connect", () => Main.Instance.Toast("The game server arrives in Milestone 2."))), 12));

        var content = Ui.VBox(12,
            row,
            Ui.HBox(12,
                Ui.Button("Add server…", () => Main.Instance.Toast("Adding servers arrives in Milestone 2.")),
                Ui.Button("Direct connect…", () => Main.Instance.Toast("Direct connect arrives in Milestone 2."))));

        var page = Page.Create("Servers", content, "Servers you add will be saved in servers.pkir.");
        AddChild(page);
        page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }
}
