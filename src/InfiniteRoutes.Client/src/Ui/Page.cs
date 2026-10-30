using Godot;

namespace InfiniteRoutes.Client.Ui;

/// <summary>Standard sub-screen frame: "← Back" + title on top, content filling the rest.</summary>
public static class Page
{
    public static Control Create(string title, Control content, string? note = null)
    {
        var back = Ui.Button("← Back", () => Main.Instance.Back());
        var header = Ui.HBox(16, back, Ui.Text(title, 30, Ui.Accent));
        var box = Ui.VBox(16, header);
        if (note is not null)
            box.AddChild(Ui.Text(note, 14, Ui.Muted, wrap: true));
        content.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        box.AddChild(content);

        var panel = new PanelContainer();
        panel.AddChild(Ui.Margin(box, 24));
        back.CallDeferred(Control.MethodName.GrabFocus);
        return Ui.Margin(panel, 40);
    }
}
