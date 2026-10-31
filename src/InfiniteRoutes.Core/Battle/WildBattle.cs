using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Core.Battle;

public enum BattleSide { Player, Foe }

public enum BattleOutcome { Ongoing, Won, Lost, Escaped, Caught }

/// <summary>Something that happened in a turn, in order, for the UI to play back.</summary>
public abstract record BattleEvent;
public sealed record MessageEvent(string Text) : BattleEvent;
/// <summary>A side's HP is now <see cref="Hp"/> (after damage or healing).</summary>
public sealed record HpEvent(BattleSide Side, int Hp, int MaxHp) : BattleEvent;
public sealed record StatusEvent(BattleSide Side, Ailment Status) : BattleEvent;
public sealed record FaintEvent(BattleSide Side) : BattleEvent;
/// <summary>Party member <see cref="PartyIndex"/> gained experience; <see cref="Level"/> is its level afterwards.</summary>
public sealed record ExpEvent(int PartyIndex, int Gained, int Level) : BattleEvent;
public sealed record LevelUpEvent(int PartyIndex, int Level, int MaxHp, int Hp) : BattleEvent;
/// <summary>The player sent out party member <see cref="PartyIndex"/>.</summary>
public sealed record SwitchEvent(int PartyIndex) : BattleEvent;
/// <summary>A Poké Ball was thrown: it shook <see cref="Shakes"/> times (0–3) and caught the Pokémon or not.</summary>
public sealed record CatchEvent(int Ball, int Shakes, bool Caught) : BattleEvent;

/// <summary>A Pokémon on the field: the Pokémon plus what resets when it leaves (stat stages, confusion, flinch).</summary>
public sealed class Battler(Pokemon mon, BattleSide side)
{
    public Pokemon Mon { get; } = mon;
    public BattleSide Side { get; } = side;
    /// <summary>Stat stages −6…+6, indexed by <see cref="BattleStat"/> (Attack … Evasion).</summary>
    public int[] Stages { get; } = new int[8];
    public int ConfusionTurns { get; set; }
    public bool Flinched { get; set; }

    public int Stage(BattleStat stat) => Stages[(int)stat];
}

/// <summary>
/// A single battle against one wild Pokémon, with Generation 5 rules: priority and speed order, accuracy and evasion,
/// critical hits, STAB, type effectiveness, random damage roll, burn, poison, paralysis, sleep, freeze, confusion,
/// flinching, stat stages, multi-hit, recoil, drain, healing, one-hit KOs, escape odds, Poké Balls and medicine,
/// switching, and experience shared between the Pokémon that fought. Moves whose special effects aren't
/// implemented yet still deal their damage.
/// </summary>
public sealed class WildBattle
{
    readonly BattleData _data;
    readonly Random _rng;
    readonly IReadOnlyList<Pokemon> _party;
    readonly Bag _bag;
    readonly HashSet<Pokemon> _participants = new();
    int _escapeAttempts;
    int _turns;

    public Battler Player { get; private set; }
    public Battler Foe { get; }
    public BattleOutcome Outcome { get; private set; }
    public bool Over => Outcome != BattleOutcome.Ongoing;

    /// <summary>The player's Pokémon fainted and another can fight: call <see cref="SendOut"/> before the next action.</summary>
    public bool NeedsSwitch { get; private set; }

    public IReadOnlyList<Pokemon> Party => _party;
    public Bag Bag => _bag;
    public int ActiveIndex => IndexOf(Player.Mon);

    /// <summary>A battle with the party's first able Pokémon leading.</summary>
    public WildBattle(BattleData data, IReadOnlyList<Pokemon> party, Bag bag, Pokemon wild, Random rng)
    {
        _data = data;
        _rng = rng;
        _party = party;
        _bag = bag;
        Player = new Battler(party.FirstOrDefault(p => !p.Fainted) ?? throw new ArgumentException("No Pokémon can fight.", nameof(party)), BattleSide.Player);
        _participants.Add(Player.Mon);
        Foe = new Battler(wild, BattleSide.Foe);
    }

    /// <summary>A one-on-one battle with no items (for tests and simple uses).</summary>
    public WildBattle(BattleData data, Pokemon player, Pokemon wild, Random rng) : this(data, [player], new Bag(), wild, rng) { }

