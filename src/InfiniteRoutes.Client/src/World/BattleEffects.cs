using Godot;
using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Extractor.Extraction;
using InfiniteRoutes.Extractor.Nitro3D;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// Plays the ROM's own move animations: the battle effect script for a move (see Gen5MoveAnimations in the
/// extractor) drives particle emitters from the ROM's particle files, moves, squashes and tints the Pokémon, and dims
/// the background. Particles are simulated like the DS's particle library (30 updates a second) in the battle's 3D
/// space — the single-battle positions (0.5, 0.4, 7) for the player's Pokémon and (0.3, 0.4, −10) for the foe — and
/// drawn through a camera fitted so those positions land on the sprites' feet.
/// </summary>
public sealed partial class BattleEffects : Control
{
    const double Tick = 1.0 / 30;
    const int MaxScriptFrames = 30 * 12;

    static readonly Vector3 PlayerPos = new(0.5f, 0.4f, 7f), FoePos = new(0.3f, 0.4f, -10f);
    static readonly Vector3 Eye = new(10.5f, 7f, 18f), LookAt = new(0.4f, 1f, -1.5f);

    // Overlay 94's double and triple battle positions, by slot (the player's slot 0 is on the left, the foe's on the right).
    static readonly Vector3[] DoublePlayer = [new(-1.688f, 0.4f, 6.375f), new(2.5f, 0.4f, 7.625f)];
    static readonly Vector3[] DoubleFoe = [new(2.425f, 0.4f, -10.375f), new(-2.2f, 0.4f, -11.562f)];
    static readonly Vector3[] TriplePlayer = [new(-4f, 0.4f, 7f), new(0.75f, 0.4f, 4.75f), new(5.188f, 0.4f, 6.188f)];
    static readonly Vector3[] TripleFoe = [new(4.5f, 0.4f, -12f), new(0.738f, 0.4f, -9f), new(-4.387f, 0.4f, -12f)];

    /// <summary>Where the Pokémon in <paramref name="side"/>'s <paramref name="slot"/> stands with <paramref name="width"/> a side.</summary>
    public static Vector3 Home(BattleSide side, int slot, int width)
    {
        var table = (width, side) switch
        {
            (2, BattleSide.Player) => DoublePlayer,
            (2, _) => DoubleFoe,
            (3, BattleSide.Player) => TriplePlayer,
            (3, _) => TripleFoe,
            (_, BattleSide.Player) => [PlayerPos],
            _ => [FoePos],
        };
        return table[Math.Clamp(slot, 0, table.Length - 1)];
    }

    /// <summary>
    /// Where a battler's feet go on screen, given where the single-battle positions go, and how big it looks there
    /// compared with the single-battle Pokémon on its side.
    /// </summary>
    public static (Vector2 Feet, float Scale) Place(Vector2 playerFeet, Vector2 foeFeet, BattleSide side, int slot, int width)
    {
        var view = new View(playerFeet, foeFeet);
        var (at, ppu) = view.Project(Home(side, slot, width));
        var (_, single) = view.Project(side == BattleSide.Player ? PlayerPos : FoePos);
        return (at, ppu / single);
    }

    /// <summary>The DS-like battle camera, scaled and moved so the single-battle positions land on the given feet.</summary>
    readonly struct View
    {
        readonly Basis _camera;
        readonly float _scale;
        readonly Vector2 _offset;

        public View(Vector2 playerFeet, Vector2 foeFeet)
        {
            var forward = (LookAt - Eye).Normalized();
            var right = forward.Cross(Vector3.Up).Normalized();
            _camera = new Basis(right, right.Cross(forward), forward);
            _scale = 1;
            _offset = Vector2.Zero;
            var p = Raw(PlayerPos);
            var f = Raw(FoePos);
            _scale = (foeFeet - playerFeet).Length() / Math.Max(1e-4f, (f - p).Length());
            _offset = playerFeet - p * _scale;
        }

        Vector2 Raw(Vector3 point)
        {
            var d = point - Eye;
            var z = Math.Max(0.1f, d.Dot(_camera.Z));
            return new Vector2(d.Dot(_camera.X) / z, -d.Dot(_camera.Y) / z);
        }

        public (Vector2 At, float PixelsPerUnit) Project(Vector3 point)
        {
            var z = Math.Max(0.1f, (point - Eye).Dot(_camera.Z));
            return (Raw(point) * _scale + _offset, _scale / z);
        }
    }

