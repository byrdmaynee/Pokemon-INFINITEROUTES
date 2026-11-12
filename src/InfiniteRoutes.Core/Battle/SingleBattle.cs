using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Core.Battle;

public enum BattleSide { Player, Foe }

public enum BattleOutcome { Ongoing, Won, Lost, Escaped, Caught }

/// <summary>Something that happened in a turn, in order, for the UI to play back.</summary>
public abstract record BattleEvent;
public sealed record MessageEvent(string Text) : BattleEvent;
/// <summary>A side's HP is now <see cref="Hp"/> (after damage or healing).</summary>
public sealed record HpEvent(BattleSide Side, int Hp, int MaxHp, int Slot = 0) : BattleEvent;
public sealed record StatusEvent(BattleSide Side, Ailment Status, int Slot = 0) : BattleEvent;
public sealed record FaintEvent(BattleSide Side, int Slot = 0) : BattleEvent;
/// <summary>Party member <see cref="PartyIndex"/> gained experience; <see cref="Level"/> is its level afterwards.</summary>
public sealed record ExpEvent(int PartyIndex, int Gained, int Level) : BattleEvent;
public sealed record LevelUpEvent(int PartyIndex, int Level, int MaxHp, int Hp) : BattleEvent;
/// <summary>A party Pokémon reached a move it could learn but already knows four: the player picks one to forget
/// (<see cref="Pokemon.LearnMove"/>) or skips it.</summary>
public sealed record LearnMoveEvent(int PartyIndex, MoveInfo Move) : BattleEvent;
/// <summary>Party member <see cref="PartyIndex"/> of <see cref="Side"/> came out (the player's or a trainer's).</summary>
public sealed record SwitchEvent(int PartyIndex, BattleSide Side = BattleSide.Player, int Slot = 0) : BattleEvent;
/// <summary>A Poké Ball was thrown: it shook <see cref="Shakes"/> times (0–3) and caught the Pokémon or not.</summary>
public sealed record CatchEvent(int Ball, int Shakes, bool Caught) : BattleEvent;
/// <summary>The trainer steps forward (<see cref="Visible"/>) or aside for their Pokémon; the battle screen shows their sprite.</summary>
public sealed record TrainerEvent(bool Visible) : BattleEvent;
/// <summary><see cref="Side"/>'s ability <see cref="Ability"/> takes effect; the messages that follow say what it did.</summary>
public sealed record AbilityEvent(BattleSide Side, string Ability, int Slot = 0) : BattleEvent;
/// <summary>
/// The move's animation plays now: the Pokémon in <paramref name="User"/>'s <paramref name="Slot"/> uses move id
/// <paramref name="Move"/>, aimed at <paramref name="Target"/>'s <paramref name="TargetSlot"/>.
/// </summary>
public sealed record MoveAnimationEvent(BattleSide User, int Move, int Slot = 0, BattleSide Target = BattleSide.Foe, int TargetSlot = 0) : BattleEvent;

/// <summary>How many Pokémon each side has out: one, two or three; Rotation is one at a time with free rotations.</summary>
public enum BattleFormat { Single, Double, Triple, Rotation }

/// <summary>What the player has one of their Pokémon do in a double or triple battle's turn.</summary>
public abstract record BattleChoice
{
    /// <summary>Move slot <paramref name="Slot"/> at the Pokémon in <paramref name="TargetSide"/>'s <paramref name="TargetSlot"/>.</summary>
    public sealed record Fight(int Slot, BattleSide TargetSide = BattleSide.Foe, int TargetSlot = 0) : BattleChoice;
    /// <summary>Calls the Pokémon back for party member <paramref name="PartyIndex"/>.</summary>
    public sealed record Switch(int PartyIndex) : BattleChoice;
    /// <summary>An item from the Bag: a Poké Ball at the last wild Pokémon, or medicine on party member <paramref name="PartyIndex"/>.</summary>
    public sealed record Item(int ItemId, int PartyIndex = -1) : BattleChoice;
    public sealed record Run : BattleChoice;
}

/// <summary>
/// A Pokémon on the field: the Pokémon plus what resets when it leaves (stat stages, confusion, flinch, a traced
/// ability, Flash Fire's boost, Truant's loafing, turns on the field).
/// </summary>
public sealed class Battler(Pokemon mon, BattleSide side, int slot = 0)
{
    public Pokemon Mon { get; } = mon;
    public BattleSide Side { get; } = side;
    /// <summary>Its place on its side: 0 in a single battle; 0–1 or 0–2, left to right as its trainer sees them.</summary>
    public int Slot { get; } = slot;
    /// <summary>The player's Pokémon that were out while this foe was: they share the EXP when it faints.</summary>
    public HashSet<Pokemon> FacedBy { get; } = [];
    /// <summary>Its faint has been announced (and a replacement asked for).</summary>
    public bool Gone { get; set; }
    /// <summary>Stat stages −6…+6, indexed by <see cref="BattleStat"/> (Attack … Evasion).</summary>
    public int[] Stages { get; } = new int[8];
    public int ConfusionTurns { get; set; }
    public bool Flinched { get; set; }
    /// <summary>The ability in effect: the Pokémon's own, or one it traced.</summary>
    public int Ability { get; set; } = mon.AbilityId;
    public string AbilityName { get; set; } = mon.AbilityName;
    /// <summary>Its types, unless Soak or Reflect Type changed them.</summary>
    public IReadOnlyList<int> Types => TypeOverride ?? Mon.Types;
    public IReadOnlyList<int>? TypeOverride { get; set; }
    /// <summary>Flash Fire took a Fire move: its own Fire moves are 1.5× stronger.</summary>
    public bool FlashFire { get; set; }
    /// <summary>Truant: it moved last turn, so it loafs around this one.</summary>
    public bool Loafing { get; set; }
    /// <summary>Ends of turn survived since coming out (Speed Boost, Slow Start).</summary>
    public int TurnsOut { get; set; }
    /// <summary>The move a Choice item locked it into (0: none yet).</summary>
    public int ChoiceMove { get; set; }
    /// <summary>It used up or lost its item this battle (Unburden).</summary>
    public bool LostItem { get; set; }
    /// <summary>The last berry it ate (Harvest).</summary>
    public int LastBerry { get; set; }

    // Move effects (see SingleBattle.Moves.cs).
    /// <summary>A two-turn move it's charging (0: none), and the semi-invulnerable state it's in meanwhile.</summary>
    public int Charging { get; set; }
    public int SemiInvulnerable { get; set; }
    public bool Recharging { get; set; }
    /// <summary>Thrash, Petal Dance or Outrage: the move and turns left.</summary>
    public int LockedMove { get; set; }
    public int LockedTurns { get; set; }
    /// <summary>Consecutive Rollout/Ice Ball and Fury Cutter hits.</summary>
    public int Rollout { get; set; }
    public int FuryCutter { get; set; }
    public bool Protected { get; set; }
    public int ProtectChain { get; set; }
    public bool Enduring { get; set; }
    /// <summary>HP left in its Substitute (0: none).</summary>
    public int Substitute { get; set; }
    /// <summary>Wrap-style trapping: turns left and the move's name; Mean Look-style trapping lasts while it's out.</summary>
    public int TrapTurns { get; set; }
    public string TrapName { get; set; } = "";
    public bool CantEscape { get; set; }
    public bool Seeded { get; set; }
    /// <summary>Badly poisoned: the next poison damage is Toxic/16 of max HP (0: plain poison).</summary>
    public int Toxic { get; set; }
    public int TauntTurns { get; set; }
    public int DisabledMove { get; set; }
    public int DisableTurns { get; set; }
    public int Yawn { get; set; }
    public bool Infatuated { get; set; }
    /// <summary>Foresight/Odor Sleuth or Miracle Eye identified it (the move's id).</summary>
    public int IdentifiedBy { get; set; }
    public int LockOn { get; set; }
    public bool Cursed { get; set; }
    public bool FocusEnergy { get; set; }
    public bool Charged { get; set; }
    public bool Ingrained, AquaRing;
    public int MagnetRise { get; set; }
    /// <summary>Perish Song: turns until it faints, counting 3, 2, 1, 0 (0 at the start: not singing).</summary>
    public int PerishCount { get; set; }
    public int LastMove { get; set; }
    /// <summary>The move it picked this turn, whether it moved yet, and the damage it took this turn by category.</summary>
    public int ChosenMove { get; set; }
    public bool Moved { get; set; }
    public int TakenPhysical { get; set; }
    public int TakenSpecial { get; set; }

    public int Stage(BattleStat stat) => Stages[(int)stat];
}

/// <summary>
/// A battle against wild Pokémon or a trainer's team — single, double, triple or rotation (see the Doubles part) — with Generation 5 rules: priority and speed order,
/// accuracy and evasion, critical hits, STAB, type effectiveness, random damage roll, burn, poison, paralysis, sleep,
/// freeze, confusion, flinching, stat stages, multi-hit, recoil, drain, healing, one-hit KOs, escape odds, Poké
/// Balls and medicine, switching, experience shared between the Pokémon that fought (×1.5 from trainers) and prize
/// money, and abilities (see <see cref="Abilities.Implemented"/>). Trainers pick moves at random for now. Moves whose
/// special effects aren't implemented yet still deal their damage.
/// </summary>
public sealed partial class SingleBattle
{
    readonly BattleData _data;
    Random _rng;
    readonly IReadOnlyList<Pokemon> _party;
    readonly Bag _bag;
    readonly IReadOnlyList<Pokemon> _foeParty;
    /// <summary>Each side's Pokémon out, by slot: [0] the player's, [1] the foe's.</summary>
    readonly Battler[][] _field;
    /// <summary>The foe's slots whose Pokémon fainted: another comes out at the end of the turn.</summary>
    readonly List<int> _foeReplacing = [];
    /// <summary>The player's slots waiting for <see cref="SendOut"/>.</summary>
    readonly List<int> _playerReplacing = [];
    int _escapeAttempts;
    int _turns;
    bool _spread;

