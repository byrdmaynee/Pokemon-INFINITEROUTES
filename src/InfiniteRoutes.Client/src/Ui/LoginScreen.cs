using Godot;
using InfiniteRoutes.Client.Online;
using InfiniteRoutes.Core.Config;

namespace InfiniteRoutes.Client.Ui;

/// <summary>
/// Play Online: pick a server, then log in or create an account. The first connection to a server shows its
/// certificate fingerprint to trust; after that a different certificate is refused.
/// </summary>
public partial class LoginScreen(ServerEntry? preselected) : Control
{
    OptionButton _server = null!;
    LineEdit _username = null!, _password = null!;
    Label _status = null!;
    Control _form = null!;
    bool _busy;

    // Godot needs a parameterless constructor to recreate script instances.
    public LoginScreen() : this(null) { }

    /// <summary>Fills in the form and presses a button; for smoke tests.</summary>
    public void Submit(string name, string password, bool register)
    {
        _username.Text = name;
        _password.Text = password;
        Go(register);
    }

    /// <summary>The trust prompt, while it's up; for smoke tests.</summary>
    public Action? TrustPending { get; private set; }

    public string Status => _status.Text;

    public override void _Ready()
    {
        OnlineSession.LoadServers();
        _server = new OptionButton();
        foreach (var s in OnlineSession.Servers)
            _server.AddItem($"{s.Name} ({s.Address})");
        if (preselected is not null && OnlineSession.Servers.IndexOf(preselected) is var i and >= 0)
            _server.Selected = i;

        _username = new LineEdit { PlaceholderText = "Account name", CustomMinimumSize = new Vector2(360, 0) };
        _password = new LineEdit { PlaceholderText = "Password (8 characters or more)", Secret = true };
        _password.TextSubmitted += _ => Go(register: false);
        _status = Ui.Text("", 15, Ui.Muted, wrap: true);
        _status.CustomMinimumSize = new Vector2(520, 0);

        _form = Ui.VBox(12,
            Ui.Text("Server", 15, Ui.Muted), Ui.HBox(10, _server, Ui.Button("Servers…", () => Main.Instance.Push(() => new ServersScreen()))),
            Ui.Text("Account", 15, Ui.Muted), _username, _password,
            Ui.HBox(12,
                Ui.Button("Log in", () => Go(register: false), 18),
                Ui.Button("Create account", () => Go(register: true))),
            _status);
        _form.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;

        var page = Page.Create("Play Online", _form, "Accounts live on the server you pick. Your offline game is separate.");
        AddChild(page);
        page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _username.CallDeferred(Control.MethodName.GrabFocus);
    }

    ServerEntry? Selected => _server.Selected >= 0 && _server.Selected < OnlineSession.Servers.Count ? OnlineSession.Servers[_server.Selected] : null;

    async void Go(bool register)
    {
        if (_busy || Selected is not { } entry)
            return;
        _busy = true;
        _status.Text = $"Connecting to {entry.Address}…";
        var client = new OnlineClient(entry);
        if (await client.Probe() is { } problem)
        {
            Done(problem, Ui.Bad);
            client.Dispose();
            return;
        }
        if (!IsInstanceValid(this))
            return;
        if (entry.Fingerprint is null)
        {
            // First contact: ask before trusting the certificate.
            if (!await AskTrust(entry, client.PresentedFingerprint ?? "?"))
            {
                Done("Not connected: the certificate wasn't trusted.", Ui.Warn);
                client.Dispose();
                return;
            }
            client.Trust(client.PresentedFingerprint!);
            OnlineSession.Update(entry, client.Server);
        }
        if (register && await client.Register(_username.Text.Trim(), _password.Text) is { } regError)
        {
            Done(regError, Ui.Bad);
            client.Dispose();
            return;
        }
        if (await client.Login(_username.Text.Trim(), _password.Text) is { } error)
        {
            Done(error, Ui.Bad);
            client.Dispose();
            return;
        }
        if (!IsInstanceValid(this))
            return;
        _busy = false;
        OnlineSession.LoggedIn(client);
        Main.Instance.Push(() => new CharacterScreen());
    }

    void Done(string text, Color color)
    {
        if (!IsInstanceValid(this))
            return;
        _status.Text = text;
        _status.AddThemeColorOverride("font_color", color);
        _busy = false;
    }

    Task<bool> AskTrust(ServerEntry entry, string fingerprint)
    {
        var answer = new TaskCompletionSource<bool>();
        var panel = new PanelContainer();
        void Close(bool trust)
        {
            panel.QueueFree();
            TrustPending = null;
            answer.TrySetResult(trust);
        }
        var text = Ui.Text($"First connection to {entry.Name} ({entry.Address}).\nIts certificate fingerprint is:\n\n{fingerprint}\n\n" +
            "If you run this server, check it matches the one it printed when it started. Trust it?", 16, wrap: true);
        text.CustomMinimumSize = new Vector2(620, 0);
        panel.AddChild(Ui.Margin(Ui.VBox(14, Ui.Text("Trust this server?", 22, Ui.Accent), text,
            Ui.HBox(12, Ui.Button("Trust and connect", () => Close(true), 18), Ui.Button("Cancel", () => Close(false)))), 20));
        AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
        panel.GrowHorizontal = panel.GrowVertical = GrowDirection.Both;
        TrustPending = () => Close(true);
        return answer.Task;
    }
}