    static IReadOnlyList<IReadOnlyList<int[]>>? _scripts;

    readonly GameAssets _assets;
    readonly Func<(Vector2 Player, Vector2 Foe)> _anchors;
    readonly Func<BattleSide, int, TextureRect> _sprite;
    readonly ColorRect _dim;
    readonly Dictionary<int, (SpaFile File, Texture2D?[] Textures)?> _particleFiles = [];
    readonly List<Emitter> _emitters = [];
    readonly List<Func<bool>> _tasks = [];
    readonly List<(long Frame, TaskCompletionSource Done)> _waits = [];
    readonly Random _rng = new();
    readonly Dictionary<TextureRect, (Vector2 Offset, Vector2 Scale)> _poses = [];
    readonly ShaderMaterial _tintMaterial;
    double _clock;
    long _frame;
    View _view;
    int _width = 1;
    (BattleSide Side, int Slot) _user, _target;

    /// <summary>True while an animation is playing.</summary>
    public bool Playing { get; private set; }

    /// <summary>Moves whose animation played (and how many particles they put up at most); for smoke tests.</summary>
    public List<(int Move, int Peak)> Played { get; } = [];

    // Godot needs a parameterless constructor to recreate script instances.
    public BattleEffects() : this(null!, null!, null!, null!) { }

    /// <param name="anchors">Where the single-battle positions' feet are on screen now (the camera is fitted to them).</param>
    /// <param name="sprite">The sprite of the Pokémon in a side's slot.</param>
    public BattleEffects(GameAssets assets, Func<(Vector2 Player, Vector2 Foe)> anchors, Func<BattleSide, int, TextureRect> sprite, ColorRect dim)
    {
        _assets = assets;
        _anchors = anchors;
        _sprite = sprite;
        _dim = dim;
        MouseFilter = MouseFilterEnum.Ignore;
        _tintMaterial = new ShaderMaterial
        {
            Shader = new Shader
            {
                Code = """
                    shader_type canvas_item;
                    uniform vec4 tint : source_color = vec4(1.0);
                    uniform float amount = 0.0;
                    void fragment() {
                        vec4 c = texture(TEXTURE, UV) * COLOR;
                        COLOR = vec4(mix(c.rgb, tint.rgb, amount), c.a);
                    }
                    """,
            },
        };
    }

    /// <summary>
    /// Plays move <paramref name="move"/>'s animation: <paramref name="user"/>'s Pokémon in <paramref name="userSlot"/>
    /// attacking the one in <paramref name="targetSlot"/> on the other side (or <paramref name="targetSide"/>), with
    /// <paramref name="width"/> Pokémon a side.
    /// </summary>
    public async Task Play(int move, BattleSide user, int userSlot = 0, BattleSide? targetSide = null, int targetSlot = 0, int width = 1)
    {
        _scripts ??= _assets?.LoadMoveAnimations() ?? [];
        if (move <= 0 || move >= _scripts.Count || _scripts[move].Count == 0)
            return;
        Playing = true;
        var (playerFeet, foeFeet) = _anchors();
        _view = new View(playerFeet, foeFeet);
        _width = Math.Max(1, width);
        _user = (user, userSlot);
        _target = (targetSide ?? Other(user), targetSlot);
        var peak = 0;
        var started = _frame;
        foreach (var c in _scripts[move])
        {
            if (!IsInstanceValid(this) || _frame - started > MaxScriptFrames)
                break;
            var a = c.AsSpan(1);
            switch (c[0])
            {
                case 0x06:
                    Particles(a[0]);
                    break;
                case 0x07 or 0x09:
                    Emit(a[0], a[1], a[2], a[3], a[4] / 4096f, 0, 0);
                    break;
                case 0x0C or 0x0E:
                    Emit(a[0], a[1], a[3], a[4], a[5] / 4096f, a.Length > 6 ? a[6] / 4096f : 0, a.Length > 7 ? a[7] / 4096f : 0);
                    break;
                case 0x0D:
                    Emit(a[0], a[1], a[6], 8, a[7] / 4096f, 0, 0);
                    break;
                case 0x08 or 0x0A or 0x0F:
                    Emit(a[0], a[1], 11, 8, 0, 0, 0);
                    break;
                case 0x12:
                    Shift(Battler(a[0]), a[2] / 4096f, a[3] / 4096f, a[4], a[6]);
                    break;
                case 0x15:
                    Squash(Battler(a[0]), a[2] / 4096f, a[3] / 4096f, a[4], a[6]);
                    break;
                case 0x1B:
                    Tint(Battler(a[0]), a[1], a[2], a[3], a[4]);
                    break;
                case 0x2A:
                    Fade(a[1], a[2], a[3], a[4]);
                    break;
                case 0x39:
                    await Frames(a[0]);
                    break;
                case 0x38:
                    for (var i = 0; i < 90 && _tasks.Count > 0; i++)
                        await Frames(1);
                    break;
            }
            peak = Math.Max(peak, Count());
        }
        // Let the effects run out (emitters that never end are stopped after a while).
        for (var i = 0; i < 150 && (_tasks.Count > 0 || _emitters.Count > 0) && IsInstanceValid(this); i++)
        {
            if (i == 75)
                foreach (var e in _emitters)
                    e.Stop();
            await Frames(1);
            peak = Math.Max(peak, Count());
        }
        if (!IsInstanceValid(this))
            return;
        Reset();
        Played.Add((move, peak));
        Playing = false;
    }

