using Godot;
using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// Water and the wild Pokémon phenomena: Surf (facing water with a Pokémon that knows Surf or is Water-type), wild
/// Pokémon while surfing, the Super Rod, and the spots that appear now and then — shaking grass, rippling water and
/// cave dust clouds — whose special encounter slots (or, for dust, a gem) the player gets by stepping in.
/// </summary>
public partial class OverworldScreen
{
    public const int SurfMove = 57, SuperRod = Core.Player.StarterKit.SuperRod;

    enum Phenomenon { ShakingGrass, RipplingWater, DustCloud }

    bool _surfing;
    (Vector2I Tile, Phenomenon Kind, Node3D Marker)? _phenomenon;

    /// <summary>True while the player is surfing; for smoke tests.</summary>
    public bool Surfing => _surfing;

    /// <summary>The tile of the shaking grass, rippling water or dust cloud now showing, if any; for smoke tests.</summary>
    public Vector2I? PhenomenonTile => _phenomenon?.Tile;

    public bool IsWater(Vector2I t) => (TileFlags(t) & 4) != 0;

    /// <summary>Whether the player may step onto <paramref name="t"/>: water only while surfing (land ends it).</summary>
    bool CanEnter(Vector2I t) => _surfing ? IsWater(t) || !IsBlocked(t) : !IsBlocked(t);

    /// <summary>The party Pokémon that can carry the player across water: one that knows Surf, else a Water type.</summary>
    static Pokemon? Surfer() =>
        OfflineSession.Party.FirstOrDefault(p => !p.Fainted && p.Moves.Any(m => m.Move.Id == SurfMove))
        ?? OfflineSession.Party.FirstOrDefault(p => !p.Fainted && p.Types.Contains(TypeChart.Water));

    /// <summary>Facing water on land: a Pokémon carries the player onto it. False when nobody can.</summary>
    bool TrySurf()
    {
        if (_surfing || !_battles || !IsWater(_tile + _dir) || NpcAt(_tile + _dir) is not null)
            return false;
        if (Surfer() is not { } surfer)
        {
            ShowMessage("The water is dyed a deep blue…\nA Pokémon that knows Surf could carry you across.");
            return true;
        }
        ShowMessage($"{surfer.Name} used Surf!");
        SetSurfing(true);
        Move(_tile + _dir, run: false);
        return true;
    }

    void SetSurfing(bool on)
    {
        if (_surfing == on)
            return;
        _surfing = on;
        _sprite.QueueFree();
        _sprite = BuildSprite(_assets.OverworldSpritePath(on ? PlayerCharacter + "-surf" : PlayerCharacter));
        _player.AddChild(_sprite);
        ShowFrame(0);
    }

    /// <summary>After each step: getting off the water, and phenomena appearing or being stepped into.</summary>
    void AfterWaterStep()
    {
        if (_surfing && !IsWater(_tile))
            SetSurfing(false);
        if (_phenomenon is { } p && (p.Tile - _tile).LengthSquared() > 14 * 14)
            ClearPhenomenon();
        if (_phenomenon is null && _battles && RandomEncounters && OfflineSession.Rng.Next(100) < 2)
            SpawnPhenomenon();
    }

    void ClearPhenomenon()
    {
        if (_phenomenon is { } p && IsInstanceValid(p.Marker))
            p.Marker.QueueFree();
        _phenomenon = null;
    }

    EncounterSet? EncountersHere() =>
        _zones.TryGetValue(_zone, out var zone) && zone.Encounters >= 0 && zone.Encounters < OfflineSession.Encounters.Count
            ? OfflineSession.Encounters[zone.Encounters].ForSeason((int)_season) : null;

    /// <summary>Shaking grass (outdoors, in tall grass), a dust cloud (caves) or rippling water (while surfing) near the player.</summary>
    void SpawnPhenomenon()
    {
        if (EncountersHere() is not { } set)
            return;
        var outdoor = _map.Key == _assets.LoadOverworld()?.Key;
        Phenomenon kind;
        Func<Vector2I, bool> fits;
        if (_surfing && set.SurfSpotRate > 0)
            (kind, fits) = (Phenomenon.RipplingWater, IsWater);
        else if (outdoor && set.ShakingGrassRate > 0)
            (kind, fits) = (Phenomenon.ShakingGrass, IsTallGrass);
        else if (!outdoor && set.ShakingGrassRate > 0)
            (kind, fits) = (Phenomenon.DustCloud, t => !IsBlocked(t) && !IsWater(t));
        else
            return;
        var rng = OfflineSession.Rng;
        for (var tries = 0; tries < 40; tries++)
        {
            var tile = _tile + new Vector2I(rng.Next(-7, 8), rng.Next(-7, 8));
            if ((tile - _tile).LengthSquared() < 9 || !fits(tile) || NpcAt(tile) is not null || _warps.ContainsKey(tile))
                continue;
            var marker = PhenomenonMarker(kind);
            _world.AddChild(marker);
            marker.Position = TileCenter(tile, GroundHeight(tile, _height));
            _phenomenon = (tile, kind, marker);
            return;
        }
    }

