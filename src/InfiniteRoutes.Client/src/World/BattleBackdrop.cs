using Godot;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// The battle scenery from the ROM, rendered in 3D behind the battle sprites: a backdrop model (batt_bg*: sky, horizon
/// and field) and two platform models (batt_stage*) under the Pokémon. Like on the DS, the platforms are near the
/// camera and the backdrop far behind, so each gets its own layer: the backdrop framed to fill the screen, the
/// platforms put on the ground under the sprites' feet and sized to match them.
/// </summary>
public sealed partial class BattleBackdrop : Control
{
    // The backdrop is an arena: a floor disc (radius ~4.8, centred at z ≈ 19) inside a cylinder of sky. Its camera
    // stands inside the near edge, looking down across the floor at the far wall.
    static readonly Vector3 BackdropEye = new(0, 1.4f, 14.6f), BackdropTarget = new(0, 0.3f, 21f);
    const float BackdropFov = 70f;
    // The platform camera: looking down at the ground in front of it.
    static readonly Vector3 StageEye = new(0, 1.6f, -2.5f), StageTarget = new(0, 0, 3.5f);
    const float StageFov = 48f;

    readonly GameAssets _assets;
    SubViewport _backdropView = null!, _stageView = null!;
    Camera3D _stageCamera = null!;
    Node3D? _backdrop, _foeStage, _playerStage;
    (Vector2 Feet, float Width) _foe, _player;

    public string Backdrop { get; private set; }
    public string Stage { get; private set; }

    /// <summary>True when the backdrop model loaded (otherwise the battle screen draws its plain field).</summary>
    public bool Loaded => _backdrop is not null;

    // Godot needs a parameterless constructor to recreate script instances.
    public BattleBackdrop() : this(null!, "", "") { }

    public BattleBackdrop(GameAssets assets, string backdrop, string stage)
    {
        _assets = assets;
        Backdrop = backdrop;
        Stage = stage;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Ready()
    {
        _backdropView = Layer(transparent: false, BackdropEye, BackdropTarget, BackdropFov);
        _stageView = Layer(transparent: true, StageEye, StageTarget, StageFov);
        _stageCamera = _stageView.GetCamera3D();
        Load(Backdrop, Stage);
        Resized += () => Place();
    }

    SubViewport Layer(bool transparent, Vector3 eye, Vector3 target, float fov)
    {
        var container = new SubViewportContainer { Stretch = true, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(container);
        container.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var view = new SubViewport { OwnWorld3D = true, TransparentBg = transparent };
        container.AddChild(view);
        if (!transparent)
            view.AddChild(new WorldEnvironment
            {
                Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.62f, 0.83f, 0.95f) },
            });
        var camera = new Camera3D { Fov = fov, KeepAspect = Camera3D.KeepAspectEnum.Width, Near = 0.05f, Far = 200, Current = true };
        view.AddChild(camera);
        camera.LookAtFromPosition(eye, target, Vector3.Up);
        return view;
    }

    /// <summary>Swaps in another backdrop and platforms (smoke tests cycle through them).</summary>
    public void Load(string backdrop, string stage)
    {
        Backdrop = backdrop;
        Stage = stage;
        _backdrop?.QueueFree();
        _foeStage?.QueueFree();
        _playerStage?.QueueFree();
        _backdrop = Model(_backdropView, backdrop);
        _foeStage = Model(_stageView, stage);
        _playerStage = Model(_stageView, stage);
        Place();
    }

    Node3D? Model(SubViewport view, string name)
    {
        if (_assets?.BattleSceneryPath(name) is not { } path)
            return null;
        var doc = new GltfDocument();
        var state = new GltfState();
        if (doc.AppendFromFile(path, state) != Error.Ok || doc.GenerateScene(state) is not Node3D scene)
            return null;
        foreach (var mesh in scene.FindChildren("*", nameof(MeshInstance3D), owned: false).OfType<MeshInstance3D>())
            for (var s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
                if (mesh.Mesh.SurfaceGetMaterial(s) is BaseMaterial3D m)
                    m.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
        view.AddChild(scene);
        return scene;
    }

    /// <summary>
    /// Puts each platform on the ground under a sprite's feet (in this control's pixels), scaled so it looks
    /// <c>width</c> pixels across.
    /// </summary>
    public void PlaceStages(Vector2 foeFeet, float foeWidth, Vector2 playerFeet, float playerWidth)
    {
        _foe = (foeFeet, foeWidth);
        _player = (playerFeet, playerWidth);
        Place();
    }

    void Place()
    {
        if (_stageCamera is null || Size.X <= 0)
            return;
        Place(_foeStage, _foe.Feet, _foe.Width);
        Place(_playerStage, _player.Feet, _player.Width);
    }

    void Place(Node3D? stage, Vector2 feet, float width)
    {
        if (stage is null || width <= 0)
            return;
        var origin = _stageCamera.ProjectRayOrigin(feet);
        var direction = _stageCamera.ProjectRayNormal(feet);
        if (direction.Y >= -0.001f)
            return;
        var at = origin + direction * (-origin.Y / direction.Y);
        // The model's own radius, then whatever scale makes it the wanted width on screen.
        var radius = 0f;
        foreach (var mesh in stage.FindChildren("*", nameof(MeshInstance3D), owned: false).OfType<MeshInstance3D>())
            radius = Math.Max(radius, Math.Max(mesh.GetAabb().Size.X, mesh.GetAabb().Size.Z) / 2);
        if (radius <= 0)
            return;
        stage.Scale = Vector3.One;
        stage.Position = at;
        // (Looking down +z puts +x on the left, hence the absolute value.)
        var across = Math.Abs(_stageCamera.UnprojectPosition(at + new Vector3(radius, 0, 0)).X - _stageCamera.UnprojectPosition(at - new Vector3(radius, 0, 0)).X);
        if (across > 0)
            stage.Scale = Vector3.One * (width / across);
    }
}