    int Count() => _emitters.Sum(e => e.Particles.Count + e.Children.Count);

    /// <summary>Puts the sprites back and clears the dimming, whatever the script left.</summary>
    void Reset()
    {
        _tasks.Clear();
        _emitters.Clear();
        foreach (var (sprite, pose) in _poses)
            if (IsInstanceValid(sprite))
            {
                sprite.Position -= pose.Offset;
                sprite.Scale = Vector2.One;
                sprite.Material = null;
            }
        _poses.Clear();
        _dim.Color = Colors.Transparent;
        QueueRedraw();
    }

    Task Frames(int n)
    {
        var done = new TaskCompletionSource();
        _waits.Add((_frame + Math.Max(1, n), done));
        return done.Task;
    }

    public override void _Process(double delta)
    {
        _clock += Math.Min(delta, 0.25);
        var stepped = false;
        while (_clock >= Tick)
        {
            _clock -= Tick;
            _frame++;
            Step();
            stepped = true;
        }
        if (stepped)
            QueueRedraw();
    }

    void Step()
    {
        for (var i = _tasks.Count - 1; i >= 0; i--)
            if (!_tasks[i]())
                _tasks.RemoveAt(i);
        for (var i = _emitters.Count - 1; i >= 0; i--)
            if (!_emitters[i].Update(_rng))
                _emitters.RemoveAt(i);
        for (var i = _waits.Count - 1; i >= 0; i--)
            if (_frame >= _waits[i].Frame)
            {
                var done = _waits[i].Done;
                _waits.RemoveAt(i);
                done.TrySetResult();
            }
    }

    // ---- Where things are -------------------------------------------------------------------------------------

    /// <summary>A point's place on screen and how many pixels one unit measures there.</summary>
    (Vector2 At, float PixelsPerUnit) Project(Vector3 point) => _view.Project(point);

    Vector3 Home((BattleSide Side, int Slot) b) => Home(b.Side, b.Slot, _width);

    static BattleSide Other(BattleSide side) => side == BattleSide.Player ? BattleSide.Foe : BattleSide.Player;

    /// <summary>Battle position 0–5: even ones are the player's side, slot = position / 2.</summary>
    (BattleSide, int) Position(int code) => (code % 2 == 0 ? BattleSide.Player : BattleSide.Foe, Math.Min(code / 2, _width - 1));

    /// <summary>A script's place code: 0–7 a battle position, 9/10 the attacker, else the defender.</summary>
    (BattleSide Side, int Slot) Place(int code) => code switch
    {
        >= 0 and <= 7 => Position(code),
        9 or 10 => _user,
        _ => _target,
    };

