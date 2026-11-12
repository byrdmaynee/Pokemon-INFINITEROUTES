using Godot;
using InfiniteRoutes.Client.Online;
using InfiniteRoutes.Core.Config;

namespace InfiniteRoutes.Client.Ui;

/// <summary>The account's characters on this server: play one, or make one (a name and the hero or heroine).</summary>
public partial class CharacterScreen : Control
{
    VBoxContainer _list = null!;
    LineEdit _name = null!;
    OptionButton _look = null!;
    Label _status = null!;
    bool _busy;

    public List<CharacterSummary> Characters { get; } = [];

    public string Status => _status.Text;

    public override void _Ready()
    {
        _list = Ui.VBox(10);
        _name = new LineEdit { PlaceholderText = "Character name (2–12 letters or digits)", CustomMinimumSize = new Vector2(320, 0) };
        _look = new OptionButton();
        _look.AddItem("Hero");
        _look.AddItem("Heroine");
        _status = Ui.Text("", 15, Ui.Muted, wrap: true);
        var client = OnlineSession.Client;
        var content = Ui.VBox(14,
            _list,
            Ui.Text("New character", 16, Ui.Muted),
            Ui.HBox(10, _name, _look, Ui.Button("Create", Create)),
            _status,
            Ui.HBox(10, Ui.Button("Account and two-factor…", () => Main.Instance.Push(() => new AccountScreen())),
                Ui.Button("Log out", LogOut)));
        var page = Page.Create($"Characters — {client?.Account} on {client?.Info?.Name}", content,
            $"{client?.Info?.Online ?? 0} player(s) online.");
        AddChild(page);
        page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Load();
    }

    /// <summary>Presses "Log out"; for smoke tests.</summary>
    public void PressLogOut() => LogOut();

    /// <summary>Ends the session (here and on the server), forgets a remembered login, and goes back to the login screen.</summary>
    async void LogOut()
    {
        if (_busy || OnlineSession.Client is not { } client)
            return;
        _busy = true;
        RememberedLogins.ForUser().Forget(client.Server);
        await client.Logout();
        if (!IsInstanceValid(this))
            return;
        _busy = false;
        Main.Instance.Back();
    }

    async void Load()
    {
        if (OnlineSession.Client is not { } client)
            return;
        var (list, error) = await client.Characters();
        if (!IsInstanceValid(this))
            return;
        if (list is null)
        {
            _status.Text = error ?? "";
            return;
        }
        Characters.Clear();
        Characters.AddRange(list);
        Ui.ClearChildren(_list);
        if (list.Count == 0)
            _list.AddChild(Ui.Text("No characters yet: make one below.", 16, Ui.Muted));
        foreach (var c in list)
        {
            var character = c;
            var row = new PanelContainer();
            row.AddChild(Ui.Margin(Ui.HBox(16,
                Ui.VBox(2, Ui.Text(character.Name, 20), Ui.Text(character.Appearance == 1 ? "Heroine" : "Hero", 14, Ui.Muted)),
                Ui.Spacer(),
                Ui.Button("Play", () => Play(character), 18)), 12));
            _list.AddChild(row);
        }
    }

    async void Create()
    {
        if (_busy || OnlineSession.Client is not { } client)
            return;
        _busy = true;
        var (made, error) = await client.CreateCharacter(_name.Text.Trim(), _look.Selected);
        _busy = false;
        if (!IsInstanceValid(this))
            return;
        _status.Text = made is null ? error ?? "" : $"{made.Name} is ready.";
        _name.Text = "";
        Load();
    }

    /// <summary>Joins the world as <paramref name="character"/>; public so smoke tests can play.</summary>
    public async void Play(CharacterSummary character)
    {
        if (_busy || OnlineSession.Client is not { } client)
            return;
        _busy = true;
        _status.Text = $"Joining as {character.Name}…";
        var (game, error) = await GameConnection.Join(client, character.Id);
        _busy = false;
        if (!IsInstanceValid(this))
        {
            game?.Dispose();
            return;
        }
        if (game is null)
        {
            _status.Text = error ?? "";
            return;
        }
        OnlineSession.Joined(game, character);
        Main.Instance.Push(() => new World.OverworldScreen(online: true));
    }
}
