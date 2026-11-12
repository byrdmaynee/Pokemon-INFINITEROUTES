using Godot;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// An NPC standing on the overworld grid: a billboard sprite from its <see cref="CharacterSheet"/> that can
/// turn, take single steps, and occupies the tile it is on (or stepping onto).
/// </summary>
public partial class NpcActor : Node3D
{
    const float StepSeconds = 0.32f;

    readonly Sprite3D _sprite;
    Facing _facing;
    int _steps;
    double _idle;

    public NpcInfo Info { get; }
    public Vector2I Tile { get; private set; }
    public Vector2I Home { get; }
    public bool Moving { get; private set; }
    /// <summary>The zone whose events place this NPC (gifts and quest givers are keyed by zone and NPC id).</summary>
    public int Zone { get; init; }

    Label3D? _questMark;

    /// <summary>Shows or hides the yellow "!" of an NPC who has a quest for the player.</summary>
    public void SetQuestMark(bool on)
    {
        if (on && _questMark is null)
        {
            _questMark = new Label3D
            {
                Text = "!", FontSize = 96, PixelSize = 0.011f, Modulate = new Color(1, 0.85f, 0.15f), OutlineSize = 18,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, Position = new Vector3(0, 2.6f, 0),
            };
            AddChild(_questMark);
        }
        if (_questMark is not null)
            _questMark.Visible = on;
    }

    public bool QuestMarked => _questMark?.Visible == true;

    public NpcActor(NpcInfo info, Texture2D? sheet)
    {
        Info = info;
        Tile = Home = new Vector2I(info.X, info.Y);
        _facing = info.Facing;
        _idle = GD.RandRange(1.0, 4.0);
        var frame = sheet is null ? CharacterSheet.FrameSize : sheet.GetWidth() / CharacterSheet.Columns;
        _sprite = CharacterSprite.Create(sheet, frame);
        AddChild(_sprite);
        ShowFrame(0);
    }

    public Facing Facing
    {
        get => _facing;
        set
        {
            _facing = value;
            ShowFrame(0);
        }
    }

    /// <summary>Counts down the idle timer; true when it is time to act (look around or take a step).</summary>
    public bool Tick(double delta)
    {
        if (Moving || (_idle -= delta) > 0)
            return false;
        _idle = GD.RandRange(1.5, 4.0);
        return true;
    }

    /// <summary>Holds still a little longer, e.g. after being talked to.</summary>
    public void Pause(double seconds) => _idle = Math.Max(_idle, seconds);

    public void StepTo(Vector2I tile, float height)
    {
        Moving = true;
        Tile = tile;
        var tween = CreateTween();
        tween.TweenProperty(this, "position", new Vector3(tile.X + 0.5f, height, tile.Y + 0.5f), StepSeconds);
        ShowFrame(1 + _steps++ % 2);
        tween.Parallel().TweenCallback(Callable.From(() => ShowFrame(0))).SetDelay(StepSeconds / 2);
        tween.Finished += () => Moving = false;
    }

    void ShowFrame(int step) => _sprite.Frame = (int)_facing * CharacterSheet.Columns + CharacterSheet.Column(false, step);
}