    /// <summary>A script's battler code: 14 the attacker, 15 its partner, 0–7 a position, 17 the defender's partner, else the defender.</summary>
    (BattleSide Side, int Slot) Battler(int code) => code switch
    {
        14 => _user,
        15 => (_user.Side, _width > 1 ? (_user.Slot == 0 ? 1 : 0) : _user.Slot),
        17 => (_target.Side, _width > 1 ? (_target.Slot == 0 ? 1 : 0) : _target.Slot),
        >= 0 and <= 7 => Position(code),
        _ => _target,
    };

    // ---- Particles ----------------------------------------------------------------------------------------------

    (SpaFile File, Texture2D?[] Textures)? Particles(int index)
    {
        if (_particleFiles.TryGetValue(index, out var cached))
            return cached;
        (SpaFile, Texture2D?[])? loaded = null;
        if (_assets?.LoadParticles(index) is { } file)
        {
            var textures = file.Textures.Select(t =>
            {
                var image = Image.LoadFromFile(_assets.ParticleImagePath(t.Image));
                return image is null || image.IsEmpty() ? null : (Texture2D)ImageTexture.CreateFromImage(image);
            }).ToArray();
            loaded = (file, textures);
        }
        _particleFiles[index] = loaded;
        return loaded;
    }

    /// <summary>
    /// Starts emitter <paramref name="index"/> of particle file <paramref name="file"/> at place <paramref name="from"/>
    /// (raised <paramref name="height"/>), aimed at place <paramref name="to"/> when that's another place (8 = none).
    /// With <paramref name="travel"/> frames the emitter itself flies there, arcing <paramref name="arc"/> units up.
    /// </summary>
    void Emit(int file, int index, int from, int to, float height, float travel, float arc)
    {
        if (Particles(file) is not { } spa || index < 0 || index >= spa.File.Emitters.Count)
            return;
        var start = Home(Place(from)) + new Vector3(0, height, 0);
        var resource = spa.File.Emitters[index];
        var axis = new Vector3(resource.Axis[0], resource.Axis[1], resource.Axis[2]);
        Vector3? end = null;
        if (to != 8 && Place(to) != Place(from))
        {
            var aim = Home(Place(to)) + new Vector3(0, height, 0);
            axis = (aim - start).Normalized();
            if (travel >= 1)
                end = aim;
        }
        _emitters.Add(new Emitter(resource, spa.Textures, start, axis.LengthSquared() > 0 ? axis.Normalized() : Vector3.Up, end, (int)travel, arc));
    }

    public override void _Draw()
    {
        foreach (var e in _emitters)
        {
            if (e.Resource.DrawChildrenFirst)
                DrawParticles(e, e.Children);
            if (!e.Resource.HideParent)
                DrawParticles(e, e.Particles);
            if (!e.Resource.DrawChildrenFirst)
                DrawParticles(e, e.Children);
        }
        DrawSetTransform(Vector2.Zero);
    }

    void DrawParticles(Emitter e, List<Particle> particles)
    {
        var r = e.Resource;
        foreach (var p in particles)
        {
            var alpha = (p.BaseAlpha * (p.AnimAlpha + 1)) >> 5;
            if (alpha <= 0 || p.Texture < 0 || p.Texture >= e.Textures.Length || e.Textures[p.Texture] is not { } texture)
                continue;
            var world = (p.Follow ? e.Position : p.Origin) + p.Position;
            var (at, ppu) = Project(world);
            var sy = p.BaseScale;
            var sx = sy * (p.Child ? 1 : r.AspectRatio);
            var dir = p.Child ? 0 : r.ScaleAnimDir;
            if (dir != 2)
                sx *= p.AnimScale;
            if (dir != 1)
                sy *= p.AnimScale;
            var angle = p.Rotation / 65536f * Mathf.Tau;
            var drawType = p.Child ? r.Child!.DrawType : r.DrawType;
            if (drawType is 1 or 3 && p.Velocity.LengthSquared() > 1e-8f)
            {
                // Directional: the quad points along the particle's motion on screen, stretched by its speed.
                var v = Project(world + p.Velocity).At - at;
                angle = v.Angle() + Mathf.Pi / 2;
                sy *= 1 + Math.Min(3, Math.Abs(r.DbbScale) * v.Length() / Math.Max(1, ppu));
            }
            var w = sx * 2 * ppu;
            var h = sy * 2 * ppu;
            if (w < 0.5f || h < 0.5f || w > 4000 || h > 4000)
                continue;
            var offset = p.Child ? Vector2.Zero : new Vector2(r.PolygonX * w / 2, -r.PolygonY * h / 2);
            DrawSetTransform(at, -angle);
            DrawTextureRect(texture, new Rect2(offset - new Vector2(w, h) / 2, w, h), false,
                new Color(p.Color.R * e.Tint.R, p.Color.G * e.Tint.G, p.Color.B * e.Tint.B, Math.Min(1, alpha / 31f)));
        }
    }

