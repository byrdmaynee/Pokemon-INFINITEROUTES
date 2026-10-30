using Godot;
using InfiniteRoutes.Client.Ui;

namespace InfiniteRoutes.Client;

/// <summary>
/// Root node. Owns the animated background, the screen stack (Esc/Back pops it) and toasts.
/// Screens are rebuilt from factories when navigated back to, so they always show fresh state.
/// </summary>
public partial class Main : Control
{
    public static Main Instance { get; private set; } = null!;

    readonly Stack<Func<Control>> _history = new();
    Control _screenHost = null!;
    Label _toast = null!;
    Tween? _toastTween;

    public override void _Ready()
    {
        Instance = this;

        var background = new MenuBackground();
        AddChild(background);
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _screenHost = new Control();
        AddChild(_screenHost);
        _screenHost.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _toast = Ui.Ui.Text("", 17, Ui.Ui.Accent);
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.Modulate = Colors.Transparent;
        _toast.AddThemeStyleboxOverride("normal", new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.07f, 0.11f, 0.92f),
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 8, ContentMarginBottom = 8,
        });
        AddChild(_toast);
        _toast.SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom);
        _toast.GrowHorizontal = GrowDirection.Both;
        _toast.GrowVertical = GrowDirection.Begin;
        _toast.OffsetBottom = -96;

        ResetTo(() => new LoadingScreen(OnLoaded));
        DebugCapture.StartIfRequested(this);
    }

    void OnLoaded() =>
        ResetTo(ClientServices.Library.WizardCompleted
            ? () => new MainMenu()
            // After setup, load again so the new ROMs get extracted before the menu opens.
            : () => new FirstRunWizard(() => ResetTo(() => new LoadingScreen(OnLoaded))));

    /// <summary>Opens a screen on top of the current one; Back/Esc returns.</summary>
    public void Push(Func<Control> screen)
    {
        _history.Push(screen);
        Show(screen());
    }

    /// <summary>Clears history and shows a screen (e.g. after loading or finishing setup).</summary>
    public void ResetTo(Func<Control> screen)
    {
        _history.Clear();
        Push(screen);
    }

    public void Back()
    {
        if (_history.Count <= 1)
            return;
        _history.Pop();
        Show(_history.Peek()());
    }

    public bool CanGoBack => _history.Count > 1;

    public Control? CurrentScreen => _screenHost.GetChildCount() > 0 ? _screenHost.GetChild<Control>(0) : null;

    public void Toast(string message)
    {
        _toast.Text = message;
        _toastTween?.Kill();
        _toast.Modulate = Colors.White;
        _toastTween = CreateTween();
        _toastTween.TweenInterval(2.2);
        _toastTween.TweenProperty(_toast, "modulate", Colors.Transparent, 0.6);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("ui_cancel") && CanGoBack)
        {
            Back();
            GetViewport().SetInputAsHandled();
        }
    }

    void Show(Control screen)
    {
        Ui.Ui.ClearChildren(_screenHost);
        _screenHost.AddChild(screen);
        screen.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }
}