    public BattleFormat Format { get; }
    /// <summary>Pokémon out per side.</summary>
    public int Width => _field[0].Length;

    /// <summary>The player's Pokémon in slot 0 (the only one in a single battle).</summary>
    public Battler Player { get => _field[0][0]; private set => _field[0][0] = value; }
    /// <summary>The foe's Pokémon in slot 0 (the only one in a single battle).</summary>
    public Battler Foe { get => _field[1][0]; private set => _field[1][0] = value; }
    public IReadOnlyList<Battler> PlayerField => _field[0];
    public IReadOnlyList<Battler> FoeField => _field[1];
    IEnumerable<Battler> AllField => _field[0].Concat(_field[1]);

    /// <summary>The player's slots that need a Pokémon sent out (<see cref="SendOut"/>) before the next turn.</summary>
    public IReadOnlyList<int> SlotsToFill => _playerReplacing;

    /// <summary>The wild Pokémon caught, once the battle ends that way.</summary>
    public Pokemon? Caught { get; private set; }

    /// <summary>The trainer being fought, or null in a wild battle.</summary>
    public TrainerInfo? Trainer { get; }
    public IReadOnlyList<Pokemon> FoeParty => _foeParty;

    /// <summary>Money won from the trainer (set when the battle is won).</summary>
    public int PrizeMoney { get; private set; }
    public BattleOutcome Outcome { get; private set; }
    public bool Over => Outcome != BattleOutcome.Ongoing;

    /// <summary>
    /// Replaces the random numbers from here on. Online, the server picks a seed for each action once the player has
    /// chosen it and the client replays the action with the same seed, so both play the same turn and neither side's
    /// choice can be fitted to the dice in advance.
    /// </summary>
    public void Reseed(int seed) => _rng = new Random(seed);

    /// <summary>
    /// A short fingerprint of where the battle stands (every Pokémon's HP and status, PP left, the outcome), so a
    /// server and a client replaying the same battle can tell whether they still agree.
    /// </summary>
    public string Digest()
    {
        var parts = _party.Concat(_foeParty).Select(m => $"{m.Species.Id}:{m.Level}:{m.Hp}:{(int)m.Status}:{m.Exp}:{string.Join('.', m.Moves.Select(x => x.Pp))}");
        return $"{(int)Outcome}|{string.Join(',', parts)}|{_bag.Items.Sum(i => i.Item * 31 + i.Count)}";
    }

    /// <summary>The player's Pokémon fainted and another can fight: call <see cref="SendOut"/> before the next action.</summary>
    public bool NeedsSwitch { get; private set; }

    public IReadOnlyList<Pokemon> Party => _party;
    public Bag Bag => _bag;
    public int ActiveIndex => IndexOf(Player.Mon);

    /// <summary>Whether party member <paramref name="index"/> is out on the field now.</summary>
    public bool IsOut(int index) => index >= 0 && index < _party.Count && _field[0].Any(b => ReferenceEquals(b.Mon, _party[index]));

    int SlotOf(Pokemon mon) => Math.Max(0, Array.FindIndex(_field[0], b => ReferenceEquals(b.Mon, mon)));

    /// <summary>
    /// A battle against <paramref name="foeParty"/> — one wild Pokémon, or <paramref name="trainer"/>'s team — with the
    /// player's first able Pokémon leading.
    /// </summary>
    /// <remarks>
    /// <paramref name="format"/> defaults to the trainer's battle type (single for wild Pokémon unless two are given). A
    /// double or triple battle needs that many able Pokémon on both sides, else it's fought with as many as both have.
    /// </remarks>
    public SingleBattle(BattleData data, IReadOnlyList<Pokemon> party, Bag bag, IReadOnlyList<Pokemon> foeParty, TrainerInfo? trainer, Random rng,
        BattleFormat? format = null)
    {
        _data = data;
        _rng = rng;
        _party = party;
        _bag = bag;
        _foeParty = foeParty;
        Trainer = trainer;
        var mine = party.Where(p => !p.Fainted).ToList();
        var theirs = foeParty.Where(p => !p.Fainted).ToList();
        if (mine.Count == 0)
            throw new ArgumentException("No Pokémon can fight.", nameof(party));
        if (theirs.Count == 0)
            throw new ArgumentException("The foe has no Pokémon.", nameof(foeParty));
        Format = format ?? (trainer?.BattleType switch { 1 => BattleFormat.Double, 2 => BattleFormat.Triple, 3 => BattleFormat.Rotation, _ => BattleFormat.Single });
        var width = Math.Min(Format switch { BattleFormat.Double => 2, BattleFormat.Triple => 3, _ => 1 }, Math.Min(mine.Count, theirs.Count));
        if (width == 1 && Format is BattleFormat.Double or BattleFormat.Triple)
            Format = BattleFormat.Single;
        else if (width == 2 && Format == BattleFormat.Triple)
            Format = BattleFormat.Double;
        _field = [new Battler[width], new Battler[width]];
        for (var i = 0; i < width; i++)
        {
            _field[0][i] = new Battler(mine[i], BattleSide.Player, i);
            _field[1][i] = new Battler(theirs[i], BattleSide.Foe, i);
        }
        foreach (var foe in _field[1])
            foreach (var b in _field[0])
                foe.FacedBy.Add(b.Mon);
        if (Format == BattleFormat.Rotation)
            _rotation = mine.Take(3).ToList();
    }

    /// <summary>A wild double battle (dark grass): two wild Pokémon at once.</summary>
    public SingleBattle(BattleData data, IReadOnlyList<Pokemon> party, Bag bag, Pokemon wild, Pokemon partner, Random rng)
        : this(data, party, bag, [wild, partner], null, rng, BattleFormat.Double) { }

    /// <summary>A wild battle with the party's first able Pokémon leading.</summary>
    public SingleBattle(BattleData data, IReadOnlyList<Pokemon> party, Bag bag, Pokemon wild, Random rng)
        : this(data, party, bag, [wild], null, rng) { }

    /// <summary>
    /// A trainer's team as Pokémon: each at its level with every IV at difficulty × 31 / 255, knowing its listed moves
    /// or else the last four it learned by that level. The high nibble of the gender/ability byte picks the ability
    /// (1 first, 2 second, 3 hidden); otherwise it's random like a wild one's.
    /// </summary>
    public static List<Pokemon> TrainerParty(BattleData data, TrainerInfo trainer, Random rng)
    {
        var team = new List<Pokemon>();
        foreach (var p in trainer.Pokemon)
        {
            if (p.Species <= 0)
                continue;
            var mon = Pokemon.Create(data, p.Species, p.Level, rng);
            Array.Fill(mon.Ivs, p.Difficulty * 31 / 255);
            if ((p.GenderAbility >> 4) is var ability and >= 1 and <= 3)
                mon.SetAbilitySlot(ability - 1);
            mon.HeldItem = p.HeldItem;
            if (p.Moves is { Count: > 0 } moves)
            {
                mon.Moves.Clear();
                foreach (var id in moves)
                    if (data.Move(id) is { } move)
                        mon.Moves.Add(new MoveSlot(move));
            }
            mon.Hp = mon.MaxHp;
            team.Add(mon);
        }
        return team;
    }

    /// <summary>A one-on-one battle with no items (for tests and simple uses).</summary>
    public SingleBattle(BattleData data, Pokemon player, Pokemon wild, Random rng) : this(data, [player], new Bag(), wild, rng) { }

    public IReadOnlyList<BattleEvent> Start()
    {
        var foes = Names(_field[1]);
        var mine = Names(_field[0]);
        List<BattleEvent> events = Trainer is { } trainer
            ?
            [
                new MessageEvent($"You are challenged by {trainer.Title}!"),
                new TrainerEvent(false),
                new MessageEvent($"{trainer.Title} sent out {foes}!"),
                new MessageEvent($"Go! {mine}!"),
            ]
            : [new MessageEvent(Width > 1 ? $"Wild {foes} appeared!" : $"A wild {foes} appeared!"), new MessageEvent($"Go! {mine}!")];
        // Entry abilities (Intimidate, Pressure, …) go off fastest first.
        foreach (var b in BySpeed(AllField))
            OnEntry(b, events);
        return events;
    }

    /// <summary>True when no move has PP left: <see cref="Fight"/> then uses Struggle whatever the slot.</summary>
    public bool MustStruggle => Player.Mon.Moves.All(m => m.Pp <= 0);

    static string Names(IEnumerable<Battler> battlers)
    {
        var names = battlers.Select(b => b.Mon.Name).ToList();
        return names.Count <= 1 ? string.Concat(names) : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
    }

    /// <summary>Fastest first (slowest first under Trick Room), speed ties broken at random.</summary>
    List<Battler> BySpeed(IEnumerable<Battler> battlers) =>
        battlers.Select(b => (b, Speed: EffectiveSpeed(b) * (_trickRoom > 0 ? -1 : 1), Tie: _rng.Next()))
            .OrderByDescending(x => x.Speed).ThenBy(x => x.Tie).Select(x => x.b).ToList();