    // ---- Sprite effects --------------------------------------------------------------------------------------------

    TextureRect Pose((BattleSide Side, int Slot) b)
    {
        var sprite = _sprite(b.Side, b.Slot);
        if (!_poses.ContainsKey(sprite))
        {
            _poses[sprite] = (Vector2.Zero, Vector2.One);
            sprite.PivotOffset = new Vector2(sprite.Size.X / 2, sprite.Size.Y);
        }
        return sprite;
    }

    void Offset(TextureRect sprite, Vector2 offset)
    {
        var pose = _poses[sprite];
        sprite.Position += offset - pose.Offset;
        _poses[sprite] = (offset, pose.Scale);
    }

    /// <summary>Moves a Pokémon (dx, dy) units — dx toward the other side — and back, <paramref name="times"/> times.</summary>
    void Shift((BattleSide Side, int Slot) b, float dx, float dy, int frames, int times)
    {
        var sprite = Pose(b);
        var home = Home(b);
        var toward = b.Side == BattleSide.Player ? 1 : -1;
        var (at, _) = Project(home);
        var reach = Project(home + new Vector3(dx * toward, dy, 0)).At - at;
        var half = Math.Max(1, frames);
        var total = half * 2 * Math.Clamp(times, 1, 12);
        var t = 0;
        _tasks.Add(() =>
        {
            if (!IsInstanceValid(sprite))
                return false;
            t++;
            var phase = t % (half * 2) / (float)half;
            Offset(sprite, reach * (phase <= 1 ? phase : 2 - phase));
            if (t < total)
                return true;
            Offset(sprite, Vector2.Zero);
            return false;
        });
    }

    /// <summary>Squashes and stretches a Pokémon by (sx, sy) of its size and back, <paramref name="times"/> times.</summary>
    void Squash((BattleSide Side, int Slot) b, float sx, float sy, int frames, int times)
    {
        var sprite = Pose(b);
        var half = Math.Max(1, frames);
        var total = half * 2 * Math.Clamp(times, 1, 12);
        var t = 0;
        _tasks.Add(() =>
        {
            if (!IsInstanceValid(sprite))
                return false;
            t++;
            var phase = t % (half * 2) / (float)half;
            var k = phase <= 1 ? phase : 2 - phase;
            sprite.Scale = new Vector2(1 + sx * k, 1 + sy * k);
            if (t < total)
                return true;
            sprite.Scale = Vector2.One;
            return false;
        });
    }

    /// <summary>Blends a Pokémon toward <paramref name="color"/> (BGR555) from <paramref name="from"/>/16 to <paramref name="to"/>/16.</summary>
    void Tint((BattleSide Side, int Slot) b, int from, int to, int speed, int color)
    {
        var sprite = Pose(b);
        var material = (ShaderMaterial)_tintMaterial.Duplicate();
        material.SetShaderParameter("tint", Bgr(color));
        sprite.Material = material;
        var frames = Math.Max(1, Math.Abs(to - from) * Math.Max(1, speed) / 2);
        var t = 0;
        _tasks.Add(() =>
        {
            if (!IsInstanceValid(sprite))
                return false;
            t++;
            material.SetShaderParameter("amount", Mathf.Lerp(from, to, Math.Min(1f, t / (float)frames)) / 16f);
            if (t < frames)
                return true;
            if (to == 0)
                sprite.Material = null;
            return false;
        });
    }

