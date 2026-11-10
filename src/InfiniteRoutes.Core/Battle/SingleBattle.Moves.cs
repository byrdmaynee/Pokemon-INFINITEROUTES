using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Core.Battle;

/// <summary>Move ids (the ROM's move table, national order) of moves with effects of their own.</summary>
public static class Moves
{
    public const int PayDay = 6, RazorWind = 13, Gust = 16, Whirlwind = 18, Fly = 19, Bind = 20, JumpKick = 26, Roar = 46,
        SonicBoom = 49, Disable = 50, Mist = 54, Surf = 57, HyperBeam = 63, LowKick = 67, Counter = 68, SeismicToss = 69,
        Toxic = 92, DragonRage = 82, Earthquake = 89, Fissure = 90, Dig = 91, Teleport = 100, NightShade = 101,
        Haze = 114, Reflect = 115, LightScreen = 113, FocusEnergy = 116, Metronome = 118, MirrorMove = 119, SelfDestruct = 120,
        SkullBash = 130, HighJumpKick = 136, DreamEater = 138, SkyAttack = 143, Psywave = 149, Explosion = 153, Rest = 156,
        SuperFang = 162, Substitute = 164, Thief = 168, MindReader = 170, Snore = 173, Curse = 174, Flail = 175,
        Reversal = 179, Protect = 182, BellyDrum = 187, Spikes = 191, Foresight = 193, PerishSong = 195, Detect = 197,
        LockOn = 199, Outrage = 200, Endure = 203, Rollout = 205, FalseSwipe = 206, FuryCutter = 210, SleepTalk = 214,
        HealBell = 215, Return = 216, Present = 217, Frustration = 218, Safeguard = 219, PainSplit = 220, Magnitude = 222,
        BatonPass = 226, RapidSpin = 229, Twister = 239, MirrorCoat = 243, PsychUp = 244, Whirlpool = 250, FakeOut = 252,
        Facade = 263, FocusPunch = 264, SmellingSalt = 265, Charge = 268, Taunt = 269, Trick = 271, RolePlay = 272, Wish = 273,
        Ingrain = 275, Recycle = 278, Revenge = 279, KnockOff = 282, Endeavor = 283, Eruption = 284, SkillSwap = 285,
        Refresh = 287, Dive = 291, PoisonFang = 305, BlastBurn = 307, HydroCannon = 308, Aromatherapy = 312, IceBall = 301,
        WaterSpout = 323, SkyUppercut = 327, FrenzyPlant = 338, Bounce = 340, Covet = 343, MiracleEye = 357, WakeUpSlap = 358,
        GyroBall = 360, Brine = 362, Feint = 364, Tailwind = 366, MetalBurst = 368, UTurn = 369, Payback = 371, Assurance = 372,
        TrumpCard = 376, WringOut = 378, GastroAcid = 380, Punishment = 386, WorrySeed = 388, SuckerPunch = 389,
        ToxicSpikes = 390, AquaRing = 392, MagnetRise = 393, Switcheroo = 415, GigaImpact = 416, Avalanche = 419,
        Defog = 432, TrickRoom = 433, RockWrecker = 439, StealthRock = 446, GrassKnot = 447, RoarOfTime = 459,
        CrushGrip = 462, ShadowForce = 467, Venoshock = 474, SmackDown = 479, HeavySlam = 484, ElectroBall = 486, Soak = 487,
        SimpleBeam = 493, Entrainment = 494, StoredPower = 500, Hex = 506, Acrobatics = 512, ReflectType = 513,
        FinalGambit = 515, VoltSwitch = 521, HeatCrash = 535, Hurricane = 542, FreezeShock = 553, IceBurn = 554,
        Thrash = 37, PetalDance = 80, SolarBeam = 76, HiddenPower = 237;

    /// <summary>Moves that charge on the first turn and strike on the second.</summary>
    public static bool TwoTurn(int move) => move is RazorWind or Fly or Dig or Dive or Bounce or SolarBeam or SkullBash
        or SkyAttack or ShadowForce or FreezeShock or IceBurn;

    /// <summary>Moves whose user must rest the turn after.</summary>
    public static bool Recharges(int move) => move is HyperBeam or GigaImpact or BlastBurn or HydroCannon or FrenzyPlant
        or RockWrecker or RoarOfTime;

    /// <summary>Thrash-style moves: the user keeps attacking for 2–3 turns, then becomes confused.</summary>
    public static bool Rampage(int move) => move is Thrash or PetalDance or Outrage;

    public static bool ProtectLike(int move) => move is Protect or Detect or Endure;
}

/// <summary>Screens, hazards and timers that belong to one side of the field.</summary>
public sealed class SideState
{
    public int Reflect, LightScreen, Mist, Safeguard, Tailwind, Spikes, ToxicSpikes, WishTurns, WishHeal;
    public bool StealthRock;
}

/// <summary>Moves with effects of their own: fixed and variable damage, two-turn and recharge moves, Protect,
/// Substitute, screens, hazards, trapping, Leech Seed, Toxic, item and ability swaps, switching moves and more.</summary>
public sealed partial class SingleBattle
{
    readonly SideState[] _sides = [new(), new()];
    int _trickRoom;
    /// <summary>The foe's move still to come in a turn where the player's U-turn, Volt Switch or Baton Pass switched out.</summary>
    (MoveInfo Move, int Slot)? _pendingFoeMove;
    /// <summary>Baton Pass: what the next Pokémon sent out inherits.</summary>
    Battler? _batonPass;

    SideState SideOf(Battler b) => _sides[(int)b.Side];

    static string Team(Battler b) => b.Side == BattleSide.Player ? "your team" : "the foe's team";