    int IndexOf(Pokemon mon)
    {
        for (var i = 0; i < _party.Count; i++)
            if (ReferenceEquals(_party[i], mon))
                return i;
        return -1;
    }

    bool Waiting => Over || NeedsSwitch;

    /// <summary>Plays one turn in which the player uses move <paramref name="slot"/> (ignored when struggling).</summary>
    public IReadOnlyList<BattleEvent> Fight(int slot)
    {
        if (Waiting)
            return [];
        var events = new List<BattleEvent>();
        if (!PlayerLocked && ChoiceLock(Player) is { } locked && !MustStruggle && slot >= 0 && slot < Player.Mon.Moves.Count
            && Player.Mon.Moves[slot].Move.Id != locked)
            return [new MessageEvent($"{Player.Mon.Name} can only use {_data.Move(locked)?.Name}!")];
        var playerMove = Forced(Player) ?? Choose(Player, slot);
        if (playerMove is null)
            return [new MessageEvent("There's no PP left for this move!")];
        if (Forced(Player) is null && Restricted(Player, playerMove.Value.Move, events))
            return events;
        var foeForced = Forced(Foe);
        // A trainer may spend its turn on medicine (before anyone moves, as items go first).
        var foeMove = foeForced is null && TrainerUsesItem(events) ? null : foeForced ?? Choose(Foe, FoeSlot());
        _turns++;
        Player.ChosenMove = playerMove.Value.Move.Id;
        Foe.ChosenMove = foeMove?.Move.Id ?? 0;

        var playerFirst = GoesFirst(playerMove.Value.Move, foeMove?.Move);
        var order = playerFirst
            ? new[] { (Player, Foe, playerMove), (Foe, Player, foeMove) }
            : new[] { (Foe, Player, foeMove), (Player, Foe, playerMove) };
        for (var i = 0; i < order.Length && !Waiting; i++)
        {
            var (user, target, choice) = order[i];
            if (choice is null || user.Mon.Fainted || target.Mon.Fainted)
                continue;
            UseMove(user, target, choice.Value.Move, choice.Value.Slot, movedFirst: i == 0, events);
            user.Moved = true;
            CheckFaints(events);
            // The player's U-turn/Volt Switch/Baton Pass: the foe still moves, against whoever comes in.
            if (_pivoting && i == 0 && order[1].Item3 is { } pending && !Foe.Mon.Fainted)
            {
                _pendingFoeMove = pending;
                return events;
            }
        }
        if (_pivoting)
            return events;
        EndOfTurn(events);
        return events;
    }

    /// <summary>Tries to run. On failure the wild Pokémon gets a free turn.</summary>
    public IReadOnlyList<BattleEvent> Run()
    {
        if (Waiting)
            return [];
        if (Trainer is not null)
            return [new MessageEvent("No! There's no running from a Trainer battle!")];
        var runAway = Has(Player, Abilities.RunAway) || ItemOf(Player) == HeldItems.SmokeBall;
        if (!runAway && Trapped(Player) is { } trap)
            return [Popup(Opponent(Player)), new MessageEvent(trap)];
        var events = new List<BattleEvent>();
        _escapeAttempts++;
        _turns++;
        int mine = EffectiveSpeed(Player), theirs = Math.Max(1, EffectiveSpeed(Foe));
        var odds = (mine * 128 / theirs + 30 * _escapeAttempts) % 256;
        if (runAway || mine >= theirs || _rng.Next(256) < odds)
        {
            events.Add(new MessageEvent("Got away safely!"));
            Outcome = BattleOutcome.Escaped;
            return events;
        }
        events.Add(new MessageEvent("Can't escape!"));
        FoeTurn(events);
        return events;
    }

    /// <summary>Switches to party member <paramref name="index"/> as the turn's action; the wild Pokémon then attacks the newcomer.</summary>
    public IReadOnlyList<BattleEvent> Switch(int index)
    {
        if (Waiting || !CanSendOut(index) || index == ActiveIndex)
            return [];
        if (Trapped(Player) is { } trap)
            return [Popup(Opponent(Player)), new MessageEvent(trap)];
        var events = new List<BattleEvent> { new MessageEvent($"{Player.Mon.Name}, come back!") };
        OnSwitchOut(Player);
        _turns++;
        Enter(index, events);
        FoeTurn(events);
        return events;
    }

    /// <summary>Replaces a fainted Pokémon with party member <paramref name="index"/> (no turn passes).</summary>
    public IReadOnlyList<BattleEvent> SendOut(int index)
    {
        if (!NeedsSwitch || !CanSendOut(index) || (Width > 1 && IsOut(index)))
            return [];
        var events = new List<BattleEvent>();
        if (Width > 1)
        {
            // Doubles and triples: fill the first empty slot; the turn goes on once all are filled.
            var slot = _playerReplacing[0];
            _playerReplacing.RemoveAt(0);
            Enter(index, events, slot);
            NeedsSwitch = _playerReplacing.Count > 0 && _party.Any(p => !p.Fainted && !_field[0].Any(b => ReferenceEquals(b.Mon, p)));
            if (!NeedsSwitch)
                _playerReplacing.Clear();
            return events;
        }
        NeedsSwitch = false;
        _playerReplacing.Clear();
        Enter(index, events);
        if (_pivoting)
        {
            // The rest of a turn in which the player's Pokémon switched out mid-turn.
            _pivoting = false;
            if (_pendingFoeMove is { } pending && !Foe.Mon.Fainted && !Over)
            {
                UseMove(Foe, Player, pending.Move, pending.Slot, movedFirst: false, events);
                Foe.Moved = true;
                CheckFaints(events);
            }
            _pendingFoeMove = null;
            if (!NeedsSwitch)
                EndOfTurn(events);
        }
        return events;
    }

    public bool CanSendOut(int index) => index >= 0 && index < _party.Count && !_party[index].Fainted;

    void Enter(int index, List<BattleEvent> events, int slot = 0)
    {
        var passed = _batonPass;
        _batonPass = null;
        var b = _field[0][slot] = new Battler(_party[index], BattleSide.Player, slot);
        if (passed is not null)
            Inherit(b, passed);
        foreach (var foe in _field[1])
            foe.FacedBy.Add(b.Mon);
        events.Add(new SwitchEvent(index, BattleSide.Player, slot));
        events.Add(new MessageEvent($"Go! {b.Mon.Name}!"));
        EntryHazards(b, events);
        OnEntry(b, events);
    }

    /// <summary>
    /// Uses item <paramref name="item"/> from the Bag: a Poké Ball is thrown at the wild Pokémon, medicine is used on
    /// party member <paramref name="target"/>. Uses up the turn unless the item would have no effect.
    /// </summary>
    public IReadOnlyList<BattleEvent> UseItem(int item, int target = -1)
    {
        if (Waiting || _bag.Count(item) <= 0 || ItemEffect.For(item) is not { } effect)
            return [];
        var name = _data.ItemName(item);
        var events = new List<BattleEvent>();
        if (effect is ItemEffect.Ball ball)
        {
            _bag.Take(item);
            _turns++;
            events.Add(new MessageEvent($"You threw a {name}!"));
            if (Trainer is not null)
            {
                events.Add(new MessageEvent("The Trainer blocked the Ball! Don't be a thief!"));
                FoeTurn(events);
                return events;
            }
            var shakes = CatchShakes(ball, item, Foe);
            var caught = shakes == 4;
            events.Add(new CatchEvent(item, Math.Min(3, shakes), caught));
            if (caught)
            {
                events.Add(new MessageEvent($"Gotcha! {Foe.Mon.Name} was caught!"));
                Caught = Foe.Mon;
                Outcome = BattleOutcome.Caught;
                return events;
            }
            events.Add(new MessageEvent(shakes switch
            {
                0 => "Oh no! The Pokémon broke free!",
                1 => "Aww! It appeared to be caught!",
                2 => "Aargh! Almost had it!",
                _ => "Gah! It was so close, too!",
            }));
            FoeTurn(events);
            return events;
        }

        if (target < 0 || target >= _party.Count)
            target = ActiveIndex;
        var mon = _party[target];
        var wasConfused = target == ActiveIndex && Player.ConfusionTurns > 0;
        var message = ItemUse.Apply(effect, mon);
        if (effect is ItemEffect.Cure { Status: Ailment.None } && wasConfused)
        {
            Player.ConfusionTurns = 0;
            message ??= $"{mon.Name} snapped out of its confusion.";
        }
        if (message is null)
            return [new MessageEvent("It won't have any effect.")];
        _bag.Take(item);
        _turns++;
        events.Add(new MessageEvent($"You used the {name}."));
        if (target == ActiveIndex)
        {
            events.Add(new HpEvent(BattleSide.Player, mon.Hp, mon.MaxHp, SlotOf(mon)));
            events.Add(new StatusEvent(BattleSide.Player, mon.Status, SlotOf(mon)));
        }
        events.Add(new MessageEvent(message));
        FoeTurn(events);
        return events;
    }