    /// <summary>Fades the background toward <paramref name="color"/> from <paramref name="from"/>/16 to <paramref name="to"/>/16.</summary>
    void Fade(int from, int to, int speed, int color)
    {
        var tint = Bgr(color);
        var frames = Math.Max(1, Math.Abs(to - from) * (speed + 1) / 2);
        var t = 0;
        _tasks.Add(() =>
        {
            t++;
            var level = Mathf.Lerp(from, to, Math.Min(1f, t / (float)frames)) / 16f;
            _dim.Color = new Color(tint.R, tint.G, tint.B, Math.Min(0.65f, level * 0.75f));
            return t < frames;
        });
    }

    static Color Bgr(int c) => new((c & 31) / 31f, ((c >> 5) & 31) / 31f, ((c >> 10) & 31) / 31f);

    // ---- The particle simulation -----------------------------------------------------------------------------------

    sealed class Particle
    {
        public Vector3 Origin, Position, Velocity;
        public float BaseScale, AnimScale = 1;
        public Color Color;
        public int BaseAlpha, AnimAlpha = 31, Rotation, Spin, Life, Age, Texture, LoopOffset;
        public bool Follow, Child;
    }

    /// <summary>One running emitter: the DS particle library's update, for one resource.</summary>
    sealed class Emitter(SpaEmitter resource, Texture2D?[] textures, Vector3 position, Vector3 axis, Vector3? end, int travel, float arc)
    {
        public readonly SpaEmitter Resource = resource;
        public readonly Texture2D?[] Textures = textures;
        public readonly List<Particle> Particles = [], Children = [];
        public Vector3 Position = position;
        public Color Tint = Colors.White;
        readonly Vector3 _start = position, _axis = axis;
        int _age;
        bool _started, _stopped;
        float _carry;

        public void Stop() => _stopped = true;

        /// <summary>One frame; false once it has finished and its particles are gone.</summary>
        public bool Update(Random rng)
        {
            var r = Resource;
            if (end is { } target && travel > 0)
            {
                var k = Math.Min(1f, (_started ? _age : 0) / (float)travel);
                Position = _start.Lerp(target, k) + new Vector3(0, arc * 4 * k * (1 - k), 0);
            }
            if (!_started)
            {
                if (_age >= r.StartDelay)
                {
                    _started = true;
                    _age = 0;
                }
                else
                {
                    _age++;
                    return true;
                }
            }
            if (!_stopped && (r.EmitterLife == 0 || _age < r.EmitterLife) && _age % Math.Max(1, r.Interval) == 0)
                Spawn(rng);
            Advance(Particles, rng);
            Advance(Children, rng);
            _age++;
            var over = _stopped || (r.EmitterLife != 0 && _age > r.EmitterLife) || _age > 30 * 20;
            return !(over && Particles.Count == 0 && Children.Count == 0);
        }

        static float Attenuate(float value, int amount, Random rng) => value * (1 - amount / 255f * (float)rng.NextDouble());

        static Vector3 RandomUnit(Random rng)
        {
            var z = (float)rng.NextDouble() * 2 - 1;
            var a = (float)rng.NextDouble() * Mathf.Tau;
            var s = MathF.Sqrt(1 - z * z);
            return new Vector3(s * MathF.Cos(a), s * MathF.Sin(a), z);
        }

        (Vector3 U, Vector3 V, Vector3 N) CirclePlane() => Resource.CircleAxis switch
        {
            0 => (Vector3.Right, Vector3.Up, Vector3.Back),
            1 => (Vector3.Right, Vector3.Back, Vector3.Up),
            2 => (Vector3.Up, Vector3.Back, Vector3.Right),
            _ => Basis(_axis),
        };

        static (Vector3 U, Vector3 V, Vector3 N) Basis(Vector3 n)
        {
            var u = (Math.Abs(n.Y) < 0.9f ? Vector3.Up : Vector3.Right).Cross(n).Normalized();
            return (u, n.Cross(u), n);
        }

