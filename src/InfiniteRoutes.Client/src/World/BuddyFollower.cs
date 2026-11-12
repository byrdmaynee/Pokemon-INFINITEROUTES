using Godot;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// An Active Buddy: a party Pokémon walking one tile behind its trainer (the player's own, or another player's).
/// With HeartGold or SoulSilver extracted, it's that game's walking sprite for the species (#1–493; shiny and female
/// looks too), stepping through its two frames per direction. Otherwise — and for Pokémon after #493, which no DS game
/// drew walking — it's the battle sprite standing on the ground: the front walking toward the camera or sideways
/// (turned the way it goes), the back walking away, with a little hop each step.
/// </summary>
public sealed partial class BuddyFollower : Node3D
{
    /// <summary>World units per pixel for battle sprites: a 96-pixel canvas spans about two and a half tiles.</summary>
    const float BattlePixelSize = 1f / 36;

    readonly GameAssets _assets;
    readonly Sprite3D _sprite;
    ImageTexture? _front, _back, _sheet;
    Tween? _tween;
    int _step;
    Facing _facing;

    public int Species { get; private set; }
    public bool Shiny { get; private set; }
    public bool Female { get; private set; }
    public Vector2I Tile { get; private set; }

    /// <summary>Whether it's a real walking sprite (not the battle-sprite stand-in); for smoke tests.</summary>
    public bool Walking => _sheet is not null;

    public BuddyFollower() : this(null!) { }

    public BuddyFollower(GameAssets assets)
    {
        _assets = assets;
        // Drawn like the player and NPCs: facing the camera, depth-tested as if standing upright.
        _sprite = CharacterSprite.Create(null);
        _sprite.Hframes = _sprite.Vframes = 1;
        AddChild(_sprite);
        Visible = false;
    }

    /// <summary>The lowest row each texture draws anything on (battle sprites leave different space below the Pokémon).</summary>
    readonly Dictionary<ImageTexture, int> _feet = [];

    ImageTexture? Load(string path)
    {
        if (!File.Exists(path) || Image.LoadFromFile(path) is not { } image)
            return null;
        var texture = ImageTexture.CreateFromImage(image);
        var used = image.GetUsedRect();
        _feet[texture] = used.Size.Y > 0 ? used.End.Y : image.GetHeight();
        return texture;
    }

    /// <summary>The walking sheet for a Pokémon from HeartGold/SoulSilver, if there is one.</summary>
    static string? SheetPath(int species, bool shiny, bool female)
    {
        if (ClientServices.FollowerSprites is not { } dir || species is < 1 or > Gen4Followers.LastSpecies)
            return null;
        var name = female && Gen4Followers.FemaleLooks.Contains(species) ? $"{species}_1" : $"{species}";
        var path = Path.Combine(dir, name + (shiny ? "s" : "") + ".png");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Which Pokémon follows (species 0: none, and the follower hides).</summary>
    public void SetPokemon(int species, bool shiny, bool female = false)
    {
        if (species == Species && shiny == Shiny && female == Female)
            return;
        (Species, Shiny, Female) = (species, shiny, female);
        _front = _back = _sheet = null;
        if (species <= 0 || _assets is null)
        {
            Visible = false;
            return;
        }
        if (SheetPath(species, shiny, female) is { } sheet && Image.LoadFromFile(sheet) is { } image)
        {
            _sheet = ImageTexture.CreateFromImage(image);
            var size = image.GetWidth() / 2;
            _sprite.Texture = _sheet;
            _sprite.Hframes = 2;
            _sprite.Vframes = 4;
            // 32 px frames on the 16 px tile grid, like the player; the feet sit near the frame's bottom.
            _sprite.PixelSize = 1f / 16;
            _sprite.FlipH = false;
            _sprite.Offset = new Vector2(0, size / 2f - 2);
            if (_sprite.MaterialOverride is ShaderMaterial material)
                material.SetShaderParameter("sheet", _sheet);
        }
        else
        {
            _front = Load(_assets.BattleSpritePath(species, shiny ? "front-shiny" : "front"));
            _back = Load(_assets.BattleSpritePath(species, shiny ? "back-shiny" : "back")) ?? _front;
            _sprite.Hframes = _sprite.Vframes = 1;
            _sprite.PixelSize = BattlePixelSize;
        }
        Face(Facing.Down);
        Visible = _sheet is not null || _front is not null;
    }

    void Face(Facing facing)
    {
        _facing = facing;
        if (_sheet is not null)
        {
            _sprite.Frame = (int)facing * 2 + _step % 2;
            return;
        }
        _sprite.Texture = facing == Facing.Up ? _back : _front;
        if (_sprite.MaterialOverride is ShaderMaterial material)
            material.SetShaderParameter("sheet", _sprite.Texture);
        // Front sprites look to the left; turned around to walk right.
        _sprite.FlipH = facing == Facing.Right;
        // The lowest drawn row stands on the ground.
        if (_sprite.Texture is ImageTexture t)
            _sprite.Offset = new Vector2(0, _feet.GetValueOrDefault(t, t.GetHeight()) - t.GetHeight() / 2f);
    }

    /// <summary>Stands it on <paramref name="tile"/> at once (arriving somewhere, a jump).</summary>
    public void Place(Vector2I tile, Facing facing, float height)
    {
        _tween?.Kill();
        Tile = tile;
        Position = Center(tile, height);
        _sprite.Position = Vector3.Zero;
        Face(facing);
    }

    /// <summary>
    /// Walks it to <paramref name="tile"/> (where its trainer just was) over <paramref name="seconds"/>, facing the way
    /// it goes — round a corner that's still towards the corner, not where its trainer has already turned.
    /// <paramref name="facing"/> (the trainer's) is only for when it doesn't move in a straight line.
    /// </summary>
    public void StepTo(Vector2I tile, Facing facing, float height, double seconds)
    {
        if (tile == Tile)
            return;
        if (Math.Abs(tile.X - Tile.X) + Math.Abs(tile.Y - Tile.Y) > 2)
        {
            Place(tile, facing, height);
            return;
        }
        facing = WayOf(tile - Tile) ?? facing;
        Tile = tile;
        _step++;
        Face(facing);
        _tween?.Kill();
        _tween = CreateTween();
        _tween.TweenProperty(this, "position", Center(tile, height), seconds);
        if (_sheet is not null)
        {
            // Two frames per direction, as the games walk them: the other frame halfway through the step.
            _tween.Parallel().TweenCallback(Callable.From(() =>
            {
                _step++;
                Face(_facing);
            })).SetDelay(seconds / 2);
            return;
        }
        // The battle-sprite stand-in hops a little each step.
        _tween.Parallel().TweenMethod(Callable.From<float>(t => _sprite.Position = new Vector3(0, MathF.Sin(t * MathF.PI) * 0.18f, 0)), 0f, 1f, seconds);
    }

    static Vector3 Center(Vector2I tile, float height) => new(tile.X + 0.5f, height, tile.Y + 0.5f);

    /// <summary>The facing for a straight move (tiles: x right, y down), or null for a diagonal or none.</summary>
    public static Facing? WayOf(Vector2I move) => move switch
    {
        { X: 0, Y: < 0 } => Facing.Up,
        { X: 0, Y: > 0 } => Facing.Down,
        { X: < 0, Y: 0 } => Facing.Left,
        { X: > 0, Y: 0 } => Facing.Right,
        _ => null,
    };
}