    /// <summary>True while the player's Pokémon is charging, recharging or rampaging: its next turn's move is fixed.</summary>
    public bool PlayerLocked => Locked(Player);

    /// <summary>The Pokémon is charging, recharging or rampaging: its move this turn is chosen for it.</summary>
    public static bool Locked(Battler b) => b.Charging != 0 || b.Recharging || b.LockedTurns > 0 || b.Rollout > 0;

    /// <summary>The move a locked Pokémon must use this turn (slot −1: no PP is spent).</summary>
    (MoveInfo Move, int Slot)? Forced(Battler b)
    {
        var id = b.Charging != 0 ? b.Charging : b.LockedTurns > 0 ? b.LockedMove : b.Rollout > 0 ? b.LastMove
            : b.Recharging ? b.LastMove : 0;
        return id != 0 && _data.Move(id) is { } move ? (move, -1) : null;
    }

    /// <summary>Hidden Power's type from the IVs' lowest bits: Fighting … Dark.</summary>
    static int HiddenPowerType(Pokemon mon)
    {
        int[] order = [0, 1, 2, 5, 3, 4];   // HP, Atk, Def, Spe, SpA, SpD
        var sum = 0;
        for (var i = 0; i < 6; i++)
            sum += (mon.Ivs[order[i]] & 1) << i;
        return 1 + sum * 15 / 63;
    }

    static int HiddenPowerPower(Pokemon mon)
    {
        int[] order = [0, 1, 2, 5, 3, 4];
        var sum = 0;
        for (var i = 0; i < 6; i++)
            sum += ((mon.Ivs[order[i]] >> 1) & 1) << i;
        return 30 + sum * 40 / 63;
    }

    /// <summary>Fixed-damage moves; null for ordinary ones.</summary>
    int? FixedDamage(Battler user, Battler target, MoveInfo move) => move.Id switch
    {
        Moves.SonicBoom => 20,
        Moves.DragonRage => 40,
        Moves.SeismicToss or Moves.NightShade => user.Mon.Level,
        Moves.SuperFang => Math.Max(1, target.Mon.Hp / 2),
        Moves.Psywave => Math.Max(1, user.Mon.Level * (50 + _rng.Next(101)) / 100),
        Moves.Endeavor => target.Mon.Hp - user.Mon.Hp,
        Moves.FinalGambit => user.Mon.Hp,
        Moves.Counter => user.TakenPhysical * 2,
        Moves.MirrorCoat => user.TakenSpecial * 2,
        Moves.MetalBurst => (user.TakenPhysical + user.TakenSpecial) * 3 / 2,
        _ => null,
    };

    /// <summary>Power that depends on the battle: weight, HP, speed, status, stat stages, friendship, timing.</summary>
    int MovePower(Battler user, Battler target, MoveInfo move, bool movedFirst)
    {
        var power = move.Power;
        int Weight(Battler b) => b.Mon.Species.WeightHectograms;
        int Positive(Battler b) => b.Stages.Where(s => s > 0).Sum();
        switch (move.Id)
        {
            case Moves.LowKick or Moves.GrassKnot:
                var kg = Weight(target) / 10;
                return kg < 10 ? 20 : kg < 25 ? 40 : kg < 50 ? 60 : kg < 100 ? 80 : kg < 200 ? 100 : 120;
            case Moves.HeavySlam or Moves.HeatCrash:
                var ratio = Weight(user) / Math.Max(1, Weight(target));
                return ratio >= 5 ? 120 : ratio >= 4 ? 100 : ratio >= 3 ? 80 : ratio >= 2 ? 60 : 40;
            case Moves.Flail or Moves.Reversal:
                var p = 48 * user.Mon.Hp / user.Mon.MaxHp;
                return p >= 33 ? 20 : p >= 17 ? 40 : p >= 10 ? 80 : p >= 5 ? 100 : p >= 2 ? 150 : 200;
            case Moves.Eruption or Moves.WaterSpout:
                return Math.Max(1, 150 * user.Mon.Hp / user.Mon.MaxHp);
            case Moves.WringOut or Moves.CrushGrip:
                return Math.Max(1, 120 * target.Mon.Hp / target.Mon.MaxHp);
            case Moves.GyroBall:
                return Math.Min(150, 25 * EffectiveSpeed(target) / Math.Max(1, EffectiveSpeed(user)) + 1);
            case Moves.ElectroBall:
                var speeds = EffectiveSpeed(user) / Math.Max(1, EffectiveSpeed(target));
                return speeds >= 4 ? 150 : speeds >= 3 ? 120 : speeds >= 2 ? 80 : 60;
            case Moves.Return:
                return Math.Max(1, user.Mon.Species.BaseFriendship * 2 / 5);
            case Moves.Frustration:
                return Math.Max(1, (255 - user.Mon.Species.BaseFriendship) * 2 / 5);
            case Moves.HiddenPower:
                return HiddenPowerPower(user.Mon);
            case Moves.StoredPower:
                return 20 + 20 * Positive(user);
            case Moves.Punishment:
                return Math.Min(200, 60 + 20 * Positive(target));
            case Moves.Magnitude:
                return _rng.Next(100) switch { < 5 => 10, < 15 => 30, < 35 => 50, < 65 => 70, < 85 => 90, < 95 => 110, _ => 150 };
            case Moves.Present:
                return _rng.Next(3) switch { 0 => 40, 1 => 80, _ => 120 };
            case Moves.FuryCutter:
                return Math.Min(160, power << Math.Min(user.FuryCutter, 2));
            case Moves.Rollout or Moves.IceBall:
                return power << Math.Min(user.Rollout, 4);
            case Moves.TrumpCard:
                var left = user.Mon.Moves.FirstOrDefault(m => m.Move.Id == move.Id)?.Pp ?? 0;
                return left switch { 0 => 200, 1 => 80, 2 => 60, 3 => 50, _ => 40 };
        }
        var doubled = move.Id switch
        {
            Moves.Facade => user.Mon.Status is Ailment.Burn or Ailment.Poison or Ailment.Paralysis,
            Moves.Hex => target.Mon.Status != Ailment.None,
            Moves.Venoshock => target.Mon.Status == Ailment.Poison,
            Moves.Brine => target.Mon.Hp * 2 <= target.Mon.MaxHp,
            Moves.Payback => !movedFirst,
            Moves.Revenge or Moves.Avalanche => user.TakenPhysical + user.TakenSpecial > 0,
            Moves.Assurance => target.TakenPhysical + target.TakenSpecial > 0,
            Moves.Acrobatics => user.Mon.HeldItem == 0,
            Moves.SmellingSalt => target.Mon.Status == Ailment.Paralysis,
            Moves.WakeUpSlap => target.Mon.Status == Ailment.Sleep,
            Moves.Gust or Moves.Twister => target.SemiInvulnerable is Moves.Fly or Moves.Bounce,
            Moves.Earthquake or Moves.Magnitude => target.SemiInvulnerable == Moves.Dig,
            Moves.Surf or Moves.Whirlpool => target.SemiInvulnerable == Moves.Dive,
            _ => false,
        };
        if (doubled)
            power *= 2;
        if (user.Charged && move.Type == TypeChart.Electric)
            power *= 2;
        return power;
    }