    /// <summary>
    /// Gen 5 capture: a = (3·max − 2·hp) · rate · ball / (3·max) × status (2.5 asleep/frozen, 1.5 other), then up to
    /// three shake checks against b = 65536 / (255 / a)^(3/16). Returns 4 when caught, else the shakes before breaking free.
    /// </summary>
    int CatchShakes(ItemEffect.Ball ball, int item, Battler wild)
    {
        if (ball.AlwaysCatches)
            return 4;
        var mon = wild.Mon;
        double max = mon.MaxHp, hp = Math.Max(1, mon.Hp);
        var a = (3 * max - 2 * hp) * mon.Species.CatchRate * BallMultiplier(ball, item, mon) / (3 * max);
        a *= mon.Status switch { Ailment.Sleep or Ailment.Freeze => 2.5, Ailment.None => 1, _ => 1.5 };
        if (a >= 255)
            return 4;
        var b = 65536 / Math.Pow(255 / Math.Max(a, 0.01), 3.0 / 16);
        for (var shake = 0; shake < 3; shake++)
            if (_rng.Next(65536) >= b)
                return shake;
        return 4;
    }

    /// <summary>A ball's bonus in this battle: Net (Bug/Water), Nest (low level), Timer (long battles), Quick (first turn).</summary>
    double BallMultiplier(ItemEffect.Ball ball, int item, Pokemon wild)
    {
        var types = wild.Types;
        return item switch
        {
            ItemEffect.NetBall when types.Contains(TypeChart.Bug) || types.Contains(TypeChart.Water) => 3,
            ItemEffect.NestBall => Math.Max(1, (41 - wild.Level) / 10.0),
            ItemEffect.TimerBall => Math.Min(4, 1 + _turns * 0.3),
            ItemEffect.QuickBall when _turns == 0 => 5,
            _ => ball.Multiplier,
        };
    }

    /// <summary>The foe's turn after the player spent theirs on something else (an item, a switch, a failed escape).</summary>
    void FoeTurn(List<BattleEvent> events)
    {
        if (Waiting)
            return;
        var usedItem = Forced(Foe) is null && TrainerUsesItem(events);
        if (!usedItem && (Forced(Foe) ?? Choose(Foe, FoeSlot())) is { } foeMove)
        {
            UseMove(Foe, Player, foeMove.Move, foeMove.Slot, movedFirst: true, events);
            CheckFaints(events);
        }
        EndOfTurn(events);
    }

    (MoveInfo Move, int Slot)? Choose(Battler b, int slot)
    {
        var moves = b.Mon.Moves;
        if (moves.All(m => m.Pp <= 0))
            return _data.Move(BattleData.Struggle) is { } struggle ? (struggle, -1) : null;
        if (b.Side == BattleSide.Foe && ChoiceLock(b) is { } locked && locked != 0)
            slot = moves.FindIndex(m => m.Move.Id == locked);
        if (b.Side == BattleSide.Foe && (slot >= moves.Count || moves[slot].Pp <= 0))
        {
            var usable = Enumerable.Range(0, moves.Count).Where(i => moves[i].Pp > 0).ToList();
            slot = usable[_rng.Next(usable.Count)];
        }
        if (slot < 0 || slot >= moves.Count || moves[slot].Pp <= 0)
            return null;
        return (moves[slot].Move, slot);
    }

    bool GoesFirst(MoveInfo mine, MoveInfo? theirs)
    {
        if (theirs is null)
            return true;
        int mp = Priority(Player, mine), tp = Priority(Foe, theirs);
        if (mp != tp)
            return mp > tp;
        // Stall moves last within its priority bracket.
        bool stallMine = Has(Player, Abilities.Stall), stallTheirs = Has(Foe, Abilities.Stall);
        if (stallMine != stallTheirs)
            return stallTheirs;
        int itemMine = ItemOrder(Player), itemTheirs = ItemOrder(Foe);
        if (itemMine != itemTheirs)
            return itemMine > itemTheirs;
        int a = EffectiveSpeed(Player), b = EffectiveSpeed(Foe);
        if (_trickRoom > 0)
            (a, b) = (b, a);
        return a != b ? a > b : _rng.Next(2) == 0;
    }

    int EffectiveSpeed(Battler b)
    {
        var speed = ItemSpeed(b, WeatherSpeed(b, Staged(b.Mon.StatValue(Stat.Speed), b.Stage(BattleStat.Speed))));
        if (Has(b, Abilities.SlowStart) && b.TurnsOut < 5)
            speed /= 2;
        if (SideOf(b).Tailwind > 0)
            speed *= 2;
        if (Has(b, Abilities.QuickFeet) && b.Mon.Status != Ailment.None)
            return speed * 3 / 2;
        return b.Mon.Status == Ailment.Paralysis ? speed / 4 : speed;
    }

    static int Staged(int value, int stage) => stage >= 0 ? value * (2 + stage) / 2 : value * 2 / (2 - stage);

    void UseMove(Battler user, Battler target, MoveInfo move, int slot, bool movedFirst, List<BattleEvent> events)
    {
        var name = Label(user);
        if (user.Recharging)
        {
            user.Recharging = false;
            events.Add(new MessageEvent($"{name} must recharge!"));
            return;
        }
        var secondTurn = user.Charging == move.Id;
        if (!CanAct(user, move, events))
        {
            // Being unable to move breaks a charge, a rampage and a Rollout.
            user.Charging = user.SemiInvulnerable = 0;
            user.LockedTurns = user.Rollout = 0;
            return;
        }
        if (move.Id == Moves.SleepTalk && user.Mon.Status == Ailment.Sleep)
        {
            var others = user.Mon.Moves.Where(m => m.Move.Id != Moves.SleepTalk && !Moves.TwoTurn(m.Move.Id)).ToList();
            if (slot >= 0)
                user.Mon.Moves[slot].Pp = Math.Max(0, user.Mon.Moves[slot].Pp - 1);
            events.Add(new MessageEvent($"{name} used {move.Name}!"));
            if (others.Count == 0)
            {
                events.Add(new MessageEvent("But it failed!"));
                return;
            }
            move = others[_rng.Next(others.Count)].Move;
            slot = -1;
        }
        else if (move.Id == Moves.SleepTalk)
        {
            events.Add(new MessageEvent($"{name} used {move.Name}!"));
            events.Add(new MessageEvent("But it failed!"));
            return;
        }
        if (slot >= 0 && user.Side == BattleSide.Foe && Restricted(user, move, events))
            return;
        if (slot >= 0)
        {
            var pp = user.Mon.Moves[slot];
            pp.Pp = Math.Max(0, pp.Pp - (Has(target, Abilities.Pressure) ? 2 : 1));
        }
        events.Add(new MessageEvent($"{name} used {move.Name}!"));
        LeppaBerry(user, slot, events);
        if (ItemOf(user) is HeldItems.ChoiceBand or HeldItems.ChoiceSpecs or HeldItems.ChoiceScarf && user.ChoiceMove == 0)
            user.ChoiceMove = move.Id;
        if (!Moves.ProtectLike(move.Id))
            user.ProtectChain = 0;
        if (move.Id != Moves.FuryCutter)
            user.FuryCutter = 0;
        user.LastMove = move.Id;

        // Moves that call another move.
        if (move.Id is Moves.Metronome or Moves.MirrorMove)
        {
            var called = move.Id == Moves.Metronome ? RandomMove() : _data.Move(target.LastMove);
            if (called is null || called.Id is Moves.Metronome or Moves.MirrorMove)
            {
                events.Add(new MessageEvent("But it failed!"));
                return;
            }
            events.Add(new MessageEvent($"{move.Name} turned into {called.Name}!"));
            UseMove(user, target, called, -1, movedFirst, events);
            return;
        }

        var type = MoveType(user, move);
        if (move.Id == Moves.HiddenPower)
            type = HiddenPowerType(user.Mon);
        if (WeatherMove(move) is var weather and not Weather.None)
        {
            if (!SetWeather(weather, HeldItems.RockWeather(ItemOf(user)) == weather ? 8 : 5, events))
                events.Add(new MessageEvent("But it failed!"));
            return;
        }

        // Two-turn moves charge first (Solar Beam fires at once in sun).
        if (Moves.TwoTurn(move.Id) && !secondTurn && !(move.Id == Moves.SolarBeam && ActiveWeather == Weather.Sun))
        {
            user.Charging = move.Id;
            if (move.Id is Moves.Fly or Moves.Dig or Moves.Dive or Moves.Bounce or Moves.ShadowForce)
                user.SemiInvulnerable = move.Id;
            events.Add(new MessageEvent(move.Id switch
            {
                Moves.Fly => $"{name} flew up high!",
                Moves.Dig => $"{name} burrowed its way under the ground!",
                Moves.Dive => $"{name} hid underwater!",
                Moves.Bounce => $"{name} sprang up!",
                Moves.SolarBeam => $"{name} absorbed light!",
                Moves.SkullBash => $"{name} tucked in its head!",
                Moves.SkyAttack => $"{name} became cloaked in a harsh light!",
                Moves.ShadowForce => $"{name} vanished instantly!",
                Moves.RazorWind => $"{name} whipped up a whirlwind!",
                _ => $"{name} became cloaked in freezing air!",
            }));
            if (move.Id == Moves.SkullBash)
                ChangeStage(user, BattleStat.Defense, 1, null, events);
            return;
        }
        user.Charging = user.SemiInvulnerable = 0;

        // Spread moves hit each target in turn (for ×0.75 each); everything else hits the one.
        var targets = _targets is { Count: > 0 } chosen ? chosen : [target];
        _spread = targets.Count > 1;
        var animated = false;
        for (var i = 0; i < targets.Count && !Over; i++)
        {
            if (i > 0 && (targets[i].Mon.Fainted || user.Mon.Fainted))
                continue;
            UseMoveOn(user, targets[i], move, type, movedFirst, last: i == targets.Count - 1, ref animated, events);
        }
        _spread = false;
    }

