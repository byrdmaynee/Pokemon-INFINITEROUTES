using Godot;
using InfiniteRoutes.Client.Online;
using InfiniteRoutes.Core.Config;

namespace InfiniteRoutes.Client.Ui;

/// <summary>
/// Play Online: pick a server, then log in or create an account. The first connection to a server shows its
/// certificate fingerprint to trust; after that a different certificate is refused. "Remember me" keeps the login on
/// this computer (the server's session token, not the password: <see cref="RememberedLogins"/>), so next time it's
/// "Continue as …" in one click.
/// </summary>
public partial class LoginScreen(ServerEntry? preselected) : Control
{
    OptionButton _server = null!;
    LineEdit _username = null!, _password = null!;
    CheckBox _remember = null!;
    Button _continue = null!, _forget = null!;
    Control _remembered = null!;
    Label _status = null!;
    Control _form = null!;
    bool _busy;
    readonly RememberedLogins _logins = RememberedLogins.ForUser();

    // Godot needs a parameterless constructor to recreate script instances.
    public LoginScreen() : this(null) { }

    /// <summary>Fills in the form and presses a button; for smoke tests (which never remember the login).</summary>
    public void Submit(string name, string password, bool register)
    {
        _username.Text = name;
        _password.Text = password;
        _remember.ButtonPressed = false;
        Go(register);
    }

    /// <summary>Logs in with "Remember me" ticked; for smoke tests.</summary>
    public void SubmitRemembering(string name, string password)
    {
        _username.Text = name;
        _password.Text = password;
        _remember.ButtonPressed = true;
        Go(register: false);
    }

    /// <summary>The account "Continue as …" offers, if any; for smoke tests.</summary>
    public string? RememberedAccount => _remembered.Visible ? Remembered?.Account : null;

    /// <summary>Presses "Continue as …"; for smoke tests.</summary>
    public void PressContinue() => Continue();

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
        _remember = new CheckBox { Text = "Remember me on this computer", TooltipText = "Stay logged in here (until you log out). Your password isn't stored." };
        _remember.AddThemeFontSizeOverride("font_size", 15);
        _status = Ui.Text("", 15, Ui.Muted, wrap: true);
        _status.CustomMinimumSize = new Vector2(520, 0);
        _continue = Ui.Button("Continue", Continue, 18);
        _forget = Ui.Button("Use another account", Forget);
        _remembered = Ui.HBox(12, _continue, _forget);

        _form = Ui.VBox(12,
            Ui.Text("Server", 15, Ui.Muted), Ui.HBox(10, _server, Ui.Button("Servers…", () => Main.Instance.Push(() => new ServersScreen()))),
            _remembered,
            Ui.Text("Account", 15, Ui.Muted), _username, _password, _remember,
            Ui.HBox(12,
                Ui.Button("Log in", () => Go(register: false), 18),
                Ui.Button("Create account", () => Go(register: true))),
            _status);
        _server.ItemSelected += _ => ShowRemembered();
        _form.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;

        var page = Page.Create("Play Online", _form, "Accounts live on the server you pick. Your offline game is separate.");
        AddChild(page);
        page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        ShowRemembered();
        (Remembered is not null ? (Control)_continue : _username).CallDeferred(Control.MethodName.GrabFocus);
    }

    RememberedLogin? Remembered => Selected is { } entry ? _logins.Find(entry) : null;

    /// <summary>"Continue as …" for the selected server when a login is kept for it; its account name filled in.</summary>
    void ShowRemembered()
    {
        var login = Remembered;
        _remembered.Visible = login is not null;
        _remember.ButtonPressed = login is not null;
        if (login is null)
            return;
        _continue.Text = $"Continue as {login.Account}";
        _username.Text = login.Account;
        _password.Text = "";
    }

    void Forget()
    {
        if (Selected is { } entry)
            _logins.Forget(entry);
        _username.Text = "";
        ShowRemembered();
        _username.GrabFocus();
    }

    /// <summary>Logs in with the kept session; if the server no longer takes it, forgets it and asks for the password.</summary>
    async void Continue()
    {
        if (_busy || Selected is not { } entry || Remembered is not { } login)
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
        if (await client.Resume(login.Token) is not null)
        {
            client.Dispose();
            _logins.Forget(entry);
            ShowRemembered();
            _username.Text = login.Account;
            Done("Your saved login has ended. Enter your password to log in again.", Ui.Warn);
            _password.CallDeferred(Control.MethodName.GrabFocus);
            return;
        }
        if (!IsInstanceValid(this))
            return;
        _busy = false;
        OnlineSession.LoggedIn(client);
        Main.Instance.Push(() => new CharacterScreen());
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
        var remember = _remember.ButtonPressed;
        if (await client.Login(_username.Text.Trim(), _password.Text, remember) is { } error)
        {
            Done(error, Ui.Bad);
            client.Dispose();
            return;
        }
        if (!IsInstanceValid(this))
            return;
        // The certificate the client trusted is now in the entry the login is kept under.
        if (remember && client.Token is { } token)
            _logins.Remember(client.Server, client.Account ?? _username.Text.Trim(), token, client.Expires);
        else
            _logins.Forget(client.Server);
        _password.Text = "";
        ShowRemembered();
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