    /// <summary>A semi-invulnerable target (flying, underground, underwater, vanished) is out of reach of most moves.</summary>
    static bool CanReach(Battler user, Battler target, MoveInfo move)
    {
        if (target.SemiInvulnerable == 0 || Has(user, Abilities.NoGuard) || Has(target, Abilities.NoGuard) || user.LockOn > 0)
            return true;
        return target.SemiInvulnerable switch
        {
            Moves.Fly or Moves.Bounce => move.Id is Moves.Gust or Moves.Twister or SingleBattle.Thunder or Moves.SkyUppercut
                or Moves.Hurricane or Moves.SmackDown,
            Moves.Dig => move.Id is Moves.Earthquake or Moves.Magnitude or Moves.Fissure,
            Moves.Dive => move.Id is Moves.Surf or Moves.Whirlpool,
            _ => false,
        };
    }

    /// <summary>Moves that fail outright in the situation (no PP used back). Returns the message, or null to go ahead.</summary>
    string? Fails(Battler user, Battler target, MoveInfo move)
    {
        return move.Id switch
        {
            Moves.DreamEater when target.Mon.Status != Ailment.Sleep || target.Substitute > 0 => $"{Label(target)} wasn't affected!",
            Moves.Snore when user.Mon.Status != Ailment.Sleep => "But it failed!",
            Moves.FakeOut when user.TurnsOut > 0 => "But it failed!",
            Moves.SuckerPunch when target.Moved || _data.Move(target.ChosenMove) is not { Category: not MoveCategory.Status } => "But it failed!",
            Moves.FocusPunch when user.TakenPhysical + user.TakenSpecial > 0 => $"{Label(user)} lost its focus and couldn't move!",
            Moves.Counter when user.TakenPhysical == 0 => "But it failed!",
            Moves.MirrorCoat when user.TakenSpecial == 0 => "But it failed!",
            Moves.MetalBurst when user.TakenPhysical + user.TakenSpecial == 0 => "But it failed!",
            Moves.Endeavor when target.Mon.Hp <= user.Mon.Hp => "But it failed!",
            _ => null,
        };
    }