    /// <summary>The targets of the move being used (spread moves), set by the double/triple turn.</summary>
    List<Battler>? _targets;

    /// <summary>The part of using a move that happens to each target.</summary>
    void UseMoveOn(Battler user, Battler target, MoveInfo move, int type, bool movedFirst, bool last, ref bool animated, List<BattleEvent> events)
    {
        var name = Label(user);
        if (Fails(user, target, move) is { } failure)
        {
            events.Add(new MessageEvent(failure));
            return;
        }
        if (move.Category == MoveCategory.Status)
        {
            if (!animated)
                events.Add(new MoveAnimationEvent(user.Side, move.Id, user.Slot, target.Side, target.Slot));
            animated = true;
            if (StatusMove(user, target, move, events))
                return;
        }

        var selfTargeted = move.Target == 7 || (move.Kind == MoveKind.StatChange && move.StatChanges.All(c => c.Stages > 0))
            || move.Kind == MoveKind.Heal;
        if (!selfTargeted && target.Protected && move.Id is not (Moves.Feint or Moves.ShadowForce))
        {
            events.Add(new MessageEvent($"{Label(target)} protected itself!"));
            user.LockedTurns = user.Rollout = 0;
            return;
        }
        if (!selfTargeted && move.Id is Moves.Feint or Moves.ShadowForce && target.Protected)
        {
            target.Protected = false;
            events.Add(new MessageEvent($"{Label(target)} fell for the feint!"));
        }
        if (!selfTargeted && (!CanReach(user, target, move) || !Hits(user, target, move, movedFirst)))
        {
            events.Add(new MessageEvent(move.Kind == MoveKind.OneHitKo ? $"{Label(target)} is unaffected!" : $"{name}'s attack missed!"));
            user.LockedTurns = user.Rollout = 0;
            if (move.Id is Moves.JumpKick or Moves.HighJumpKick && !Has(user, Abilities.MagicGuard))
            {
                Damage(user, Math.Max(1, user.Mon.MaxHp / 2), events, $"{name} kept going and crashed!");
                CheckFaints(events);
            }
            if (move.Id is Moves.Explosion or Moves.SelfDestruct && last)
                Damage(user, user.Mon.Hp, events);
            return;
        }
        if (!selfTargeted && Absorbs(user, target, move, type, events))
            return;
        if (user.LockOn > 0 && !selfTargeted)
            user.LockOn = 0;
        if (!animated)
            events.Add(new MoveAnimationEvent(user.Side, move.Id, user.Slot, target.Side, target.Slot));
        animated = true;

        if (move.Kind == MoveKind.OneHitKo)
        {
            if (Effectiveness(user, target, type) == 0 || user.Mon.Level < target.Mon.Level)
            {
                events.Add(new MessageEvent($"It doesn't affect {Label(target)}..."));
                return;
            }
            if (Defends(target, user, Abilities.Sturdy))
            {
                events.Add(new AbilityEvent(target.Side, target.AbilityName, target.Slot));
                events.Add(new MessageEvent($"{Label(target)} is unaffected!"));
                return;
            }
            Damage(target, target.Mon.Hp, events);
            events.Add(new MessageEvent("It's a one-hit KO!"));
            return;
        }

        var dealt = 0;
        var hitSubstitute = false;
        if (move.Category != MoveCategory.Status && (move.Power > 0 || FixedDamage(user, target, move) is not null))
        {
            var effectiveness = move.Id == BattleData.Struggle ? 1 : Effectiveness(user, target, type);
            if (effectiveness == 0)
            {
                events.Add(new MessageEvent($"It doesn't affect {Label(target)}..."));
                user.LockedTurns = user.Rollout = 0;
                return;
            }
            var fixedDamage = FixedDamage(user, target, move);
            if (fixedDamage is null && effectiveness <= 1 && move.Id != BattleData.Struggle && Defends(target, user, Abilities.WonderGuard))
            {
                events.Add(new AbilityEvent(target.Side, target.AbilityName, target.Slot));
                events.Add(new MessageEvent($"It doesn't affect {Label(target)}..."));
                return;
            }
            var hits = HitCount(user, move);
            var landed = 0;
            var gem = fixedDamage is null && UseGem(user, move, type, events);
            var power = MovePower(user, target, move, movedFirst);
            for (var h = 0; h < hits && !target.Mon.Fainted && !user.Mon.Fainted; h++, landed++)
            {
                var crit = fixedDamage is null && IsCritical(user, target, move);
                var berry = fixedDamage is null && UseResistBerry(target, type, effectiveness, events);
                var damage = fixedDamage ?? CalculateDamage(user, target, move, type, crit, effectiveness, movedFirst, gem, berry, power);
                if (target.Substitute > 0 && user != target)
                {
                    // The substitute takes the hit instead.
                    hitSubstitute = true;
                    var taken = Math.Min(target.Substitute, damage);
                    target.Substitute -= taken;
                    dealt += taken;
                    events.Add(new MessageEvent($"The substitute took damage for {Label(target)}!"));
                    if (target.Substitute == 0)
                        events.Add(new MessageEvent($"{Label(target)}'s substitute faded!"));
                    continue;
                }
                // Sturdy: a Pokémon at full HP survives any one hit with 1 HP.
                var endured = target.Mon.Hp == target.Mon.MaxHp && damage >= target.Mon.Hp && Defends(target, user, Abilities.Sturdy);
                var hungOn = "";
                if (!endured && damage >= target.Mon.Hp && (target.Enduring || move.Id == Moves.FalseSwipe))
                {
                    damage = target.Mon.Hp - 1;
                    if (target.Enduring)
                        hungOn = $"{Label(target)} endured the hit!";
                }
                else if (!endured && HangsOn(target, damage, out hungOn))
                    damage = target.Mon.Hp - 1;
                if (endured)
                    damage = target.Mon.Hp - 1;
                var dealtHit = Damage(target, damage, events);
                dealt += dealtHit;
                if (move.Category == MoveCategory.Physical)
                    target.TakenPhysical += dealtHit;
                else
                    target.TakenSpecial += dealtHit;
                if (crit)
                    events.Add(new MessageEvent("A critical hit!"));
                if (endured)
                {
                    events.Add(new AbilityEvent(target.Side, target.AbilityName, target.Slot));
                    events.Add(new MessageEvent($"{Label(target)} endured the hit!"));
                }
                if (hungOn != "")
                    events.Add(new MessageEvent(hungOn));
                AfterHit(user, target, move, type, crit, events);
                ItemAfterHit(user, target, move, type, events);
            }
            if (fixedDamage is null && effectiveness > 1)
                events.Add(new MessageEvent("It's super effective!"));
            else if (fixedDamage is null && effectiveness < 1)
                events.Add(new MessageEvent("It's not very effective..."));
            if (hits > 1)
                events.Add(new MessageEvent($"Hit {landed} time(s)!"));
            if (move.Type == TypeChart.Electric)
                user.Charged = false;
            if (move.Id == Moves.FuryCutter)
                user.FuryCutter++;
            if (move.Id is Moves.Rollout or Moves.IceBall)
                user.Rollout = user.Rollout >= 4 ? 0 : user.Rollout + 1;

            if (move.Id == BattleData.Struggle)
                Damage(user, Math.Max(1, user.Mon.MaxHp / 4), events, $"{name} is hit with recoil!");
            else if (move.Recoil < 0 && dealt > 0 && !Has(user, Abilities.RockHead) && !Has(user, Abilities.MagicGuard))
                Damage(user, Math.Max(1, dealt * -move.Recoil / 100), events, $"{name} is hit with recoil!");
            else if (move.Recoil > 0 && dealt > 0)
            {
                var drained = Math.Max(1, dealt * move.Recoil / 100);
                if (ItemOf(user) == HeldItems.BigRoot)
                    drained = drained * 13 / 10;
                if (Has(target, Abilities.LiquidOoze))
                {
                    events.Add(new AbilityEvent(target.Side, target.AbilityName, target.Slot));
                    if (!Has(user, Abilities.MagicGuard))
                        Damage(user, drained, events, $"{name} sucked up the liquid ooze!");
                }
                else
                    Heal(user, drained, events, $"{Label(target)} had its energy drained!");
            }
            if (target.Mon.Fainted)
                OnKnockOut(user, target, move, events);
            ItemAfterMove(user, move, dealt, events);
            if (move.Id is Moves.Explosion or Moves.SelfDestruct or Moves.FinalGambit && last)
                Damage(user, user.Mon.Hp, events);
            if (Moves.Rampage(move.Id))
            {
                if (user.LockedTurns == 0)
                {
                    user.LockedMove = move.Id;
                    user.LockedTurns = 1 + _rng.Next(2);
                }
                else if (--user.LockedTurns == 0 && !user.Mon.Fainted && user.ConfusionTurns == 0)
                {
                    user.ConfusionTurns = 2 + _rng.Next(4);
                    events.Add(new MessageEvent($"{name} became confused due to fatigue!"));
                }
            }
            AfterDamagingMove(user, target, move, dealt, events);
            if (Over || NeedsSwitch && _pivoting)
                return;
        }

        if (move.Kind == MoveKind.Heal && move.Heal > 0)
        {
            if (user.Mon.Hp >= user.Mon.MaxHp)
                events.Add(new MessageEvent($"{name}'s HP is full!"));
            else
                Heal(user, Math.Max(1, user.Mon.MaxHp * HealPercent(move) / 100), events, $"{name} regained health!");
        }

        if (target.Mon.Fainted && move.Category != MoveCategory.Status)
            return;
        // A substitute shields its owner from the move's other effects.
        if (hitSubstitute || (target.Substitute > 0 && !selfTargeted && move.Category == MoveCategory.Status))
        {
            if (move.Category == MoveCategory.Status)
                events.Add(new MessageEvent("But it failed!"));
            return;
        }
        if (move.Ailment > Ailment.Confusion && !selfTargeted && VolatileAilment(user, target, move, events))
        {
            ApplyStatChanges(user, target, move, events);
            return;
        }
        ApplyAilment(user, target, move, events);
        if (move.Id is Moves.Toxic or Moves.PoisonFang && target.Mon.Status == Ailment.Poison && target.Toxic == 0)
            target.Toxic = 1;
        if (move.Id == Moves.SmellingSalt && target.Mon.Status == Ailment.Paralysis)
            SetStatus(target, Ailment.None, events, $"{Label(target)} was cured of paralysis.");
        if (move.Id == Moves.WakeUpSlap && target.Mon.Status == Ailment.Sleep)
            SetStatus(target, Ailment.None, events, $"{Label(target)} woke up!");
        ApplyStatChanges(user, target, move, events);
        var flinch = move.FlinchChance > 0 ? move.FlinchChance
            : (Has(user, Abilities.Stench) || ItemOf(user) is HeldItems.KingsRock or HeldItems.RazorFang)
              && move.Category != MoveCategory.Status && dealt > 0 ? 10 : 0;
        if (flinch > 0 && movedFirst && !target.Mon.Fainted && SecondaryLands(user, target, flinch)
            && !Defends(target, user, Abilities.InnerFocus))
            target.Flinched = true;

        if (move.Category == MoveCategory.Status && move.Ailment == Ailment.None && move.StatChanges.Count == 0
            && move.Kind is not MoveKind.Heal && move.Id is not (Moves.Charge or Moves.Defog))
            events.Add(new MessageEvent("But nothing happened!"));
    }