        void Spawn(Random rng)
        {
            var r = Resource;
            _carry += r.EmissionCount;
            var count = (int)_carry;
            _carry -= count;
            var (u, v, n) = CirclePlane();
            for (var i = 0; i < Math.Min(count, 64); i++)
            {
                var angle = (float)rng.NextDouble() * Mathf.Tau;
                var ring = u * MathF.Cos(angle) + v * MathF.Sin(angle);
                var rnd = (float)rng.NextDouble();
                var pos = r.EmissionType switch
                {
                    1 => RandomUnit(rng) * r.Radius,
                    2 => ring * r.Radius,
                    3 => (u * MathF.Cos(i * Mathf.Tau / count) + v * MathF.Sin(i * Mathf.Tau / count)) * r.Radius,
                    4 => RandomUnit(rng) * r.Radius * rnd,
                    5 => ring * r.Radius * rnd,
                    6 => ring * r.Radius + n * (float)rng.NextDouble() * r.Length,
                    7 => ring * r.Radius * rnd + n * (float)rng.NextDouble() * r.Length,
                    8 => Hemisphere(rng) * r.Radius,
                    9 => Hemisphere(rng) * r.Radius * (0.5f + rnd / 2),
                    _ => Vector3.Zero,
                };
                pos += new Vector3(r.Position[0], r.Position[1], r.Position[2]);
                var outward = r.EmissionType == 6 ? ring : pos.LengthSquared() > 1e-8f ? pos.Normalized() : RandomUnit(rng);
                var velocity = outward * Attenuate(r.VelocityFromPosition, r.RandomVelocity, rng)
                    + _axis * Attenuate(r.VelocityFromAxis, r.RandomVelocity, rng);
                var life = Math.Max(1, (int)Attenuate(r.ParticleLife, r.RandomLife, rng)) + 1;
                Particles.Add(new Particle
                {
                    Origin = Position, Position = pos, Velocity = velocity, Follow = r.FollowEmitter,
                    BaseScale = Attenuate(r.BaseScale, r.RandomScale, rng),
                    Color = Bgr(r.ColorAnim is { RandomStart: true } c ? new[] { c.Start, r.Color, c.End }[rng.Next(3)] : r.Color),
                    BaseAlpha = r.BaseAlpha, Texture = r.Texture, Life = life,
                    Rotation = r.RandomInitAngle ? rng.Next(65536) : r.InitAngle,
                    Spin = r.HasRotation ? rng.Next(Math.Min(r.MinRotation, r.MaxRotation), Math.Max(r.MinRotation, r.MaxRotation) + 1) : 0,
                    LoopOffset = r.RandomizeLoopedAnim ? rng.Next(256) : 0,
                });
            }
            Vector3 Hemisphere(Random random)
            {
                var d = RandomUnit(random);
                return d.Dot(_axis) < 0 ? -d : d;
            }
        }