    /// <summary>Taunt and Disable keep a move from being used.</summary>
    bool Restricted(Battler user, MoveInfo move, List<BattleEvent> events)
    {
        if (user.TauntTurns > 0 && move.Category == MoveCategory.Status)
        {
            events.Add(new MessageEvent($"{Label(user)} can't use {move.Name} after the taunt!"));
            return true;
        }
        if (user.DisableTurns > 0 && user.DisabledMove == move.Id)
        {
            events.Add(new MessageEvent($"{Label(user)}'s {move.Name} is disabled!"));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Status moves with effects the generic stat/ailment handling doesn't cover. True when the move was handled
    /// (its messages are in <paramref name="events"/>).
    /// </summary>
    bool StatusMove(Battler user, Battler target, MoveInfo move, List<BattleEvent> events)
    {
        var name = Label(user);
        var side = SideOf(user);
        void Fail() => events.Add(new MessageEvent("But it failed!"));
        switch (move.Id)
        {
            case Moves.Protect or Moves.Detect or Moves.Endure:
                // Each use in a row halves the odds.
                if (user.ProtectChain > 0 && _rng.Next(1 << Math.Min(user.ProtectChain, 8)) != 0)
                {
                    user.ProtectChain = 0;
                    Fail();
                    return true;
                }
                user.ProtectChain++;
                if (move.Id == Moves.Endure)
                {
                    user.Enduring = true;
                    events.Add(new MessageEvent($"{name} braced itself!"));
                }
                else
                {
                    user.Protected = true;
                    events.Add(new MessageEvent($"{name} protected itself!"));
                }
                return true;
            case Moves.Substitute:
                if (user.Substitute > 0)
                {
                    events.Add(new MessageEvent($"{name} already has a substitute!"));
                    return true;
                }
                var cost = user.Mon.MaxHp / 4;
                if (user.Mon.Hp <= cost)
                {
                    events.Add(new MessageEvent("It was too weak to make a substitute!"));
                    return true;
                }
                Damage(user, cost, events);
                user.Substitute = cost;
                events.Add(new MessageEvent($"{name} put in a substitute!"));
                return true;
            case Moves.Rest:
                if (user.Mon.Hp == user.Mon.MaxHp || Defends(user, null, Abilities.Insomnia) || Defends(user, null, Abilities.VitalSpirit))
                {
                    Fail();
                    return true;
                }
                user.Mon.Status = Ailment.None;
                user.Mon.SleepTurns = 3;   // asleep for two turns
                user.Toxic = 0;
                Heal(user, user.Mon.MaxHp, events, $"{name} slept and became healthy!");
                SetStatus(user, Ailment.Sleep, events, $"{name} fell asleep!");
                return true;
            case Moves.Haze:
                foreach (var b in AllField)
                    Array.Clear(b.Stages);
                events.Add(new MessageEvent("All stat changes were eliminated!"));
                return true;
            case Moves.HealBell or Moves.Aromatherapy or Moves.Refresh:
                events.Add(new MessageEvent(move.Id switch
                {
                    Moves.HealBell => "A bell chimed!",
                    Moves.Aromatherapy => "A soothing aroma wafted through the area!",
                    _ => $"{name}'s status returned to normal!",
                }));
                var team = move.Id == Moves.Refresh ? [user.Mon] : user.Side == BattleSide.Player ? _party : _foeParty;
                foreach (var mon in team.Where(m => !m.Fainted))
                {
                    mon.Status = Ailment.None;
                    mon.SleepTurns = 0;
                }
                user.Toxic = 0;
                events.Add(new StatusEvent(user.Side, Ailment.None, user.Slot));
                return true;
            case Moves.Reflect or Moves.LightScreen:
            {
                ref var turns = ref move.Id == Moves.Reflect ? ref side.Reflect : ref side.LightScreen;
                if (turns > 0)
                {
                    Fail();
                    return true;
                }
                turns = ItemOf(user) == 269 ? 8 : 5;   // Light Clay
                events.Add(new MessageEvent($"{(move.Id == Moves.Reflect ? "Reflect" : "Light Screen")} raised {Team(user)}'s {(move.Id == Moves.Reflect ? "Defense" : "Special Defense")}!"));
                return true;
            }
            case Moves.Mist or Moves.Safeguard or Moves.Tailwind:
            {
                ref var turns = ref move.Id == Moves.Mist ? ref side.Mist : ref move.Id == Moves.Safeguard ? ref side.Safeguard : ref side.Tailwind;
                if (turns > 0)
                {
                    Fail();
                    return true;
                }
                turns = move.Id == Moves.Tailwind ? 4 : 5;
                events.Add(new MessageEvent(move.Id switch
                {
                    Moves.Mist => $"{Capital(Team(user))} became shrouded in mist!",
                    Moves.Safeguard => $"{Capital(Team(user))} became cloaked in a mystical veil!",
                    _ => $"The tailwind blew from behind {Team(user)}!",
                }));
                return true;
            }
            case Moves.Spikes or Moves.ToxicSpikes or Moves.StealthRock:
            {
                var other = SideOf(target);
                var added = move.Id switch
                {
                    Moves.Spikes when other.Spikes < 3 => ++other.Spikes > 0,
                    Moves.ToxicSpikes when other.ToxicSpikes < 2 => ++other.ToxicSpikes > 0,
                    Moves.StealthRock when !other.StealthRock => other.StealthRock = true,
                    _ => false,
                };
                if (!added)
                {
                    Fail();
                    return true;
                }
                events.Add(new MessageEvent(move.Id switch
                {
                    Moves.Spikes => $"Spikes were scattered all around the feet of {Team(target)}!",
                    Moves.ToxicSpikes => $"Poison spikes were scattered all around the feet of {Team(target)}!",
                    _ => $"Pointed stones float in the air around {Team(target)}!",
                }));
                return true;
            }
            case Moves.FocusEnergy:
                if (user.FocusEnergy)
                {
                    Fail();
                    return true;
                }
                user.FocusEnergy = true;
                events.Add(new MessageEvent($"{name} is getting pumped!"));
                return true;
            case Moves.BellyDrum:
                if (user.Mon.Hp <= user.Mon.MaxHp / 2 || user.Stage(BattleStat.Attack) >= 6)
                {
                    Fail();
                    return true;
                }
                Damage(user, user.Mon.MaxHp / 2, events);
                user.Stages[(int)BattleStat.Attack] = 6;
                events.Add(new MessageEvent($"{name} cut its own HP and maximized its Attack!"));
                return true;
            case Moves.PainSplit:
                var average = (user.Mon.Hp + target.Mon.Hp) / 2;
                user.Mon.Hp = Math.Min(user.Mon.MaxHp, average);
                target.Mon.Hp = Math.Min(target.Mon.MaxHp, average);
                events.Add(new HpEvent(user.Side, user.Mon.Hp, user.Mon.MaxHp, user.Slot));
                events.Add(new HpEvent(target.Side, target.Mon.Hp, target.Mon.MaxHp, target.Slot));
                events.Add(new MessageEvent("The battlers shared their pain!"));
                return true;
            case Moves.Curse when user.Types.Contains(TypeChart.Ghost):
                if (target.Cursed)
                {
                    Fail();
                    return true;
                }
                Damage(user, Math.Max(1, user.Mon.MaxHp / 2), events);
                target.Cursed = true;
                events.Add(new MessageEvent($"{name} cut its own HP and laid a curse on {Label(target)}!"));
                CheckFaints(events);
                return true;
            case Moves.PsychUp:
                Array.Copy(target.Stages, user.Stages, user.Stages.Length);
                events.Add(new MessageEvent($"{name} copied {Label(target)}'s stat changes!"));
                return true;
            case Moves.LockOn or Moves.MindReader:
                user.LockOn = 2;
                events.Add(new MessageEvent($"{name} took aim at {Label(target)}!"));
                return true;
            case Moves.Wish:
                if (side.WishTurns > 0)
                {
                    Fail();
                    return true;
                }
                side.WishTurns = 2;
                side.WishHeal = user.Mon.MaxHp / 2;
                events.Add(new MessageEvent($"{name} made a wish!"));
                return true;
            case Moves.TrickRoom:
                _trickRoom = _trickRoom > 0 ? 0 : 5;
                events.Add(new MessageEvent(_trickRoom > 0 ? $"{name} twisted the dimensions!" : "The twisted dimensions returned to normal!"));
                return true;
            case Moves.Charge:
                user.Charged = true;
                events.Add(new MessageEvent($"{name} began charging power!"));
                return false;   // and its Sp. Def boost
            case Moves.Taunt:
                if (target.TauntTurns > 0 || Has(target, 12))   // Oblivious
                {
                    Fail();
                    return true;
                }
                target.TauntTurns = 3;
                events.Add(new MessageEvent($"{Label(target)} fell for the taunt!"));
                return true;
            case Moves.Recycle:
                if (user.Mon.HeldItem != 0 || user.LastBerry == 0)
                {
                    Fail();
                    return true;
                }
                user.Mon.HeldItem = user.LastBerry;
                user.LastBerry = 0;
                events.Add(new MessageEvent($"{name} found one {ItemName(user)}!"));
                return true;
            case Moves.Trick or Moves.Switcheroo:
                if ((user.Mon.HeldItem == 0 && target.Mon.HeldItem == 0) || Has(target, Abilities.StickyHold) || target.Substitute > 0)
                {
                    Fail();
                    return true;
                }
                (user.Mon.HeldItem, target.Mon.HeldItem) = (target.Mon.HeldItem, user.Mon.HeldItem);
                user.ChoiceMove = target.ChoiceMove = 0;
                events.Add(new MessageEvent($"{name} switched items with its target!"));
                if (user.Mon.HeldItem != 0)
                    events.Add(new MessageEvent($"{name} obtained one {ItemName(user)}."));
                if (target.Mon.HeldItem != 0)
                    events.Add(new MessageEvent($"{Label(target)} obtained one {ItemName(target)}."));
                return true;
            case Moves.SkillSwap or Moves.RolePlay or Moves.Entrainment or Moves.WorrySeed or Moves.SimpleBeam or Moves.GastroAcid:
                return ChangeAbility(user, target, move, events);
            case Moves.Soak:
                if (target.Types is [TypeChart.Water])
                {
                    Fail();
                    return true;
                }
                target.TypeOverride = [TypeChart.Water];
                events.Add(new MessageEvent($"{Label(target)} transformed into the Water type!"));
                return true;
            case Moves.ReflectType:
                user.TypeOverride = target.Types.ToList();
                events.Add(new MessageEvent($"{name}'s type changed to match {Label(target)}'s!"));
                return true;
            case Moves.MagnetRise:
                if (user.MagnetRise > 0)
                {
                    Fail();
                    return true;
                }
                user.MagnetRise = 5;
                events.Add(new MessageEvent($"{name} levitated with electromagnetism!"));
                return true;
            case Moves.AquaRing or Moves.Ingrain:
                ref var flag = ref move.Id == Moves.AquaRing ? ref user.AquaRing : ref user.Ingrained;
                if (flag)
                {
                    Fail();
                    return true;
                }
                flag = true;
                events.Add(new MessageEvent(move.Id == Moves.AquaRing ? $"{name} surrounded itself with a veil of water!" : $"{name} planted its roots!"));
                return true;
            case Moves.Roar or Moves.Whirlwind:
                return ForceOut(user, target, events);
            case Moves.Teleport:
                if (Trainer is not null || Trapped(user) is not null || user.Ingrained)
                {
                    Fail();
                    return true;
                }
                events.Add(new MessageEvent(user.Side == BattleSide.Player ? $"{name} fled from battle!" : $"{name} fled!"));
                Outcome = BattleOutcome.Escaped;
                return true;
            case Moves.BatonPass:
                if (!Pivot(user, batonPass: true, events))
                    Fail();
                return true;
            case Moves.PerishSong:
                foreach (var b in new[] { user, target }.Where(b => b.PerishCount == 0 && !Has(b, 43)))   // Soundproof
                    b.PerishCount = 4;
                events.Add(new MessageEvent("All Pokémon hearing the song will faint in three turns!"));
                return true;
            case Moves.Disable:
                if (target.DisableTurns > 0 || target.LastMove == 0 || _data.Move(target.LastMove) is not { } last)
                {
                    Fail();
                    return true;
                }
                target.DisabledMove = last.Id;
                target.DisableTurns = 4;
                events.Add(new MessageEvent($"{Label(target)}'s {last.Name} was disabled!"));
                return true;
            case Moves.Defog:
                var cleared = SideOf(target);
                cleared.Reflect = cleared.LightScreen = cleared.Safeguard = cleared.Mist = 0;
                cleared.Spikes = cleared.ToxicSpikes = 0;
                cleared.StealthRock = false;
                return false;   // and its evasion drop
        }
        return false;
    }

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Skill Swap, Role Play, Entrainment, Worry Seed, Simple Beam and Gastro Acid.</summary>
    bool ChangeAbility(Battler user, Battler target, MoveInfo move, List<BattleEvent> events)
    {
        if (Abilities.Untraceable(target.Ability) || target.Ability == Abilities.WonderGuard || target.Substitute > 0)
        {
            events.Add(new MessageEvent("But it failed!"));
            return true;
        }
        switch (move.Id)
        {
            case Moves.SkillSwap:
                ((user.Ability, user.AbilityName), (target.Ability, target.AbilityName)) =
                    ((target.Ability, target.AbilityName), (user.Ability, user.AbilityName));
                events.Add(new MessageEvent($"{Label(user)} swapped Abilities with its target!"));
                break;
            case Moves.RolePlay:
                (user.Ability, user.AbilityName) = (target.Ability, target.AbilityName);
                events.Add(new MessageEvent($"{Label(user)} copied {Label(target)}'s {target.AbilityName}!"));
                break;
            case Moves.Entrainment:
                (target.Ability, target.AbilityName) = (user.Ability, user.AbilityName);
                events.Add(new MessageEvent($"{Label(target)} acquired {target.AbilityName}!"));
                break;
            case Moves.WorrySeed:
                (target.Ability, target.AbilityName) = (Abilities.Insomnia, "Insomnia");
                events.Add(new MessageEvent($"{Label(target)} acquired Insomnia!"));
                if (target.Mon.Status == Ailment.Sleep)
                    SetStatus(target, Ailment.None, events, $"{Label(target)} woke up!");
                break;
            case Moves.SimpleBeam:
                (target.Ability, target.AbilityName) = (Abilities.Simple, "Simple");
                events.Add(new MessageEvent($"{Label(target)} acquired Simple!"));
                break;
            default:
                (target.Ability, target.AbilityName) = (Abilities.None, "");
                events.Add(new MessageEvent($"{Label(target)}'s Ability was suppressed!"));
                break;
        }
        return true;
    }

    /// <summary>
    /// Roar and Whirlwind: a wild Pokémon is scared off (the battle ends); otherwise the target is dragged out for a
    /// random healthy team mate. Ingrain and Suction Cups hold on.
    /// </summary>
    bool ForceOut(Battler user, Battler target, List<BattleEvent> events)
    {
        if (target.Ingrained || Has(target, Abilities.SuctionCups))
        {
            events.Add(new MessageEvent($"{Label(target)} anchored itself!"));
            return true;
        }
        if (Trainer is null && target.Side == BattleSide.Foe)
        {
            events.Add(new MessageEvent($"{Label(target)} fled!"));
            Outcome = BattleOutcome.Escaped;
            return true;
        }
        var team = target.Side == BattleSide.Player ? _party : _foeParty;
        var options = Enumerable.Range(0, team.Count).Where(i => !team[i].Fainted && !AllField.Any(b => ReferenceEquals(b.Mon, team[i]))).ToList();
        if (options.Count == 0)
        {
            events.Add(new MessageEvent("But it failed!"));
            return true;
        }
        var next = options[_rng.Next(options.Count)];
        if (target.Side == BattleSide.Player)
        {
            Enter(next, events, target.Slot);
            events.Add(new MessageEvent($"{_party[next].Name} was dragged out!"));
        }
        else
            SendOutFoe(next, events, $"{_foeParty[next].Name} was dragged out!", target.Slot);
        return true;
    }

    /// <summary>A trainer's Pokémon <paramref name="index"/> takes the field (the player's Pokémon fights it from now on).</summary>
    void SendOutFoe(int index, List<BattleEvent> events, string message, int slot = 0)
    {
        var passed = _batonPass;
        _batonPass = null;
        var foe = _field[1][slot] = new Battler(_foeParty[index], BattleSide.Foe, slot);
        if (passed is not null)
            Inherit(foe, passed);
        foreach (var b in _field[0])
            if (!b.Mon.Fainted)
                foe.FacedBy.Add(b.Mon);
        events.Add(new SwitchEvent(index, BattleSide.Foe, slot));
        events.Add(new MessageEvent(message));
        EntryHazards(foe, events);
        OnEntry(foe, events);
    }

    /// <summary>
    /// U-turn, Volt Switch and Baton Pass: the user leaves for a team mate. The player picks one (<see cref="NeedsSwitch"/>),
    /// a trainer sends a random one. False when nobody can come in.
    /// </summary>
    bool Pivot(Battler user, bool batonPass, List<BattleEvent> events)
    {
        var team = user.Side == BattleSide.Player ? _party : _foeParty;
        var options = Enumerable.Range(0, team.Count).Where(i => !team[i].Fainted && !AllField.Any(b => ReferenceEquals(b.Mon, team[i]))).ToList();
        if (options.Count == 0 || user.Mon.Fainted || (user.Side == BattleSide.Foe && Trainer is null))
            return false;
        if (batonPass)
            _batonPass = user;
        if (user.Side == BattleSide.Player && Width > 1)
        {
            // Doubles and triples: the next Pokémon on the bench comes in at once.
            OnSwitchOut(user);
            events.Add(new MessageEvent($"{user.Mon.Name} went back to you!"));
            Enter(options[0], events, user.Slot);
        }
        else if (user.Side == BattleSide.Player)
        {
            OnSwitchOut(user);
            events.Add(new MessageEvent($"{user.Mon.Name} went back to you!"));
            NeedsSwitch = true;
            _pivoting = true;
        }
        else
        {
            OnSwitchOut(user);
            var next = options[_rng.Next(options.Count)];
            events.Add(new MessageEvent($"{Label(user)} went back to {Trainer!.Title}!"));
            SendOutFoe(next, events, $"{Trainer.Title} sent out {_foeParty[next].Name}!", user.Slot);
        }
        return true;
    }

    bool _pivoting;

    /// <summary>Baton Pass: stat stages, Substitute, confusion, Focus Energy, Leech Seed, curse, Ingrain, Aqua Ring and Magnet Rise carry over.</summary>
    static void Inherit(Battler to, Battler from)
    {
        Array.Copy(from.Stages, to.Stages, to.Stages.Length);
        to.Substitute = from.Substitute;
        to.ConfusionTurns = from.ConfusionTurns;
        to.FocusEnergy = from.FocusEnergy;
        to.Seeded = from.Seeded;
        to.Cursed = from.Cursed;
        to.Ingrained = from.Ingrained;
        to.AquaRing = from.AquaRing;
        to.MagnetRise = from.MagnetRise;
        to.PerishCount = from.PerishCount;
    }

    /// <summary>Spikes, Toxic Spikes and Stealth Rock hurt (or poison) a Pokémon coming in.</summary>
    void EntryHazards(Battler b, List<BattleEvent> events)
    {
        var side = SideOf(b);
        var grounded = !b.Types.Contains(TypeChart.Flying) && !Has(b, Abilities.Levitate) && ItemOf(b) != HeldItems.AirBalloon && b.MagnetRise == 0;
        if (!Has(b, Abilities.MagicGuard))
        {
            if (side.StealthRock)
            {
                var times = TypeChart.Against(TypeChart.Rock, b.Types);
                Damage(b, Math.Max(1, (int)(b.Mon.MaxHp * times / 8)), events, $"Pointed stones dug into {Label(b)}!");
            }
            if (side.Spikes > 0 && grounded && !b.Mon.Fainted)
                Damage(b, Math.Max(1, b.Mon.MaxHp / (side.Spikes switch { 1 => 8, 2 => 6, _ => 4 })), events, $"{Label(b)} is hurt by spikes!");
        }
        if (side.ToxicSpikes > 0 && grounded && !b.Mon.Fainted)
        {
            if (b.Types.Contains(TypeChart.Poison))
            {
                side.ToxicSpikes = 0;
                events.Add(new MessageEvent($"The poison spikes disappeared from around {Team(b)}'s feet!"));
            }
            else if (Inflict(b, Ailment.Poison, null, events, loud: false) && side.ToxicSpikes > 1)
                b.Toxic = 1;
        }
        CheckFaints(events);
    }

    /// <summary>
    /// Volatile conditions a move gives (move ailments above poison): infatuation, trapping, Leech Seed, Yawn,
    /// Foresight/Miracle Eye, Ingrain-style holds. True when the ailment was one of these.
    /// </summary>
    bool VolatileAilment(Battler user, Battler target, MoveInfo move, List<BattleEvent> events)
    {
        var status = move.Category == MoveCategory.Status;
        var name = Label(target);
        void Fail()
        {
            if (status)
                events.Add(new MessageEvent("But it failed!"));
        }
        switch (move.Ailment)
        {
            case Ailment.Infatuation:
                if (target.Infatuated || user.Mon.Gender == Gender.Genderless || target.Mon.Gender == Gender.Genderless
                    || user.Mon.Gender == target.Mon.Gender || Defends(target, user, 12))   // Oblivious
                    Fail();
                else
                {
                    target.Infatuated = true;
                    events.Add(new MessageEvent($"{name} fell in love!"));
                }
                return true;
            case Ailment.Trap:
                if (status)
                {
                    if (target.CantEscape)
                        Fail();
                    else
                    {
                        target.CantEscape = true;
                        events.Add(new MessageEvent($"{name} can no longer escape!"));
                    }
                }
                else if (target.TrapTurns == 0 && !target.Mon.Fainted)
                {
                    target.TrapTurns = 4 + _rng.Next(2);
                    target.TrapName = move.Name;
                    events.Add(new MessageEvent($"{name} was trapped by {move.Name}!"));
                }
                return true;
            case Ailment.LeechSeed:
                if (target.Seeded || target.Types.Contains(TypeChart.Grass))
                    events.Add(new MessageEvent($"{name} evaded the attack!"));
                else
                {
                    target.Seeded = true;
                    events.Add(new MessageEvent($"{name} was seeded!"));
                }
                return true;
            case Ailment.Yawn:
                if (target.Yawn > 0 || target.Mon.Status != Ailment.None || SideOf(target).Safeguard > 0)
                    Fail();
                else
                {
                    target.Yawn = 2;
                    events.Add(new MessageEvent($"{name} grew drowsy!"));
                }
                return true;
            case Ailment.NoTypeImmunity:
                target.IdentifiedBy = move.Id;
                events.Add(new MessageEvent($"{Label(user)} identified {name}!"));
                return true;
            case Ailment.Ingrain:
            case Ailment.PerishSong:
            case Ailment.Disable:
                return true;   // handled by the move itself
            case > Ailment.Confusion:
                return true;   // Nightmare, Torment, Heal Block, Embargo: not yet
        }
        return false;
    }

    /// <summary>After a damaging move lands: item theft, Rapid Spin, switching out, self-destruction, recharge, Pay Day.</summary>
    void AfterDamagingMove(Battler user, Battler target, MoveInfo move, int dealt, List<BattleEvent> events)
    {
        var name = Label(user);
        switch (move.Id)
        {
            case Moves.Thief or Moves.Covet when dealt > 0 && user.Mon.HeldItem == 0 && target.Mon.HeldItem != 0
                                                 && !Has(target, Abilities.StickyHold) && !user.Mon.Fainted:
                user.Mon.HeldItem = target.Mon.HeldItem;
                target.Mon.HeldItem = 0;
                target.LostItem = true;
                events.Add(new MessageEvent($"{name} stole {Label(target)}'s {ItemName(user)}!"));
                break;
            case Moves.KnockOff when dealt > 0 && target.Mon.HeldItem != 0 && !Has(target, Abilities.StickyHold):
                events.Add(new MessageEvent($"{name} knocked off {Label(target)}'s {ItemName(target)}!"));
                target.Mon.HeldItem = 0;
                target.LostItem = true;
                break;
            case Moves.RapidSpin when !user.Mon.Fainted:
                user.TrapTurns = 0;
                user.Seeded = false;
                var side = SideOf(user);
                side.Spikes = side.ToxicSpikes = 0;
                side.StealthRock = false;
                break;
            case Moves.PayDay:
                _payDay += user.Mon.Level * 5;
                events.Add(new MessageEvent("Coins were scattered everywhere!"));
                break;
            case Moves.FalseSwipe:
                break;
        }
        if (Moves.Recharges(move.Id) && !user.Mon.Fainted)
            user.Recharging = true;
        if (move.Id is Moves.UTurn or Moves.VoltSwitch && dealt > 0 && !Over)
            Pivot(user, batonPass: false, events);
    }

    int _payDay;

    /// <summary>Per-Pokémon end-of-turn effects of moves: Ingrain, Aqua Ring, Leech Seed, curse, trapping, Yawn, Perish Song, timers.</summary>
    void MoveEndOfTurn(Battler b, List<BattleEvent> events)
    {
        var name = Label(b);
        if ((b.Ingrained || b.AquaRing) && b.Mon.Hp < b.Mon.MaxHp)
            Heal(b, Math.Max(1, b.Mon.MaxHp / 16), events, b.Ingrained ? $"{name} absorbed nutrients with its roots!" : $"A veil of water restored {name}'s HP!");
        var other = Opponent(b);
        if (b.Seeded && !other.Mon.Fainted && !Has(b, Abilities.MagicGuard))
        {
            var sapped = Damage(b, Math.Max(1, b.Mon.MaxHp / 8), events, $"{name}'s health is sapped by Leech Seed!");
            if (Has(b, Abilities.LiquidOoze))
                Damage(other, sapped, events, $"{Label(other)} sucked up the liquid ooze!");
            else if (other.Mon.Hp < other.Mon.MaxHp)
                Heal(other, sapped, events, $"{Label(other)} regained health!");
            CheckFaints(events);
        }
        if (b.Mon.Fainted || Over)
            return;
        if (b.Cursed && !Has(b, Abilities.MagicGuard))
        {
            Damage(b, Math.Max(1, b.Mon.MaxHp / 4), events, $"{name} is afflicted by the curse!");
            CheckFaints(events);
        }
        if (b.TrapTurns > 0 && !b.Mon.Fainted)
        {
            if (--b.TrapTurns == 0)
                events.Add(new MessageEvent($"{name} was freed from {b.TrapName}!"));
            else if (!Has(b, Abilities.MagicGuard))
            {
                Damage(b, Math.Max(1, b.Mon.MaxHp / 16), events, $"{name} is hurt by {b.TrapName}!");
                CheckFaints(events);
            }
        }
        if (b.Mon.Fainted || Over)
            return;
        if (b.TauntTurns > 0 && --b.TauntTurns == 0)
            events.Add(new MessageEvent($"{name}'s taunt wore off!"));
        if (b.DisableTurns > 0 && --b.DisableTurns == 0)
            events.Add(new MessageEvent($"{name} is no longer disabled!"));
        if (b.MagnetRise > 0 && --b.MagnetRise == 0)
            events.Add(new MessageEvent($"{name}'s electromagnetism wore off!"));
        if (b.LockOn > 0)
            b.LockOn--;
        if (b.Yawn > 0 && --b.Yawn == 0)
            Inflict(b, Ailment.Sleep, null, events, loud: false);
        if (b.PerishCount > 0)
        {
            b.PerishCount--;
            events.Add(new MessageEvent($"{name}'s perish count fell to {b.PerishCount}."));
            if (b.PerishCount == 0)
            {
                Damage(b, b.Mon.Hp, events);
                CheckFaints(events);
            }
        }
    }

    /// <summary>Side and field timers: screens, Mist, Safeguard, Tailwind, Wish, Trick Room.</summary>
    void SideEndOfTurn(List<BattleEvent> events)
    {
        // Once per side (the side's first able Pokémon gets the Wish).
        foreach (var b in _field.Select(side => side.FirstOrDefault(x => !x.Mon.Fainted) ?? side[0]))
        {
            var side = SideOf(b);
            var team = Capital(Team(b));
            if (side.WishTurns > 0 && --side.WishTurns == 0 && !b.Mon.Fainted && b.Mon.Hp < b.Mon.MaxHp)
                Heal(b, Math.Max(1, side.WishHeal), events, "The wish came true!");
            if (side.Reflect > 0 && --side.Reflect == 0)
                events.Add(new MessageEvent($"{team}'s Reflect wore off!"));
            if (side.LightScreen > 0 && --side.LightScreen == 0)
                events.Add(new MessageEvent($"{team}'s Light Screen wore off!"));
            if (side.Mist > 0 && --side.Mist == 0)
                events.Add(new MessageEvent($"{team} is no longer protected by mist!"));
            if (side.Safeguard > 0 && --side.Safeguard == 0)
                events.Add(new MessageEvent($"{team} is no longer protected by Safeguard!"));
            if (side.Tailwind > 0 && --side.Tailwind == 0)
                events.Add(new MessageEvent($"{team}'s tailwind petered out!"));
        }
        if (_trickRoom > 0 && --_trickRoom == 0)
            events.Add(new MessageEvent("The twisted dimensions returned to normal!"));
    }
}