    /// <summary>Metronome's pick: any move but the ones that call other moves, Struggle and a few others.</summary>
    MoveInfo? RandomMove()
    {
        for (var tries = 0; tries < 50; tries++)
            if (_data.Move(1 + _rng.Next(559)) is { } m && m.Id is not (Moves.Metronome or Moves.MirrorMove or Moves.SleepTalk
                    or BattleData.Struggle or Moves.Protect or Moves.Detect or Moves.Endure or Moves.Counter or Moves.MirrorCoat
                    or Moves.FocusPunch or Moves.Thief or Moves.Covet or Moves.Trick or Moves.Switcheroo))
                return m;
        return null;
    }

    /// <summary>Sleep, freeze, paralysis, flinch and confusion checks before moving.</summary>
    bool CanAct(Battler user, MoveInfo move, List<BattleEvent> events)
    {
        var name = Label(user);
        if (user.Flinched)
        {
            user.Flinched = false;
            events.Add(new MessageEvent($"{name} flinched and couldn't move!"));
            if (Has(user, Abilities.Steadfast))
            {
                events.Add(new AbilityEvent(user.Side, user.AbilityName, user.Slot));
                ChangeStage(user, BattleStat.Speed, 1, null, events);
            }
            return false;
        }
        if (Has(user, Abilities.Truant) && user.Loafing)
        {
            user.Loafing = false;
            events.Add(new AbilityEvent(user.Side, user.AbilityName, user.Slot));
            events.Add(new MessageEvent($"{name} is loafing around!"));
            return false;
        }
        switch (user.Mon.Status)
        {
            case Ailment.Sleep:
                // Early Bird wakes up twice as fast.
                user.Mon.SleepTurns -= Has(user, Abilities.EarlyBird) ? 2 : 1;
                if (user.Mon.SleepTurns > 0)
                {
                    events.Add(new MessageEvent($"{name} is fast asleep."));
                    if (move.Id is Moves.Snore or Moves.SleepTalk)
                        break;
                    return false;
                }
                SetStatus(user, Ailment.None, events, $"{name} woke up!");
                break;
            case Ailment.Freeze:
                if (!Roll(20))
                {
                    events.Add(new MessageEvent($"{name} is frozen solid!"));
                    return false;
                }
                SetStatus(user, Ailment.None, events, $"{name} thawed out!");
                break;
            case Ailment.Paralysis when Roll(25):
                events.Add(new MessageEvent($"{name} is paralyzed! It can't move!"));
                return false;
        }
        if (user.ConfusionTurns > 0)
        {
            if (--user.ConfusionTurns == 0)
                events.Add(new MessageEvent($"{name} snapped out of its confusion!"));
            else
            {
                events.Add(new MessageEvent($"{name} is confused!"));
                if (Roll(50))
                {
                    // A 40-power typeless physical hit on itself, without crits or the random roll's STAB.
                    var damage = BaseDamage(user.Mon.Level, 40, Staged(user.Mon.StatValue(Stat.Attack), user.Stage(BattleStat.Attack)),
                        Math.Max(1, Staged(user.Mon.StatValue(Stat.Defense), user.Stage(BattleStat.Defense))));
                    Damage(user, damage * (85 + _rng.Next(16)) / 100, events, "It hurt itself in its confusion!");
                    CheckFaints(events);
                    return false;
                }
            }
        }
        if (user.Infatuated)
        {
            events.Add(new MessageEvent($"{name} is in love with {Label(Opponent(user))}!"));
            if (Roll(50))
            {
                events.Add(new MessageEvent($"{name} is immobilized by love!"));
                return false;
            }
        }
        user.Loafing = Has(user, Abilities.Truant);
        return true;
    }

    bool Hits(Battler user, Battler target, MoveInfo move, bool movedFirst = true)
    {
        var weatherAccuracy = WeatherAccuracy(move);
        if (move.NeverMisses || move.Accuracy == 0 || weatherAccuracy > 100 || Has(user, Abilities.NoGuard) || Has(target, Abilities.NoGuard)
            || user.LockOn > 0)
            return true;
        if (move.Kind == MoveKind.OneHitKo)
            return _rng.Next(100) < move.Accuracy + user.Mon.Level - target.Mon.Level;
        var accuracy = weatherAccuracy ?? move.Accuracy;
        if (move.Category == MoveCategory.Status && Defends(target, user, Abilities.WonderSkin))
            accuracy = Math.Min(50, accuracy);
        // Unaware ignores the other side's accuracy or evasion stages.
        var accStage = Defends(target, user, Abilities.Unaware) ? 0 : user.Stage(BattleStat.Accuracy);
        var evaStage = Has(user, Abilities.Unaware) || target.IdentifiedBy != 0 ? 0 : target.Stage(BattleStat.Evasion);
        var stage = Math.Clamp(accStage - evaStage, -6, 6);
        var chance = stage >= 0 ? accuracy * (3 + stage) / 3 : accuracy * 3 / (3 - stage);
        if (Has(user, Abilities.CompoundEyes))
            chance = chance * 13 / 10;
        if (Has(user, Abilities.VictoryStar))
            chance = chance * 11 / 10;
        if (Has(user, Abilities.Hustle) && move.Category == MoveCategory.Physical)
            chance = chance * 4 / 5;
        if ((ActiveWeather == Weather.Sandstorm && Defends(target, user, Abilities.SandVeil))
            || (ActiveWeather == Weather.Hail && Defends(target, user, Abilities.SnowCloak)))
            chance = chance * 4 / 5;
        if (ItemOf(target) is HeldItems.BrightPowder or HeldItems.LaxIncense)
            chance = chance * 9 / 10;
        if (ItemOf(user) == HeldItems.WideLens)
            chance = chance * 11 / 10;
        if (ItemOf(user) == HeldItems.ZoomLens && !movedFirst)
            chance = chance * 6 / 5;
        return _rng.Next(100) < chance;
    }

    int HitCount(Battler user, MoveInfo move)
    {
        if (move.MaxHits <= 1)
            return 1;
        if (Has(user, Abilities.SkillLink))
            return move.MaxHits;
        if (move.MinHits == move.MaxHits)
            return move.MinHits;
        if (move is { MinHits: 2, MaxHits: 5 })
            return _rng.Next(6) switch { 0 or 1 => 2, 2 or 3 => 3, 4 => 4, _ => 5 };
        return _rng.Next(move.MinHits, move.MaxHits + 1);
    }

    bool IsCritical(Battler user, Battler target, MoveInfo move)
    {
        if (Defends(target, user, Abilities.BattleArmor) || Defends(target, user, Abilities.ShellArmor))
            return false;
        // Gen 5 stages: 1/16, 1/8, 1/4, 1/3, 1/2.
        int[] odds = [16, 8, 4, 3, 2];
        var stage = move.CritStage + (Has(user, Abilities.SuperLuck) ? 1 : 0) + (user.FocusEnergy ? 2 : 0)
            + ItemOf(user) switch
            {
                HeldItems.ScopeLens or HeldItems.RazorClaw => 1,
                HeldItems.LuckyPunch when user.Mon.Species.Id == 113 => 2,
                HeldItems.Stick when user.Mon.Species.Id == 83 => 2,
                _ => 0,
            };
        return _rng.Next(odds[Math.Clamp(stage, 0, odds.Length - 1)]) == 0;
    }

    static int BaseDamage(int level, int power, int attack, int defense) => (2 * level / 5 + 2) * power * attack / defense / 50 + 2;

