using Godot;
using InfiniteRoutes.Extractor.Roms;

namespace InfiniteRoutes.Client.Ui;

/// <summary>
/// Settings → ROM Library (BUILD_SPEC §2.3): one row per supported game, grouped by the region it
/// unlocks. Used both in the first-run wizard and from the home screen.
/// </summary>
public partial class RomLibraryPanel : VBoxContainer
{
    sealed class Row
    {
        public required Label Status;
        public required Label Path;
        public required Label Details;
        public required ProgressBar Progress;
        public required Button[] Buttons;
    }

    readonly Dictionary<string, Row> _rows = new();
    readonly Dictionary<string, Label> _regionHeadings = new();
    readonly HashSet<string> _busy = new();
    Button _scanButton = null!;
    Label _scanStatus = null!;
    bool _scanning;

    /// <summary>Raised on the main thread after any assignment changes.</summary>
    public event Action? LibraryChanged;

    static RomLibrary Library => ClientServices.Library;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 12);
        SizeFlagsVertical = SizeFlags.ExpandFill;

        _scanButton = Ui.Button("Scan folder…", PickScanFolder);
        _scanStatus = Ui.Text("", 14, Ui.Muted, wrap: true);
        AddChild(Ui.HBox(12, _scanButton, _scanStatus));

        var list = Ui.VBox(6);
        list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        foreach (var region in Library.Manifest.Regions)
        {
            var heading = Ui.Text("", 18, Ui.Accent);
            _regionHeadings[region.Id] = heading;
            list.AddChild(Ui.Margin(heading, 4));
            foreach (var gameId in region.AnyOf)
                list.AddChild(BuildRow(Library.Manifest.FindById(gameId)!));
        }

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        scroll.AddChild(list);
        AddChild(scroll);

        Refresh();

        foreach (var entry in Library.Entries.Where(e => e is { Status: RomStatus.Unchecked, Path: not null }))
            Validate(entry.GameId, entry.Path!);
    }

    Control BuildRow(GameDefinition game)
    {
        var name = Ui.Text(game.Name, 17);
        var status = Ui.Text("", 15);
        var path = Ui.Text("", 13, Ui.Muted);
        path.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        path.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        path.MouseFilter = MouseFilterEnum.Pass;
        var details = Ui.Text("", 13, Ui.Muted, wrap: true);
        var progress = new ProgressBar { Visible = false, CustomMinimumSize = new Vector2(160, 0), MaxValue = 100 };

        var info = Ui.VBox(2, Ui.HBox(12, name, status), path, details);
        info.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var browse = Ui.Button("Browse…", () => PickFile(game.Id), 14);
        var recheck = Ui.Button("Re-validate", () => Validate(game.Id, Library[game.Id].Path!), 14);
        var extract = Ui.Button("Re-extract", () => { }, 14);
        extract.Disabled = true;
        extract.TooltipText = "Asset extraction arrives in Milestone 1.";
        var remove = Ui.Button("Remove", () => RemoveRom(game.Id), 14);

        _rows[game.Id] = new Row
        {
            Status = status,
            Path = path,
            Details = details,
            Progress = progress,
            Buttons = [browse, recheck, remove],
        };

        var panel = new PanelContainer();
        panel.AddChild(Ui.Margin(Ui.HBox(10, info, progress, browse, recheck, extract, remove), 10));
        return panel;
    }

    void Refresh()
    {
        foreach (var entry in Library.Entries)
        {
            var row = _rows[entry.GameId];
            var busy = _busy.Contains(entry.GameId);
            row.Status.Text = busy ? "Checking…" : entry.Label;
            row.Status.AddThemeColorOverride("font_color", busy ? Ui.Muted : ColorFor(entry.Status));
            row.Path.Text = entry.Path ?? "No file selected";
            row.Path.TooltipText = entry.Path ?? "";
            row.Details.Text = busy ? "" : DetailsFor(entry);
            row.Details.Visible = row.Details.Text.Length > 0;
            row.Progress.Visible = busy;
            row.Buttons[0].Disabled = busy || _scanning;
            row.Buttons[1].Disabled = busy || _scanning || entry.Path is null;
            row.Buttons[2].Disabled = busy || _scanning || entry.Path is null;
        }

        foreach (var region in Library.Regions())
        {
            var r = region.Region;
            var tags = (r.RequiredToPlay ? " · required" : "") + (r.InBaseGame ? "" : " · future expansion");
            _regionHeadings[r.Id].Text = $"{r.Name}{tags} — {(region.Available ? "unlocked" : "locked")}";
        }

        _scanButton.Disabled = _scanning || _busy.Count > 0;
    }

    static string DetailsFor(RomLibraryEntry e)
    {
        var parts = new List<string>();
        if (e.Status != RomStatus.Valid && e.Message.Length > 0)
            parts.Add(e.Message);
        if (e.Sha1 is { } sha)
            parts.Add($"SHA-1 {sha[..12]}…");
        return string.Join("  ·  ", parts);
    }

    static Color ColorFor(RomStatus s) => s switch
    {
        RomStatus.Valid => Ui.Good,
        RomStatus.Missing => Ui.Warn,
        RomStatus.Unchecked => Ui.Muted,
        _ => Ui.Bad,
    };

    void PickFile(string gameId)
    {
        var dialog = new FileDialog
        {
            Title = $"Choose your {Library.Manifest.FindById(gameId)!.Name} ROM",
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = ["*.nds, *.zip ; Nintendo DS ROMs"],
            UseNativeDialog = true,
        };
        if (Library[gameId].Path is { } current && System.IO.Path.GetDirectoryName(current) is { } dir && Directory.Exists(dir))
            dialog.CurrentDir = dir;

        dialog.FileSelected += path =>
        {
            dialog.QueueFree();
            Validate(gameId, path);
        };
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    void PickScanFolder()
    {
        var dialog = new FileDialog
        {
            Title = "Choose a folder containing your DS ROMs",
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
        };
        dialog.DirSelected += dir =>
        {
            dialog.QueueFree();
            Scan(dir);
        };
        dialog.Canceled += dialog.QueueFree;
        AddChild(dialog);
        dialog.PopupCentered(new Vector2I(900, 600));
    }

    async void Validate(string gameId, string path)
    {
        if (!_busy.Add(gameId))
            return;
        var row = _rows[gameId];
        row.Progress.Value = 0;
        Refresh();

        // Progress<T> captures Godot's synchronization context, so reports land on the main thread.
        var progress = new Progress<double>(p => row.Progress.Value = p * 100);
        var result = await Task.Run(() => ClientServices.Validator.Validate(path, progress));
        if (!IsInstanceValid(this))
            return;

        _busy.Remove(gameId);
        var target = result.Game?.Id ?? gameId;
        Library.Assign(target, path, result);
        if (target != gameId && Library[gameId].Path == path)
            Library.Remove(gameId);
        if (target != gameId)
            _scanStatus.Text = $"That file is {result.Game!.Name}, so it was put in that slot.";
        Commit();
    }

    async void Scan(string folder)
    {
        _scanning = true;
        _scanStatus.Text = "Scanning…";
        Refresh();

        var progress = new Progress<(int Done, int Total)>(p => _scanStatus.Text = $"Checking file {Math.Min(p.Done + 1, p.Total)} of {p.Total}…");
        // Scan into a scratch library off the main thread, then merge here so the real one is only touched on the main thread.
        var scratch = new RomLibrary(Library.Manifest);
        List<ScanResult> results;
        try
        {
            results = await Task.Run(() => scratch.Scan(folder, ClientServices.Validator, progress));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _scanning = false;
            _scanStatus.Text = $"Couldn't scan that folder: {e.Message}";
            Refresh();
            return;
        }
        if (!IsInstanceValid(this))
            return;

        var added = new List<string>();
        foreach (var r in results.Where(r => r.AssignedTo is not null))
        {
            if (RomStatusText.IsUsable(Library[r.AssignedTo!].Status))
                continue;
            Library.Assign(r.AssignedTo!, r.Path, r.Result);
            added.Add(r.Result.Game!.Name);
        }

        _scanning = false;
        var skipped = results.Count - added.Count;
        _scanStatus.Text = results.Count == 0
            ? "No .nds or .zip files found in that folder."
            : $"Found {results.Count} file(s). Added: {(added.Count > 0 ? string.Join(", ", added) : "none")}."
              + (skipped > 0 ? $" {skipped} skipped (already assigned, unsupported, or invalid)." : "");
        Commit();
    }

    void RemoveRom(string gameId)
    {
        Library.Remove(gameId);
        Commit();
    }

    void Commit()
    {
        ClientServices.SaveLibrary();
        Refresh();
        LibraryChanged?.Invoke();
    }
}
