using Godot;
using InfiniteRoutes.Client.Ui;
using InfiniteRoutes.Core;
using InfiniteRoutes.Core.Config;

namespace InfiniteRoutes.Client;

/// <summary>Root node: loads client state and swaps between top-level screens.</summary>
public partial class Main : Control
{
    public override void _Ready()
    {
        try
        {
            ClientServices.Initialize();
        }
        catch (PkirException e)
        {
            ShowScreen(BrokenConfigScreen(e.Message));
            return;
        }

        if (ClientServices.Library.WizardCompleted)
            ShowHome();
        else
            ShowScreen(new FirstRunWizard(ShowHome));
    }

    void ShowHome() => ShowScreen(new HomeScreen(ShowScreen, () => ShowHome()));

    void ShowScreen(Control screen)
    {
        Ui.Ui.ClearChildren(this);
        AddChild(screen);
        screen.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    Control BrokenConfigScreen(string error)
    {
        var box = Ui.Ui.VBox(16,
            Ui.Ui.Text("Your ROM library settings couldn't be read", 26, Ui.Ui.Bad),
            Ui.Ui.Text($"File: {AppPaths.RomLibraryFile}", 14, Ui.Ui.Muted, wrap: true),
            Ui.Ui.Text(error, 14, wrap: true),
            Ui.Ui.Text("Fix the file in a text editor and restart, or start over. Starting over keeps a backup of the old file.", 16, wrap: true),
            Ui.Ui.HBox(8,
                Ui.Ui.Button("Start over", () =>
                {
                    ClientServices.BackUpBrokenLibrary();
                    _Ready();
                }),
                Ui.Ui.Button("Quit", () => GetTree().Quit())));
        return Ui.Ui.Margin(box, 48);
    }
}