    /// <summary>
    /// Gen 5 damage: base damage, then ×2 on a critical hit (×3 with Sniper), the 85–100% random roll, ×1.5 STAB (×2 with
    /// Adaptability), type effectiveness and ×0.5 for a burned physical attacker, flooring after each step, with the
    /// abilities that change power, attack, defense or the result. Critical hits ignore the attacker's negative and the
    /// defender's positive stat stages.
    /// </summary>
    int CalculateDamage(Battler user, Battler target, MoveInfo move, int type, bool crit, double effectiveness, bool movedFirst,
        bool gem = false, bool resistBerry = false, int? movePower = null)
    {
        var physical = move.Category == MoveCategory.Physical;
        var atkStage = Defends(target, user, Abilities.Unaware) ? 0 : user.Stage(physical ? BattleStat.Attack : BattleStat.SpAttack);
        var defStage = Has(user, Abilities.Unaware) ? 0 : target.Stage(physical ? BattleStat.Defense : BattleStat.SpDefense);
        if (crit)
        {
            atkStage = Math.Max(0, atkStage);
            defStage = Math.Min(0, defStage);
        }
        var attack = ItemAttack(user, physical, Staged(user.Mon.StatValue(physical ? Stat.Attack : Stat.SpAttack), atkStage));
        var defense = Math.Max(1, ItemDefense(target, physical, Staged(target.Mon.StatValue(physical ? Stat.Defense : Stat.SpDefense), defStage)));
        var power = ItemPower(user, move, type, WeatherPower(move, movePower ?? move.Power));
        if (gem)
            power = power * 3 / 2;
        var status = user.Mon.Status;
        var weather = ActiveWeather;

        // The attacker's abilities.
        if (Has(user, Abilities.Technician) && power <= 60)
            power = power * 3 / 2;
        if (Has(user, Abilities.Reckless) && move.Recoil < 0 && move.Id != BattleData.Struggle)
            power = power * 6 / 5;
        if (Has(user, Abilities.SheerForce) && HasSecondary(move))
            power = power * 13 / 10;
        if (Has(user, Abilities.Analytic) && !movedFirst)
            power = power * 13 / 10;
        if (Has(user, Abilities.Rivalry) && user.Mon.Gender != Gender.Genderless && target.Mon.Gender != Gender.Genderless)
            power = user.Mon.Gender == target.Mon.Gender ? power * 5 / 4 : power * 3 / 4;
        if (user.FlashFire && type == TypeChart.Fire)
            attack = attack * 3 / 2;
        if (Abilities.PinchType(user.Ability) == type && user.Mon.Hp * 3 <= user.Mon.MaxHp)
            attack = attack * 3 / 2;
        if (physical && (Has(user, Abilities.HugePower) || Has(user, Abilities.PurePower)))
            attack *= 2;
        if (physical && Has(user, Abilities.Hustle))
            attack = attack * 3 / 2;
        if (physical && Has(user, Abilities.Guts) && status != Ailment.None)
            attack = attack * 3 / 2;
        if ((physical && Has(user, Abilities.ToxicBoost) && status == Ailment.Poison)
            || (!physical && Has(user, Abilities.FlareBoost) && status == Ailment.Burn))
            attack = attack * 3 / 2;
        if (Has(user, Abilities.Defeatist) && user.Mon.Hp * 2 <= user.Mon.MaxHp)
            attack /= 2;
        if (physical && Has(user, Abilities.SlowStart) && user.TurnsOut < 5)
            attack /= 2;
        if (weather == Weather.Sun && ((!physical && Has(user, Abilities.SolarPower)) || (physical && Has(user, Abilities.FlowerGift))))
            attack = attack * 3 / 2;
        if (weather == Weather.Sandstorm && Has(user, Abilities.SandForce) && type is TypeChart.Rock or TypeChart.Ground or TypeChart.Steel)
            power = power * 13 / 10;

        // The target's.
        if (type is TypeChart.Fire or TypeChart.Ice && Defends(target, user, Abilities.ThickFat))
            attack /= 2;
        if (type == TypeChart.Fire && Defends(target, user, Abilities.Heatproof))
            power /= 2;
        if (type == TypeChart.Fire && Defends(target, user, Abilities.DrySkin))
            power = power * 5 / 4;
        if (physical && target.Mon.Status != Ailment.None && Defends(target, user, Abilities.MarvelScale))
            defense = defense * 3 / 2;
        // Sandstorm raises Rock types' Sp. Def; Flower Gift raises its holder's in sun.
        if (!physical && ((weather == Weather.Sandstorm && target.Types.Contains(TypeChart.Rock))
            || (weather == Weather.Sun && Defends(target, user, Abilities.FlowerGift))))
            defense = defense * 3 / 2;

        var damage = BaseDamage(user.Mon.Level, Math.Max(1, power), Math.Max(1, attack), defense);
        damage = (int)(damage * WeatherDamage(type));
        if (crit)
            damage *= Has(user, Abilities.Sniper) ? 3 : 2;
        damage = damage * (85 + _rng.Next(16)) / 100;
        if (move.Id != BattleData.Struggle && user.Types.Contains(type))
            damage = Has(user, Abilities.Adaptability) ? damage * 2 : damage * 3 / 2;
        damage = (int)(damage * effectiveness);
        if (effectiveness < 1 && Has(user, Abilities.TintedLens))
            damage *= 2;
        if (effectiveness > 1 && (Defends(target, user, Abilities.Filter) || Defends(target, user, Abilities.SolidRock)))
            damage = damage * 3 / 4;
        if (target.Mon.Hp == target.Mon.MaxHp && Defends(target, user, Abilities.Multiscale))
            damage /= 2;
        if (effectiveness > 1 && ItemOf(user) == HeldItems.ExpertBelt)
            damage = damage * 6 / 5;
        if (ItemOf(user) == HeldItems.LifeOrb)
            damage = damage * 13 / 10;
        if (resistBerry)
            damage /= 2;
        // A move that hits more than one Pokémon does three quarters to each.
        if (_spread)
            damage = damage * 3 / 4;
        // Reflect and Light Screen halve hits that aren't critical (by a third with more than one Pokémon a side).
        var side = SideOf(target);
        if (!crit && (physical ? side.Reflect : side.LightScreen) > 0)
            damage = Width > 1 ? damage * 2 / 3 : damage / 2;
        if (physical && status == Ailment.Burn && !Has(user, Abilities.Guts))
            damage /= 2;
        return Math.Max(1, damage);
    }

    void ApplyAilment(Battler user, Battler target, MoveInfo move, List<BattleEvent> events)
    {
        if (move.Ailment == Ailment.None)
            return;
        var status = move.Category == MoveCategory.Status;
        var chance = status ? 100 : move.AilmentChance;
        var victim = move.Target == 7 ? user : target;
        if (chance <= 0 || !(status || victim == user ? Roll(chance) : SecondaryLands(user, target, chance)))
            return;
        var name = Label(victim);
        if (move.Ailment == Ailment.Confusion)
        {
            if (victim.ConfusionTurns > 0)
            {
                if (status)
                    events.Add(new MessageEvent($"{name} is already confused!"));
                return;
            }
            if (victim != user && Defends(victim, user, Abilities.OwnTempo))
            {
                if (status)
                {
                    events.Add(new AbilityEvent(victim.Side, victim.AbilityName, victim.Slot));
                    events.Add(new MessageEvent($"{name} doesn't become confused!"));
                }
                return;
            }
            victim.ConfusionTurns = 2 + _rng.Next(4);
            events.Add(new MessageEvent($"{name} became confused!"));
            StatusBerry(victim, events);
            return;
        }
        if (move.Ailment > Ailment.Poison)
            return;   // Volatile conditions beyond confusion aren't implemented yet.
        // Electric status moves (Thunder Wave) don't affect Ground types.
        Inflict(victim, move.Ailment, victim == user ? null : user, events, loud: status,
            groundImmune: status && move.Type == TypeChart.Electric);
    }

    void ApplyStatChanges(Battler user, Battler target, MoveInfo move, List<BattleEvent> events)
    {
        foreach (var change in move.StatChanges)
        {
            var victim = move.Kind switch
            {
                MoveKind.DamageRaiseUser => user,
                MoveKind.DamageLowerFoe or MoveKind.Swagger => target,
                _ when move.Target == 7 => user,
                _ => change.Stages > 0 ? user : target,
            };
            var guaranteed = move.Category == MoveCategory.Status || change.Chance == 0;
            if (!guaranteed && !(victim == user ? !Has(user, Abilities.SheerForce) && Roll(change.Chance * Grace(user)) : SecondaryLands(user, target, change.Chance)))
                continue;
            if (victim.Mon.Fainted)
                continue;
            var stats = change.Stat == BattleStat.All
                ? new[] { BattleStat.Attack, BattleStat.Defense, BattleStat.SpAttack, BattleStat.SpDefense, BattleStat.Speed }
                : [change.Stat];
            foreach (var stat in stats)
                ChangeStage(victim, stat, change.Stages, victim == user ? null : user, events);
        }
    }