    public IReadOnlyList<BattleEvent> Start() =>
        [new MessageEvent($"A wild {Foe.Mon.Name} appeared!"), new MessageEvent($"Go! {Player.Mon.Name}!")];

    /// <summary>True when no move has PP left: <see cref="Fight"/> then uses Struggle whatever the slot.</summary>
    public bool MustStruggle => Player.Mon.Moves.All(m => m.Pp <= 0);

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
        var playerMove = Choose(Player, slot);
        if (playerMove is null)
            return [new MessageEvent("There's no PP left for this move!")];
        var foeMove = Choose(Foe, _rng.Next(Math.Max(1, Foe.Mon.Moves.Count)));
        _turns++;

        var playerFirst = GoesFirst(playerMove.Value.Move, foeMove?.Move);
        var order = playerFirst
            ? new[] { (Player, Foe, playerMove), (Foe, Player, foeMove) }
            : new[] { (Foe, Player, foeMove), (Player, Foe, playerMove) };
        for (var i = 0; i < order.Length && !Waiting; i++)
        {
            var (user, target, choice) = order[i];
            if (choice is null || user.Mon.Fainted)
                continue;
            UseMove(user, target, choice.Value.Move, choice.Value.Slot, movedFirst: i == 0, events);
            CheckFaints(events);
        }
        EndOfTurn(events);
        return events;
    }

    /// <summary>Tries to run. On failure the wild Pokémon gets a free turn.</summary>
    public IReadOnlyList<BattleEvent> Run()
    {
        if (Waiting)
            return [];
        var events = new List<BattleEvent>();
        _escapeAttempts++;
        _turns++;
        int mine = EffectiveSpeed(Player), theirs = Math.Max(1, EffectiveSpeed(Foe));
        var odds = (mine * 128 / theirs + 30 * _escapeAttempts) % 256;
        if (mine >= theirs || _rng.Next(256) < odds)
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
        var events = new List<BattleEvent> { new MessageEvent($"{Player.Mon.Name}, come back!") };
        _turns++;
        Enter(index, events);
        FoeTurn(events);
        return events;
    }

    /// <summary>Replaces a fainted Pokémon with party member <paramref name="index"/> (no turn passes).</summary>
    public IReadOnlyList<BattleEvent> SendOut(int index)
    {
        if (!NeedsSwitch || !CanSendOut(index))
            return [];
        NeedsSwitch = false;
        var events = new List<BattleEvent>();
        Enter(index, events);
        return events;
    }

    public bool CanSendOut(int index) => index >= 0 && index < _party.Count && !_party[index].Fainted;

    void Enter(int index, List<BattleEvent> events)
    {
        Player = new Battler(_party[index], BattleSide.Player);
        _participants.Add(Player.Mon);
        events.Add(new SwitchEvent(index));
        events.Add(new MessageEvent($"Go! {Player.Mon.Name}!"));
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
            var shakes = CatchShakes(ball, item);
            var caught = shakes == 4;
            events.Add(new CatchEvent(item, Math.Min(3, shakes), caught));
            if (caught)
            {
                events.Add(new MessageEvent($"Gotcha! {Foe.Mon.Name} was caught!"));
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
            events.Add(new HpEvent(BattleSide.Player, mon.Hp, mon.MaxHp));
            events.Add(new StatusEvent(BattleSide.Player, mon.Status));
        }
        events.Add(new MessageEvent(message));
        FoeTurn(events);
        return events;
    }

    /// <summary>
    /// Gen 5 capture: a = (3·max − 2·hp) · rate · ball / (3·max) × status (2.5 asleep/frozen, 1.5 other), then up to
    /// three shake checks against b = 65536 / (255 / a)^(3/16). Returns 4 when caught, else the shakes before breaking free.
    /// </summary>
    int CatchShakes(ItemEffect.Ball ball, int item)
    {
        if (ball.AlwaysCatches)
            return 4;
        var mon = Foe.Mon;
        double max = mon.MaxHp, hp = Math.Max(1, mon.Hp);
        var a = (3 * max - 2 * hp) * mon.Species.CatchRate * BallMultiplier(ball, item) / (3 * max);
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
    double BallMultiplier(ItemEffect.Ball ball, int item)
    {
        var types = Foe.Mon.Types;
        return item switch
        {
            ItemEffect.NetBall when types.Contains(TypeChart.Bug) || types.Contains(TypeChart.Water) => 3,
            ItemEffect.NestBall => Math.Max(1, (41 - Foe.Mon.Level) / 10.0),
            ItemEffect.TimerBall => Math.Min(4, 1 + _turns * 0.3),
            ItemEffect.QuickBall when _turns == 0 => 5,
            _ => ball.Multiplier,
        };
    }

    /// <summary>The wild Pokémon's move after the player spent the turn on something else.</summary>
    void FoeTurn(List<BattleEvent> events)
    {
        if (Waiting)
            return;
        if (Choose(Foe, _rng.Next(Math.Max(1, Foe.Mon.Moves.Count))) is { } foeMove)
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
        if (mine.Priority != theirs.Priority)
            return mine.Priority > theirs.Priority;
        int a = EffectiveSpeed(Player), b = EffectiveSpeed(Foe);
        return a != b ? a > b : _rng.Next(2) == 0;
    }

    int EffectiveSpeed(Battler b)
    {
        var speed = Staged(b.Mon.StatValue(Stat.Speed), b.Stage(BattleStat.Speed));
        return b.Mon.Status == Ailment.Paralysis ? speed / 4 : speed;
    }

    static int Staged(int value, int stage) => stage >= 0 ? value * (2 + stage) / 2 : value * 2 / (2 - stage);

    void UseMove(Battler user, Battler target, MoveInfo move, int slot, bool movedFirst, List<BattleEvent> events)
    {
        var name = Label(user);
        if (!CanAct(user, events))
            return;
        if (slot >= 0)
            user.Mon.Moves[slot].Pp--;
        events.Add(new MessageEvent($"{name} used {move.Name}!"));

        var selfTargeted = move.Target == 7 || (move.Kind == MoveKind.StatChange && move.StatChanges.All(c => c.Stages > 0))
            || move.Kind == MoveKind.Heal;
        if (!selfTargeted && !Hits(user, target, move))
        {
            events.Add(new MessageEvent(move.Kind == MoveKind.OneHitKo ? $"{Label(target)} is unaffected!" : $"{name}'s attack missed!"));
            return;
        }

        if (move.Kind == MoveKind.OneHitKo)
        {
            if (TypeChart.Against(move.Type, target.Mon.Types) == 0 || user.Mon.Level < target.Mon.Level)
            {
                events.Add(new MessageEvent($"It doesn't affect {Label(target)}..."));
                return;
            }
            Damage(target, target.Mon.Hp, events);
            events.Add(new MessageEvent("It's a one-hit KO!"));
            return;
        }

        var dealt = 0;
        if (move.Category != MoveCategory.Status && move.Power > 0)
        {
            var effectiveness = TypeChart.Against(move.Type, target.Mon.Types);
            if (move.Id == BattleData.Struggle)
                effectiveness = 1;
            if (effectiveness == 0)
            {
                events.Add(new MessageEvent($"It doesn't affect {Label(target)}..."));
                return;
            }
            var hits = HitCount(move);
            var landed = 0;
            for (var h = 0; h < hits && !target.Mon.Fainted && !user.Mon.Fainted; h++, landed++)
            {
                var crit = IsCritical(move);
                var damage = CalculateDamage(user, target, move, crit, effectiveness);
                dealt += Damage(target, damage, events);
                if (crit)
                    events.Add(new MessageEvent("A critical hit!"));
            }
            if (effectiveness > 1)
                events.Add(new MessageEvent("It's super effective!"));
            else if (effectiveness < 1)
                events.Add(new MessageEvent("It's not very effective..."));
            if (hits > 1)
                events.Add(new MessageEvent($"Hit {landed} time(s)!"));

            if (move.Id == BattleData.Struggle)
                Damage(user, Math.Max(1, user.Mon.MaxHp / 4), events, $"{name} is hit with recoil!");
            else if (move.Recoil < 0 && dealt > 0)
                Damage(user, Math.Max(1, dealt * -move.Recoil / 100), events, $"{name} is hit with recoil!");
            else if (move.Recoil > 0 && dealt > 0)
                Heal(user, Math.Max(1, dealt * move.Recoil / 100), events, $"{Label(target)} had its energy drained!");
        }

        if (move.Kind == MoveKind.Heal && move.Heal > 0)
        {
            if (user.Mon.Hp >= user.Mon.MaxHp)
                events.Add(new MessageEvent($"{name}'s HP is full!"));
            else
                Heal(user, Math.Max(1, user.Mon.MaxHp * move.Heal / 100), events, $"{name} regained health!");
        }

        if (target.Mon.Fainted && move.Category != MoveCategory.Status)
            return;
        ApplyAilment(user, target, move, events);
        ApplyStatChanges(user, target, move, events);
        if (move.FlinchChance > 0 && movedFirst && !target.Mon.Fainted && Roll(move.FlinchChance))
            target.Flinched = true;

        if (move.Category == MoveCategory.Status && move.Ailment == Ailment.None && move.StatChanges.Count == 0
            && move.Kind is not MoveKind.Heal)
            events.Add(new MessageEvent("But nothing happened!"));
    }

    /// <summary>Sleep, freeze, paralysis, flinch and confusion checks before moving.</summary>
    bool CanAct(Battler user, List<BattleEvent> events)
    {
        var name = Label(user);
        if (user.Flinched)
        {
            user.Flinched = false;
            events.Add(new MessageEvent($"{name} flinched and couldn't move!"));
            return false;
        }
        switch (user.Mon.Status)
        {
            case Ailment.Sleep:
                if (--user.Mon.SleepTurns > 0)
                {
                    events.Add(new MessageEvent($"{name} is fast asleep."));
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
        return true;
    }

    bool Hits(Battler user, Battler target, MoveInfo move)
    {
        if (move.NeverMisses || move.Accuracy == 0)
            return true;
        if (move.Kind == MoveKind.OneHitKo)
            return _rng.Next(100) < move.Accuracy + user.Mon.Level - target.Mon.Level;
        var stage = Math.Clamp(user.Stage(BattleStat.Accuracy) - target.Stage(BattleStat.Evasion), -6, 6);
        var chance = stage >= 0 ? move.Accuracy * (3 + stage) / 3 : move.Accuracy * 3 / (3 - stage);
        return _rng.Next(100) < chance;
    }

    int HitCount(MoveInfo move)
    {
        if (move.MaxHits <= 1)
            return 1;
        if (move.MinHits == move.MaxHits)
            return move.MinHits;
        if (move is { MinHits: 2, MaxHits: 5 })
            return _rng.Next(6) switch { 0 or 1 => 2, 2 or 3 => 3, 4 => 4, _ => 5 };
        return _rng.Next(move.MinHits, move.MaxHits + 1);
    }

    bool IsCritical(MoveInfo move)
    {
        // Gen 5 stages: 1/16, 1/8, 1/4, 1/3, 1/2.
        int[] odds = [16, 8, 4, 3, 2];
        return _rng.Next(odds[Math.Clamp(move.CritStage, 0, odds.Length - 1)]) == 0;
    }

    static int BaseDamage(int level, int power, int attack, int defense) => (2 * level / 5 + 2) * power * attack / defense / 50 + 2;

    /// <summary>
    /// Gen 5 damage: base damage, then ×2 on a critical hit, the 85–100% random roll, ×1.5 STAB, type effectiveness
    /// and ×0.5 for a burned physical attacker, flooring after each step. Critical hits ignore the attacker's
    /// negative and the defender's positive stat stages.
    /// </summary>
    int CalculateDamage(Battler user, Battler target, MoveInfo move, bool crit, double effectiveness)
    {
        var physical = move.Category == MoveCategory.Physical;
        var atkStage = user.Stage(physical ? BattleStat.Attack : BattleStat.SpAttack);
        var defStage = target.Stage(physical ? BattleStat.Defense : BattleStat.SpDefense);
        if (crit)
        {
            atkStage = Math.Max(0, atkStage);
            defStage = Math.Min(0, defStage);
        }
        var attack = Staged(user.Mon.StatValue(physical ? Stat.Attack : Stat.SpAttack), atkStage);
        var defense = Math.Max(1, Staged(target.Mon.StatValue(physical ? Stat.Defense : Stat.SpDefense), defStage));

        var damage = BaseDamage(user.Mon.Level, move.Power, attack, defense);
        if (crit)
            damage *= 2;
        damage = damage * (85 + _rng.Next(16)) / 100;
        if (move.Id != BattleData.Struggle && user.Mon.Types.Contains(move.Type))
            damage = damage * 3 / 2;
        damage = (int)(damage * effectiveness);
        if (physical && user.Mon.Status == Ailment.Burn)
            damage /= 2;
        return Math.Max(1, damage);
    }

    void ApplyAilment(Battler user, Battler target, MoveInfo move, List<BattleEvent> events)
    {
        if (move.Ailment == Ailment.None)
            return;
        var chance = move.Category == MoveCategory.Status ? 100 : move.AilmentChance;
        if (chance <= 0 || !Roll(chance))
            return;
        var victim = move.Target == 7 ? user : target;
        var name = Label(victim);
        var types = victim.Mon.Types;
        if (move.Ailment == Ailment.Confusion)
        {
            if (victim.ConfusionTurns > 0)
            {
                if (move.Category == MoveCategory.Status)
                    events.Add(new MessageEvent($"{name} is already confused!"));
                return;
            }
            victim.ConfusionTurns = 2 + _rng.Next(4);
            events.Add(new MessageEvent($"{name} became confused!"));
            return;
        }
        if (move.Ailment > Ailment.Poison)
            return;   // Volatile conditions beyond confusion aren't implemented yet.

        var immune = victim.Mon.Status != Ailment.None
            || (move.Ailment == Ailment.Burn && types.Contains(TypeChart.Fire))
            || (move.Ailment == Ailment.Freeze && types.Contains(TypeChart.Ice))
            || (move.Ailment == Ailment.Poison && (types.Contains(TypeChart.Poison) || types.Contains(TypeChart.Steel)))
            || (move.Category == MoveCategory.Status && move.Type == TypeChart.Electric && types.Contains(TypeChart.Ground));
        if (immune)
        {
            if (move.Category == MoveCategory.Status)
                events.Add(new MessageEvent(victim.Mon.Status == move.Ailment ? $"{name} is already {Describe(move.Ailment)}!" : "But it failed!"));
            return;
        }
        if (move.Ailment == Ailment.Sleep)
            victim.Mon.SleepTurns = 2 + _rng.Next(3);   // 1–3 turns asleep, counted down before each move
        SetStatus(victim, move.Ailment, events, move.Ailment switch
        {
            Ailment.Paralysis => $"{name} is paralyzed! It may be unable to move!",
            Ailment.Sleep => $"{name} fell asleep!",
            Ailment.Freeze => $"{name} was frozen solid!",
            Ailment.Burn => $"{name} was burned!",
            _ => $"{name} was poisoned!",
        });
    }

    void ApplyStatChanges(Battler user, Battler target, MoveInfo move, List<BattleEvent> events)
    {
        foreach (var change in move.StatChanges)
        {
            var guaranteed = move.Category == MoveCategory.Status || change.Chance == 0;
            if (!guaranteed && !Roll(change.Chance))
                continue;
            var victim = move.Kind switch
            {
                MoveKind.DamageRaiseUser => user,
                MoveKind.DamageLowerFoe or MoveKind.Swagger => target,
                _ when move.Target == 7 => user,
                _ => change.Stages > 0 ? user : target,
            };
            if (victim.Mon.Fainted)
                continue;
            var stats = change.Stat == BattleStat.All
                ? new[] { BattleStat.Attack, BattleStat.Defense, BattleStat.SpAttack, BattleStat.SpDefense, BattleStat.Speed }
                : [change.Stat];
            foreach (var stat in stats)
            {
                var i = (int)stat;
                if (i <= 0 || i >= victim.Stages.Length)
                    continue;
                var before = victim.Stages[i];
                victim.Stages[i] = Math.Clamp(before + change.Stages, -6, 6);
                var delta = victim.Stages[i] - before;
                var statName = StatName(stat);
                events.Add(new MessageEvent(delta switch
                {
                    0 when change.Stages > 0 => $"{Label(victim)}'s {statName} won't go any higher!",
                    0 => $"{Label(victim)}'s {statName} won't go any lower!",
                    >= 2 => $"{Label(victim)}'s {statName} sharply rose!",
                    1 => $"{Label(victim)}'s {statName} rose!",
                    -1 => $"{Label(victim)}'s {statName} fell!",
                    _ => $"{Label(victim)}'s {statName} harshly fell!",
                }));
            }
        }
    }

    void EndOfTurn(List<BattleEvent> events)
    {
        foreach (var b in new[] { Player, Foe })
        {
            b.Flinched = false;
            if (Over || b.Mon.Fainted)
                continue;
            if (b.Mon.Status is Ailment.Burn or Ailment.Poison)
            {
                Damage(b, Math.Max(1, b.Mon.MaxHp / 8), events,
                    b.Mon.Status == Ailment.Burn ? $"{Label(b)} is hurt by its burn!" : $"{Label(b)} is hurt by poison!");
                CheckFaints(events);
            }
        }
    }

    void CheckFaints(List<BattleEvent> events)
    {
        if (Over)
            return;
        if (Foe.Mon.Fainted)
        {
            events.Add(new FaintEvent(BattleSide.Foe));
            events.Add(new MessageEvent($"{Label(Foe)} fainted!"));
            Outcome = BattleOutcome.Won;
            AwardExp(events);
        }
        if (Player.Mon.Fainted && !NeedsSwitch)
        {
            events.Add(new FaintEvent(BattleSide.Player));
            events.Add(new MessageEvent($"{Player.Mon.Name} fainted!"));
            if (Outcome == BattleOutcome.Won)
                return;
            if (_party.Any(p => !p.Fainted))
                NeedsSwitch = true;
            else
            {
                events.Add(new MessageEvent("You have no more Pokémon that can fight! You blacked out!"));
                Outcome = BattleOutcome.Lost;
            }
        }
    }

    /// <summary>
    /// Gen 5 scaled experience for beating a wild Pokémon, shared by <paramref name="participants"/> Pokémon:
    /// base EXP × level / 5 / participants × ((2L + 10) / (L + Lp + 10))^2.5 + 1.
    /// </summary>
    public static int ExpYield(Pokemon defeated, Pokemon winner, int participants = 1)
    {
        double lv = defeated.Level, own = winner.Level;
        var scaled = defeated.Species.BaseExp * lv / 5 / Math.Max(1, participants) * Math.Pow((2 * lv + 10) / (lv + own + 10), 2.5);
        return (int)Math.Floor(scaled) + 1;
    }

    /// <summary>Every Pokémon that was sent out against the wild one and is still standing shares the experience.</summary>
    void AwardExp(List<BattleEvent> events)
    {
        var earners = _participants.Where(p => !p.Fainted && IndexOf(p) >= 0).ToList();
        foreach (var mon in earners)
        {
            if (mon.Level >= 100)
                continue;
            var index = IndexOf(mon);
            var exp = ExpYield(Foe.Mon, mon, earners.Count);
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
                    events.Add(new MessageEvent($"{mon.Name} wants to learn {move.Name}, but it already knows four moves."));
            }
        }
    }

    int Damage(Battler b, int amount, List<BattleEvent> events, string? message = null)
    {
        var dealt = Math.Min(b.Mon.Hp, Math.Max(0, amount));
        b.Mon.Hp -= dealt;
        if (message is not null)
            events.Add(new MessageEvent(message));
        events.Add(new HpEvent(b.Side, b.Mon.Hp, b.Mon.MaxHp));
        return dealt;
    }

    void Heal(Battler b, int amount, List<BattleEvent> events, string message)
    {
        b.Mon.Hp = Math.Min(b.Mon.MaxHp, b.Mon.Hp + amount);
        events.Add(new HpEvent(b.Side, b.Mon.Hp, b.Mon.MaxHp));
        events.Add(new MessageEvent(message));
    }

    void SetStatus(Battler b, Ailment status, List<BattleEvent> events, string message)
    {
        b.Mon.Status = status;
        events.Add(new StatusEvent(b.Side, status));
        events.Add(new MessageEvent(message));
    }

    bool Roll(int percent) => _rng.Next(100) < percent;

    static string Label(Battler b) => b.Side == BattleSide.Foe ? $"The wild {b.Mon.Name}" : b.Mon.Name;

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
