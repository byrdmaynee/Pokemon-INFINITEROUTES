using Godot;

namespace InfiniteRoutes.Client.Ui;

/// <summary>Login form layout. Accounts and the server arrive in Milestone 2.</summary>
public partial class LoginScreen : Control
{
    public override void _Ready()
    {
        var server = new OptionButton();
        server.AddItem("Local test server (127.0.0.1:7450)");

        var username = new LineEdit { PlaceholderText = "Username or email", CustomMinimumSize = new Vector2(360, 0) };
        var password = new LineEdit { PlaceholderText = "Password", Secret = true };
        var remember = new CheckBox { Text = "Remember username" };

        var login = Ui.Button("Log in", () => Main.Instance.Toast("Online play arrives in Milestone 2."), 18);
        password.TextSubmitted += _ => login.EmitSignal(BaseButton.SignalName.Pressed);

        var form = Ui.VBox(12,
            Ui.Text("Server", 15, Ui.Muted), server,
            Ui.Text("Account", 15, Ui.Muted), username, password, remember,
            Ui.HBox(12,
                login,
                Ui.Button("Create account", () => Main.Instance.Toast("Account creation arrives in Milestone 2.")),
                Ui.Button("Forgot password", () => Main.Instance.Toast("Password reset arrives in Milestone 2."))));
        form.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;

        var page = Page.Create("Play Online", form, "Layout preview — nothing is sent anywhere yet.");
        AddChild(page);
        page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        username.CallDeferred(Control.MethodName.GrabFocus);
    }
}