    /// <summary>A small particle effect: leaves flying out of the grass, rings on the water, puffs of dust.</summary>
    static Node3D PhenomenonMarker(Phenomenon kind)
    {
        var color = kind switch
        {
            Phenomenon.ShakingGrass => new Color(0.35f, 0.75f, 0.3f),
            Phenomenon.RipplingWater => new Color(0.9f, 0.97f, 1f, 0.9f),
            _ => new Color(0.75f, 0.68f, 0.55f, 0.85f),
        };
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = color, VertexColorUseAsAlbedo = true,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        var particles = new CpuParticles3D
        {
            Amount = kind == Phenomenon.DustCloud ? 24 : 12,
            Lifetime = 0.9,
            Mesh = new QuadMesh { Size = new Vector2(0.18f, 0.18f), Material = material },
            Direction = Vector3.Up,
            Spread = kind == Phenomenon.RipplingWater ? 80 : 35,
            InitialVelocityMin = 0.6f,
            InitialVelocityMax = kind == Phenomenon.DustCloud ? 1.6f : 1.2f,
            Gravity = new Vector3(0, -2.5f, 0),
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.35f,
        };
        particles.Position = new Vector3(0, 0.15f, 0);
        var node = new Node3D();
        node.AddChild(particles);
        return node;
    }

    /// <summary>
    /// A wild Pokémon for this step from the water or a phenomenon, or null to fall back to tall grass. Stepping into a
    /// dust cloud may turn up a gem instead (it's put in the Bag and announced).
    /// </summary>
    Pokemon? WaterOrPhenomenonEncounter(out bool handled)
    {
        handled = false;
        if (EncountersHere() is not { } set)
            return null;
        var rng = OfflineSession.Rng;
        if (_phenomenon is { } p && p.Tile == _tile)
        {
            handled = true;
            ClearPhenomenon();
            if (p.Kind == Phenomenon.DustCloud && rng.Next(100) < 40)
            {
                var gem = HeldItems.FireGem + rng.Next(HeldItems.NormalGem - HeldItems.FireGem + 1);
                OfflineSession.Bag.Add(gem);
                ShowMessage($"You found a {OfflineSession.Data.ItemName(gem)} in the dust!");
                return null;
            }
            var (slots, weights) = p.Kind == Phenomenon.RipplingWater
                ? (set.SurfSpots, EncounterSet.SurfWeights) : (set.ShakingGrass, EncounterSet.GrassWeights);
            return Wild(slots, weights) ?? Wild(set.Grass, EncounterSet.GrassWeights);
        }
        if (_surfing && IsWater(_tile))
        {
            handled = true;
            return set.SurfRate > 0 && rng.Next(100) < set.SurfRate ? Wild(set.Surf, EncounterSet.SurfWeights) : null;
        }
        return null;
    }

    Pokemon? Wild(IReadOnlyList<EncounterSlot> slots, IReadOnlyList<int> weights)
    {
        var rng = OfflineSession.Rng;
        if (EncounterSet.Pick(slots, weights, rng) is not { } slot)
            return null;
        var level = rng.Next(slot.MinLevel, slot.MaxLevel + 1);
        if (OfflineSession.State.RepelSteps > 0 && OfflineSession.Lead is { } lead && level < lead.Level)
            return null;
        return Pokemon.Create(OfflineSession.Data, slot.Species, level, rng);
    }

    /// <summary>
    /// The Super Rod, used from the Bag while facing water: a bite (the zone's fishing rate) brings a battle. Public so
    /// smoke tests can fish.
    /// </summary>
    public async void Fish()
    {
        if (_menu is not null)
            ToggleMenu();
        if (_moving || !IsWater(_tile + _dir))
        {
            ShowMessage("You can't use that here.");
            return;
        }
        var set = EncountersHere();
        var ripple = _phenomenon is { Kind: Phenomenon.RipplingWater } p && p.Tile == _tile + _dir;
        _moving = true;
        await Say(["You cast your Super Rod…", "… … …"]);
        if (!IsInstanceValid(this))
            return;
        var rng = OfflineSession.Rng;
        var wild = set is null ? null
            : ripple ? Wild(set.FishSpots, EncounterSet.FishWeights)
            : set.FishRate > 0 && rng.Next(100) < Math.Max(set.FishRate, 50) ? Wild(set.Fish, EncounterSet.FishWeights) : null;
        if (ripple)
            ClearPhenomenon();
        if (wild is null)
        {
            _moving = false;
            ShowMessage("Not even a nibble…");
            return;
        }
        await Say(["Oh! A bite!"]);
        if (IsInstanceValid(this))
            StartBattle(wild);
    }
}
