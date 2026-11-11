using Godot;
using InfiniteRoutes.Client.Online;

namespace InfiniteRoutes.Client.Ui;

/// <summary>
/// The account on this server: its staff role, and two-factor setup (BUILD_SPEC §11.1) — the secret to put in an
/// authenticator app (Google Authenticator, Microsoft Authenticator, 1Password, …), then a code from it to turn it on.
/// Staff need it to enter Admin Mode.
/// </summary>
public partial class AccountScreen : Control
{
    VBoxContainer _body = null!;
    Label _status = null!;

    public override void _Ready()
    {
        _body = Ui.VBox(12);
        _status = Ui.Text("", 15, Ui.Muted, wrap: true);
        _status.CustomMinimumSize = new Vector2(620, 0);
        var page = Page.Create($"Account — {OnlineSession.Client?.Account}", Ui.VBox(14, _body, _status));
        AddChild(page);
        page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Load();
    }

    async void Load()
    {
        if (OnlineSession.Client is not { } client)
            return;
        var (info, error) = await client.AccountInfo();
        if (!IsInstanceValid(this))
            return;
        Ui.ClearChildren(_body);
        if (info is null)
        {
            _status.Text = error ?? "";
            return;
        }
        _body.AddChild(Ui.Text($"Role: {info.Role}", 18));
        _body.AddChild(Ui.Text(info.TwoFactor ? "Two-factor: on" : "Two-factor: off", 18, info.TwoFactor ? Ui.Good : Ui.Muted));
        if (info.Role != "Player")
            _body.AddChild(Ui.Text(info.Require2fa
                ? "Admin Mode on this server needs two-factor. Once it's on, type /admin in the game."
                : "Type /admin in the game to enter Admin Mode.", 15, Ui.Muted, wrap: true));
        if (!info.TwoFactor)
            _body.AddChild(Ui.Button("Set up two-factor", SetUp, 17));
    }

    async void SetUp()
    {
        if (OnlineSession.Client is not { } client)
            return;
        var (secret, uri, error) = await client.SetupTwoFactor();
        if (!IsInstanceValid(this))
            return;
        if (secret is null)
        {
            _status.Text = error ?? "";
            return;
        }
        Ui.ClearChildren(_body);
        var secretField = new LineEdit { Text = secret, Editable = false, CustomMinimumSize = new Vector2(520, 0) };
        var uriField = new LineEdit { Text = uri, Editable = false };
        var code = new LineEdit { PlaceholderText = "6-digit code from the app", CustomMinimumSize = new Vector2(220, 0) };
        async void Confirm()
        {
            var problem = await client.ConfirmTwoFactor(code.Text);
            if (!IsInstanceValid(this))
                return;
            _status.Text = problem ?? "Two-factor is on.";
            if (problem is null)
                Load();
        }
        code.TextSubmitted += _ => Confirm();
        _body.AddChild(Ui.Text("1. In your authenticator app, add an account by entering this key (time-based):", 15, wrap: true));
        _body.AddChild(Ui.HBox(8, secretField, Ui.Button("Copy", () => DisplayServer.ClipboardSet(secret))));
        _body.AddChild(Ui.Text("…or paste this link into an app that takes otpauth links:", 14, Ui.Muted));
        _body.AddChild(Ui.HBox(8, uriField, Ui.Button("Copy", () => DisplayServer.ClipboardSet(uri ?? ""))));
        _body.AddChild(Ui.Text("2. Type the code it shows:", 15));
        _body.AddChild(Ui.HBox(8, code, Ui.Button("Turn on", Confirm)));
        code.GrabFocus();
    }
}
