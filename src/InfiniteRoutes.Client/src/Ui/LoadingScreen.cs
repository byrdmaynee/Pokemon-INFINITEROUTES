using Godot;
using InfiniteRoutes.Core;
using InfiniteRoutes.Core.Config;
using InfiniteRoutes.Extractor.Roms;

namespace InfiniteRoutes.Client.Ui;

/// <summary>
/// Boot sequence: load the manifest and ROM library, re-check any ROM that changed since last
/// run, then hand over to the wizard or main menu.
/// </summary>
public partial class LoadingScreen : CenterContainer
{
    const double MinimumSplashSeconds = 1.2;

    readonly Action _onDone;
    Label _step = null!;
    ProgressBar _bar = null!;

    public LoadingScreen(Action onDone) => _onDone = onDone;

    /// <summary>For the engine (script reload); always created with the other constructor.</summary>
    public LoadingScreen() : this(() => { }) { }

    public override void _Ready()
    {
        _step = Ui.Text("", 16, Ui.Muted);
        _step.HorizontalAlignment = HorizontalAlignment.Center;
        _bar = new ProgressBar { MaxValue = 100, CustomMinimumSize = new Vector2(420, 8), ShowPercentage = false };
        _bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.12f) });
        _bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = Ui.Accent });

        var title = Ui.Text(ClientInfo.Title, 46, Ui.Accent);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var version = Ui.Text($"v{ClientInfo.Version}", 14, Ui.Muted);
        version.HorizontalAlignment = HorizontalAlignment.Center;

        AddChild(Ui.VBox(18, title, version, _bar, _step));
        Run();
    }

    async void Run()
    {
        var started = Time.GetTicksMsec();

        SetStep("Loading game manifest and ROM library…", 10);
        await NextFrame();
        try
        {
            ClientServices.Initialize();
        }
        catch (PkirException e)
        {
            ShowBrokenConfig(e.Message);
            return;
        }

        var toCheck = ClientServices.Library.Entries
            .Where(e => e is { Status: RomStatus.Unchecked, Path: not null })
            .ToList();
        for (var i = 0; i < toCheck.Count; i++)
        {
            var entry = toCheck[i];
            var name = ClientServices.Manifest.FindById(entry.GameId)!.Name;
            var from = 20 + 70.0 * i / toCheck.Count;
            var span = 70.0 / toCheck.Count;
            SetStep($"Checking {name}…", from);
            var progress = new Progress<double>(p => _bar.Value = from + p * span);
            var path = entry.Path!;
            var result = await Task.Run(() => ClientServices.Validator.Validate(path, progress));
            if (!IsInstanceValid(this))
                return;
            ClientServices.Library.Assign(result.Game?.Id ?? entry.GameId, path, result);
        }
        if (toCheck.Count > 0)
            ClientServices.SaveLibrary();

        SetStep("Ready", 100);
        var elapsed = (Time.GetTicksMsec() - started) / 1000.0;
        if (elapsed < MinimumSplashSeconds)
            await ToSignal(GetTree().CreateTimer(MinimumSplashSeconds - elapsed), SceneTreeTimer.SignalName.Timeout);
        if (IsInstanceValid(this))
            _onDone();
    }

    void SetStep(string text, double percent)
    {
        _step.Text = text;
        _bar.Value = percent;
    }

    SignalAwaiter NextFrame() => ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    void ShowBrokenConfig(string error)
    {
        Ui.ClearChildren(this);
        AddChild(Ui.VBox(16,
            Ui.Text("Your ROM library settings couldn't be read", 26, Ui.Bad),
            Ui.Text($"File: {AppPaths.RomLibraryFile}", 14, Ui.Muted),
            Ui.Text(error, 14),
            Ui.Text("Fix the file in a text editor and restart, or start over (the old file is kept as a backup).", 16),
            Ui.HBox(8,
                Ui.Button("Start over", () =>
                {
                    ClientServices.BackUpBrokenLibrary();
                    Main.Instance.ResetTo(() => new LoadingScreen(_onDone));
                }),
                Ui.Button("Quit", () => GetTree().Quit()))));
    }
}