        void Advance(List<Particle> list, Random rng)
        {
            var r = Resource;
            var air = (r.AirResistance + 384) / 512f;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var p = list[i];
                var rate = Math.Clamp(p.Age * 255 / Math.Max(1, p.Life), 0, 255);
                var loop = r.LoopFrames > 0 ? (p.LoopOffset + p.Age * 255 / r.LoopFrames) & 0xFF : rate;
                if (p.Child)
                {
                    var c = r.Child!;
                    if (c.ScaleToEnd)
                        p.AnimScale = 1 + (c.EndScale - 1) * rate / 255f;
                    if (c.FadeOut)
                        p.AnimAlpha = (255 - rate) * 31 / 255;
                }
                else
                {
                    if (r.ScaleAnim is { } s)
                        p.AnimScale = Curve(s.Loop ? loop : rate, s.In, s.Out, s.Start, s.Mid, s.End);
                    if (r.ColorAnim is { RandomStart: false } c)
                        p.Color = ColorAt(c.Loop ? loop : rate, c, r.Color);
                    if (r.AlphaAnim is { } a)
                        p.AnimAlpha = (int)Curve(a.Loop ? loop : rate, a.In, a.Out, a.Start, a.Mid, a.End);
                    if (r.TexAnim is { Frames.Count: > 0 } t)
                    {
                        var at = t.Loop ? loop : rate;
                        for (var f = 0; f < t.Frames.Count; f++)
                            if (at < t.Step * (f + 1))
                            {
                                p.Texture = t.Frames[f];
                                break;
                            }
                    }
                }

                var behave = !p.Child || r.Child!.UsesBehaviors;
                var acc = Vector3.Zero;
                if (behave)
                {
                    if (r.Gravity is { } g)
                        acc += new Vector3(g[0], g[1], g[2]);
                    if (r.Random is { } rnd && rnd.Interval > 0 && p.Age % rnd.Interval == 0)
                        acc += new Vector3(rnd.Magnitude[0] * Sym(rng), rnd.Magnitude[1] * Sym(rng), rnd.Magnitude[2] * Sym(rng));
                    if (r.Magnet is { } m)
                        acc += (new Vector3(m.Point[0], m.Point[1], m.Point[2]) - p.Position - p.Velocity) * m.Force;
                    if (r.Spin is { } spin)
                        p.Position = p.Position.Rotated(spin.Axis switch { 0 => Vector3.Right, 2 => Vector3.Back, _ => Vector3.Up }, spin.Angle / 65536f * Mathf.Tau);
                    if (r.Convergence is { } cv)
                        p.Position += (new Vector3(cv.Point[0], cv.Point[1], cv.Point[2]) - p.Position) * cv.Force;
                }
                p.Rotation = (p.Rotation + p.Spin) & 0xFFFF;
                if (!p.Child)
                    p.Velocity *= air;
                p.Velocity += acc;
                p.Position += p.Velocity;
                if (behave && r.Plane is { } plane && p.Position.Y < plane.Y)
                {
                    if (plane.Type == 0)
                        p.Age = p.Life;
                    else
                    {
                        p.Position.Y = plane.Y;
                        p.Velocity.Y = -p.Velocity.Y * plane.Elasticity;
                    }
                }
                if (!p.Child && r.Child is { } child)
                {
                    var delay = p.Life * child.Delay / 256;
                    if (p.Age - delay >= 0 && (p.Age - delay) % Math.Max(1, child.Interval) == 0)
                        for (var k = 0; k < child.Count; k++)
                            Children.Add(new Particle
                            {
                                Origin = p.Follow ? Position : p.Origin, Position = p.Position, Follow = p.Follow, Child = true,
                                Velocity = p.Velocity * child.VelocityRatio / 256f
                                    + new Vector3(Sym(rng), Sym(rng), Sym(rng)) * child.RandomVelocity,
                                BaseScale = p.BaseScale * p.AnimScale * (child.ScaleRatio + 1) / 64f,
                                Color = child.UseChildColor ? Bgr(child.Color) : p.Color,
                                BaseAlpha = p.BaseAlpha * (p.AnimAlpha + 1) >> 5, Texture = child.Texture,
                                Life = Math.Max(1, child.Life), Rotation = child.RotationType == 0 ? 0 : p.Rotation,
                            });
                }
                p.Age++;
                if (p.Age > p.Life)
                    list.RemoveAt(i);
            }
        }

        static float Sym(Random rng) => (float)rng.NextDouble() * 2 - 1;

        /// <summary>The three-point curve the scale and alpha animations use: start → mid by <paramref name="inAt"/>, mid until <paramref name="outAt"/>, then → end.</summary>
        static float Curve(int rate, int inAt, int outAt, float start, float mid, float end)
        {
            if (rate < inAt)
                return start + (mid - start) * rate / Math.Max(1, inAt);
            if (rate < outAt)
                return mid;
            return mid + (end - mid) * (rate - outAt) / Math.Max(1, 255 - outAt);
        }

        static Color ColorAt(int rate, SpaColorAnim c, int header)
        {
            if (rate < c.In)
                return Bgr(c.Start);
            if (rate < c.Peak)
                return c.Interpolate ? Bgr(c.Start).Lerp(Bgr(header), (rate - c.In) / (float)Math.Max(1, c.Peak - c.In)) : Bgr(header);
            if (rate < c.Out)
                return c.Interpolate ? Bgr(header).Lerp(Bgr(c.End), (rate - c.Peak) / (float)Math.Max(1, c.Out - c.Peak)) : Bgr(c.End);
            return Bgr(c.End);
        }
    }
}
