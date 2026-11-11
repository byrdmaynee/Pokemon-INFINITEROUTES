using Godot;
using InfiniteRoutes.Client.Online;
using InfiniteRoutes.Core.Config;

namespace InfiniteRoutes.Client.Ui;

/// <summary>The server list (BUILD_SPEC §4.5), saved in servers.pkir: connect to one, add one, or forget one.</summary>
public partial class ServersScreen : Control
{
    VBoxContainer _rows = null!;
    LineEdit _name = null!, _address = null!;

    public override void _Ready()
    {
        OnlineSession.LoadServers();
        _rows = Ui.VBox(10);
        _name = new LineEdit { PlaceholderText = "Name (optional)", CustomMinimumSize = new Vector2(220, 0) };
        _address = new LineEdit { PlaceholderText = "Address, e.g. 192.168.1.50:7451", CustomMinimumSize = new Vector2(300, 0) };
        _address.TextSubmitted += _ => Add();
        var content = Ui.VBox(14,
            _rows,
            Ui.Text("Add a server", 16, Ui.Muted),
            Ui.HBox(10, _name, _address, Ui.Button("Add", Add)));
        var page = Page.Create("Servers", content,
            "The address is the server's accounts port (7451 by default). The first time you connect you're asked to trust the server's certificate.");
        AddChild(page);
        page.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Refresh();
    }

    void Refresh()
    {
        Ui.ClearChildren(_rows);
        foreach (var server in OnlineSession.Servers)
        {
            var entry = server;
            var row = new PanelContainer();
            row.AddChild(Ui.Margin(Ui.HBox(16,
                Ui.VBox(2,
                    Ui.Text(entry.Name, 18),
                    Ui.Text($"{entry.Address} · {(entry.Fingerprint is null ? "not trusted yet" : "certificate trusted")}", 14, Ui.Muted)),
                Ui.Spacer(),
                Ui.Button("Connect", () => Main.Instance.Push(() => new LoginScreen(entry))),
                Ui.Button("Remove", () =>
                {
                    OnlineSession.Servers.Remove(entry);
                    OnlineSession.SaveServers();
                    Refresh();
                })), 12));
            _rows.AddChild(row);
        }
    }

    void Add()
    {
        if (ServerEntry.ParseAddress(_address.Text) is not { } parsed)
        {
            Main.Instance.Toast("That isn't an address (host or host:port).");
            return;
        }
        var name = _name.Text.Trim() is { Length: > 0 } n ? n : $"{parsed.Host}:{parsed.Port}";
        OnlineSession.Servers.Add(new ServerEntry(name, parsed.Host, parsed.Port));
        OnlineSession.SaveServers();
        _name.Text = _address.Text = "";
        Refresh();
    }
}
