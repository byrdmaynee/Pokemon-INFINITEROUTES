using Godot;

namespace InfiniteRoutes.Client.Ui;

public partial class CreditsScreen : Control
{
    public override void _Ready()
    {
        var content = Ui.VBox(14,
            Ui.Text($"{ClientInfo.Title}  ·  v{ClientInfo.Version}", 20),
            Ui.Text("An unofficial fan project. Made by Rodney Hall(bYRD) and his company TalonWingForge.com. Infinite Routes ships no game graphics, maps, music or text: " +
                    "it reads them on your computer from DS games you own.", 16, wrap: true),
            Ui.Text("Pokémon and all related names are trademarks of Nintendo, Game Freak and The Pokémon Company. " +
                    "This project is not affiliated with or endorsed by them.", 15, Ui.Muted, wrap: true),
            Ui.Text("Built with Godot Engine (MIT licence) and Tomlyn (BSD-2-Clause).", 15, Ui.Muted, wrap: true));

        var page = Page.Create("Credits & Legal", content);
        AddChild(page);
        page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }
}