    void EndOfTurn(List<BattleEvent> events)
    {
        WeatherEndOfTurn(events);
        foreach (var b in BySpeed(AllField))
        {
            b.Flinched = false;
            if (Over || b.Mon.Fainted)
                continue;
            if (b.Mon.Status == Ailment.Poison && Has(b, Abilities.PoisonHeal))
            {
                if (b.Mon.Hp < b.Mon.MaxHp)
                {
                    events.Add(new AbilityEvent(b.Side, b.AbilityName, b.Slot));
                    Heal(b, Math.Max(1, b.Mon.MaxHp / 8), events, $"{Label(b)} is healed by poison!");
                }
            }
            else if (b.Mon.Status is Ailment.Burn or Ailment.Poison && !Has(b, Abilities.MagicGuard))
            {
                // Heatproof halves burn damage; bad poison grows by 1/16 a turn.
                var share = b.Mon.Status == Ailment.Burn && Has(b, Abilities.Heatproof) ? 16 : 8;
                var amount = b.Mon.Status == Ailment.Poison && b.Toxic > 0 ? b.Mon.MaxHp * b.Toxic / 16 : b.Mon.MaxHp / share;
                if (b.Mon.Status == Ailment.Poison && b.Toxic > 0)
                    b.Toxic = Math.Min(15, b.Toxic + 1);
                Damage(b, Math.Max(1, amount), events,
                    b.Mon.Status == Ailment.Burn ? $"{Label(b)} is hurt by its burn!" : $"{Label(b)} is hurt by poison!");
                CheckFaints(events);
            }
            if (!b.Mon.Fainted && !Over)
                MoveEndOfTurn(b, events);
            if (!b.Mon.Fainted && !Over)
                AbilityEndOfTurn(b, events);
            if (!b.Mon.Fainted && !Over)
                ItemEndOfTurn(b, events);
            b.TurnsOut++;
        }
        if (!Over)
            SideEndOfTurn(events);
        foreach (var b in AllField)
        {
            b.Protected = b.Enduring = b.Moved = false;
            b.TakenPhysical = b.TakenSpecial = 0;
        }

        // A trainer sends out their next Pokémon once the turn is over; it's fought by whoever is out now.
        foreach (var slot in _foeReplacing)
        {
            if (Over || NextFoe() is not { } next)
                break;
            SendOutFoe(next, events, $"{Trainer?.Title ?? "The foe"} sent out {_foeParty[next].Name}!", slot);
        }
        _foeReplacing.Clear();
    }

    /// <summary>A trainer's next Pokémon that can come out, or null.</summary>
    int? NextFoe()
    {
        for (var i = 0; i < _foeParty.Count; i++)
            if (!_foeParty[i].Fainted && !_field[1].Any(b => ReferenceEquals(b.Mon, _foeParty[i])))
                return i;
        return null;
    }

    void CheckFaints(List<BattleEvent> events)
    {
        if (Over)
            return;
        foreach (var foe in _field[1])
        {
            if (!foe.Mon.Fainted || foe.Gone)
                continue;
            foe.Gone = true;
            events.Add(new FaintEvent(BattleSide.Foe, foe.Slot));
            events.Add(new MessageEvent($"{Label(foe)} fainted!"));
            AwardExp(foe, events);
            if (_foeParty.Any(p => !p.Fainted))
            {
                if (NextFoe() is not null)
                    _foeReplacing.Add(foe.Slot);
            }
            else
            {
                Outcome = BattleOutcome.Won;
                if (_payDay > 0)
                {
                    PrizeMoney += _payDay;
                    events.Add(new MessageEvent($"You picked up ₽{_payDay}!"));
                }
                if (Trainer is { } trainer)
                {
                    // The trainer steps back out and says their line from the ROM.
                    events.Add(new TrainerEvent(true));
                    events.AddRange(TrainerInfo.Pages(trainer.Defeat).Select(page => new MessageEvent(page)));
                    PrizeMoney = trainer.PrizeBase * 4 * _foeParty[^1].Level;
                    // Amulet Coin and Luck Incense double it.
                    if (_party.Any(p => p.HeldItem is HeldItems.AmuletCoin or HeldItems.LuckIncense))
                        PrizeMoney *= 2;
                    events.Add(new MessageEvent($"You defeated {trainer.Title}!"));
                    events.Add(new MessageEvent($"You got ₽{PrizeMoney} for winning!"));
                }
            }
        }
        foreach (var mine in _field[0])
        {
            if (!mine.Mon.Fainted || mine.Gone)
                continue;
            mine.Gone = true;
            events.Add(new FaintEvent(BattleSide.Player, mine.Slot));
            events.Add(new MessageEvent($"{mine.Mon.Name} fainted!"));
            if (Outcome == BattleOutcome.Won)
                return;
            if (_party.All(p => p.Fainted))
            {
                events.Add(new MessageEvent("You have no more Pokémon that can fight! You blacked out!"));
                Outcome = BattleOutcome.Lost;
                return;
            }
            // Someone on the bench takes its place (in doubles, only while there is someone left to send).
            if (_party.Any(p => !p.Fainted && !_field[0].Any(b => ReferenceEquals(b.Mon, p))))
            {
                _playerReplacing.Add(mine.Slot);
                NeedsSwitch = true;
            }
        }
    }

    /// <summary>
    /// Gen 5 scaled experience for beating a wild Pokémon, shared by <paramref name="participants"/> Pokémon:
    /// base EXP × level / 5 / participants × ((2L + 10) / (L + Lp + 10))^2.5 + 1.
    /// </summary>
    public static int ExpYield(Pokemon defeated, Pokemon winner, int participants = 1, bool trainer = false) =>
        ExpShare(defeated, winner, 1.0 / Math.Max(1, participants), trainer);

    /// <summary>Scaled experience for <paramref name="share"/> of the whole; a Lucky Egg adds half.</summary>
    static int ExpShare(Pokemon defeated, Pokemon winner, double share, bool trainer)
    {
        double lv = defeated.Level, own = winner.Level;
        var scaled = defeated.Species.BaseExp * lv / 5 * share * Math.Pow((2 * lv + 10) / (lv + own + 10), 2.5);
        var exp = (Math.Floor(scaled) + 1) * (trainer ? 1.5 : 1);
        return (int)Math.Floor(winner.HeldItem == HeldItems.LuckyEgg ? Math.Floor(exp) * 1.5 : exp);
    }

    /// <summary>
    /// Every Pokémon that was sent out against the defeated one and is still standing shares the experience; with
    /// an Exp. Share in the party, the fighters split half and the holders the other half.
    /// </summary>
    void AwardExp(Battler defeated, List<BattleEvent> events)
    {
        // In party order (FacedBy is a set of references, whose order differs between processes; a server and its
        // client must play the same events).
        var fighters = defeated.FacedBy.Where(p => !p.Fainted && IndexOf(p) >= 0).OrderBy(IndexOf).ToList();
        var holders = _party.Where(p => !p.Fainted && p.HeldItem == HeldItems.ExpShare).ToList();
        var earners = fighters.Union(holders).ToList();
        foreach (var mon in earners)
        {
            if (mon.Level >= 100)
                continue;
            var index = IndexOf(mon);
            var share = (fighters.Contains(mon) ? (holders.Count > 0 ? 0.5 : 1.0) / fighters.Count : 0)
                + (holders.Contains(mon) ? 0.5 / holders.Count : 0);
            var exp = ExpShare(defeated.Mon, mon, share, Trainer is not null);
            var levels = mon.GainExp(exp, _data);
            events.Add(new MessageEvent($"{mon.Name} gained {exp} Exp. Points!"));
            events.Add(new ExpEvent(index, exp, mon.Level));
            foreach (var (level, learned, skipped) in levels)
            {
                events.Add(new LevelUpEvent(index, level, mon.MaxHp, mon.Hp));
                events.Add(new MessageEvent($"{mon.Name} grew to Lv. {level}!"));
                foreach (var move in learned)
                    events.Add(new MessageEvent($"{mon.Name} learned {move.Name}!"));
                foreach (var move in skipped)
                    events.Add(new LearnMoveEvent(index, move));
            }
        }
    }

    int Damage(Battler b, int amount, List<BattleEvent> events, string? message = null)
    {
        var dealt = Math.Min(b.Mon.Hp, Math.Max(0, amount));
        b.Mon.Hp -= dealt;
        if (message is not null)
            events.Add(new MessageEvent(message));
        events.Add(new HpEvent(b.Side, b.Mon.Hp, b.Mon.MaxHp, b.Slot));
        return dealt;
    }

    void Heal(Battler b, int amount, List<BattleEvent> events, string message)
    {
        b.Mon.Hp = Math.Min(b.Mon.MaxHp, b.Mon.Hp + amount);
        events.Add(new HpEvent(b.Side, b.Mon.Hp, b.Mon.MaxHp, b.Slot));
        events.Add(new MessageEvent(message));
    }

    void SetStatus(Battler b, Ailment status, List<BattleEvent> events, string message)
    {
        b.Mon.Status = status;
        events.Add(new StatusEvent(b.Side, status, b.Slot));
        events.Add(new MessageEvent(message));
    }

    bool Roll(int percent) => _rng.Next(100) < percent;

    string Label(Battler b) => b.Side != BattleSide.Foe ? b.Mon.Name : Trainer is null ? $"The wild {b.Mon.Name}" : $"The foe's {b.Mon.Name}";

    static string StatName(BattleStat stat) => stat switch
    {
        BattleStat.Attack => "Attack",
        BattleStat.Defense => "Defense",
        BattleStat.SpAttack => "Sp. Atk",
        BattleStat.SpDefense => "Sp. Def",
        BattleStat.Speed => "Speed",
        BattleStat.Accuracy => "accuracy",
        _ => "evasiveness",
    };

    static string Describe(Ailment a) => a switch
    {
        Ailment.Paralysis => "paralyzed",
        Ailment.Sleep => "asleep",
        Ailment.Freeze => "frozen",
        Ailment.Burn => "burned",
        _ => "poisoned",
    };
}
