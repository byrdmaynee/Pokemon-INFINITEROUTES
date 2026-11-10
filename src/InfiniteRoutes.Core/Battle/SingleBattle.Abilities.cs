using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Core.Battle;

/// <summary>Where abilities hook into a battle: entering and leaving, absorbing moves, reacting to hits, status and stat changes.</summary>
public sealed partial class SingleBattle
{
    static bool Has(Battler b, int ability) => b.Ability == ability;

    /// <summary>The target's defensive <paramref name="ability"/> applies, unless the attacker's Mold Breaker ignores it.</summary>
    static bool Defends(Battler target, Battler? attacker, int ability) =>
        target.Ability == ability
        && !(attacker is not null && attacker != target && Abilities.BreaksMold(attacker.Ability) && Abilities.Breakable.Contains(ability));

    Battler Opponent(Battler b) => b == Player ? Foe : Player;

    static AbilityEvent Popup(Battler b) => new(b.Side, b.AbilityName);

    static int Priority(Battler b, MoveInfo move) =>
        move.Priority + (Has(b, Abilities.Prankster) && move.Category == MoveCategory.Status ? 1 : 0);

    /// <summary>Normalize makes every move (but Struggle) Normal-type.</summary>
    static int MoveType(Battler user, MoveInfo move) =>
        Has(user, Abilities.Normalize) && move.Id != BattleData.Struggle ? TypeChart.Normal : move.Type;

    /// <summary>Type effectiveness; Scrappy's Normal and Fighting moves hit Ghost types.</summary>
    static double Effectiveness(Battler user, Battler target, int type)
    {
        var types = target.Types;
        if (Has(user, Abilities.Scrappy) && type is TypeChart.Normal or TypeChart.Fighting && types.Contains(TypeChart.Ghost))
        {
            types = types.Where(t => t != TypeChart.Ghost).ToList();
            if (types.Count == 0)
                return 1;
        }
        return TypeChart.Against(type, types);
    }

    /// <summary>Serene Grace doubles the chance of a move's secondary effects.</summary>
    static int Grace(Battler user) => Has(user, Abilities.SereneGrace) ? 2 : 1;

    /// <summary>A damaging move's chance-based effect on the target: none with Sheer Force or against Shield Dust.</summary>
    bool SecondaryLands(Battler user, Battler target, int chance) =>
        !Has(user, Abilities.SheerForce) && !Defends(target, user, Abilities.ShieldDust) && Roll(chance * Grace(user));

    /// <summary>The damaging moves Sheer Force powers up (and strips the extra effect from).</summary>
    static bool HasSecondary(MoveInfo move) =>
        move.Category != MoveCategory.Status
        && ((move.Ailment != Ailment.None && move.AilmentChance > 0) || move.FlinchChance > 0 || move.StatChanges.Any(c => c.Chance > 0));

    /// <summary>
    /// The foe's Shadow Tag, Arena Trap (grounded Pokémon) or Magnet Pull (Steel types) keeps <paramref name="b"/> from
    /// fleeing or switching; returns the message, or null when it can leave.
    /// </summary>
    string? Trapped(Battler b)
    {
        var foe = Opponent(b);
        if (foe.Mon.Fainted)
            return null;
        var trapped = foe.Ability switch
        {
            Abilities.ShadowTag => !Has(b, Abilities.ShadowTag),
            Abilities.ArenaTrap => !b.Types.Contains(TypeChart.Flying) && !Has(b, Abilities.Levitate),
            Abilities.MagnetPull => b.Types.Contains(TypeChart.Steel),
            _ => false,
        };
        return trapped ? $"{Label(foe)} prevents escape with {foe.AbilityName}!" : null;
    }

    /// <summary>Abilities that go off when <paramref name="b"/> comes out: Intimidate, Trace, Download, Pressure, Mold Breaker…</summary>
    void OnEntry(Battler b, List<BattleEvent> events)
    {
        var other = Opponent(b);
        if (b.Mon.Fainted || other.Mon.Fainted)
            return;
        var name = Label(b);
        switch (b.Ability)
        {
            case Abilities.Trace when !Abilities.Untraceable(other.Ability):
                events.Add(Popup(b));
                (b.Ability, b.AbilityName) = (other.Ability, other.AbilityName);
                events.Add(new MessageEvent($"{name} traced {Label(other)}'s {other.AbilityName}!"));
                OnEntry(b, events);   // the traced ability goes off too (it can't be Trace again)
                break;
            case Abilities.Intimidate:
                events.Add(Popup(b));
                ChangeStage(other, BattleStat.Attack, -1, b, events);
                break;
            case Abilities.Download:
            {
                events.Add(Popup(b));
                var def = Staged(other.Mon.StatValue(Stat.Defense), other.Stage(BattleStat.Defense));
                var spDef = Staged(other.Mon.StatValue(Stat.SpDefense), other.Stage(BattleStat.SpDefense));
                ChangeStage(b, def < spDef ? BattleStat.Attack : BattleStat.SpAttack, 1, null, events);
                break;
            }
            case Abilities.Pressure:
                events.Add(Popup(b));
                events.Add(new MessageEvent($"{name} is exerting its Pressure!"));
                break;
            case Abilities.MoldBreaker:
                events.Add(Popup(b));
                events.Add(new MessageEvent($"{name} breaks the mold!"));
                break;
            case Abilities.Turboblaze:
                events.Add(Popup(b));
                events.Add(new MessageEvent($"{name} is radiating a blazing aura!"));
                break;
            case Abilities.Teravolt:
                events.Add(Popup(b));
                events.Add(new MessageEvent($"{name} is radiating a bursting aura!"));
                break;
            case Abilities.SlowStart:
                events.Add(Popup(b));
                events.Add(new MessageEvent($"{name} can't get it going!"));
                break;
        }
    }

    /// <summary>Natural Cure and Regenerator when the player calls a Pokémon back.</summary>
    static void OnSwitchOut(Battler b)
    {
        if (b.Mon.Fainted)
            return;
        if (Has(b, Abilities.NaturalCure))
        {
            b.Mon.Status = Ailment.None;
            b.Mon.SleepTurns = 0;
        }
        else if (Has(b, Abilities.Regenerator))
            b.Mon.Hp = Math.Min(b.Mon.MaxHp, b.Mon.Hp + b.Mon.MaxHp / 3);
    }

    /// <summary>
    /// The target's ability takes the move instead of being hit: Levitate (Ground), Volt/Water Absorb and Dry Skin (heal),
    /// Flash Fire, Motor Drive, Lightning Rod, Storm Drain and Sap Sipper (a boost). True when the move is used up.
    /// </summary>
    bool Absorbs(Battler user, Battler target, MoveInfo move, int type, List<BattleEvent> events)
    {
        if (user == target)
            return false;
        var name = Label(target);
        var damaging = move.Category != MoveCategory.Status && move.Power > 0;
        if (type == TypeChart.Ground && damaging && Defends(target, user, Abilities.Levitate))
        {
            events.Add(Popup(target));
            events.Add(new MessageEvent($"It doesn't affect {name}..."));
            return true;
        }
        if ((type == TypeChart.Electric && Defends(target, user, Abilities.VoltAbsorb))
            || (type == TypeChart.Water && (Defends(target, user, Abilities.WaterAbsorb) || Defends(target, user, Abilities.DrySkin))))
        {
            events.Add(Popup(target));
            if (target.Mon.Hp >= target.Mon.MaxHp)
                events.Add(new MessageEvent($"It doesn't affect {name}..."));
            else
                Heal(target, Math.Max(1, target.Mon.MaxHp / 4), events, $"{name} restored its HP!");
            return true;
        }
        if (type == TypeChart.Fire && Defends(target, user, Abilities.FlashFire))
        {
            events.Add(Popup(target));
            target.FlashFire = true;
            events.Add(new MessageEvent($"The power of {name}'s Fire-type moves rose!"));
            return true;
        }
        var boost = type switch
        {
            TypeChart.Electric when Defends(target, user, Abilities.MotorDrive) => BattleStat.Speed,
            TypeChart.Electric when Defends(target, user, Abilities.LightningRod) => BattleStat.SpAttack,
            TypeChart.Water when Defends(target, user, Abilities.StormDrain) => BattleStat.SpAttack,
            TypeChart.Grass when Defends(target, user, Abilities.SapSipper) => BattleStat.Attack,
            _ => BattleStat.None,
        };
        if (boost == BattleStat.None)
            return false;
        events.Add(Popup(target));
        ChangeStage(target, boost, 1, null, events);
        return true;
    }

    /// <summary>Reactions to one landed hit: contact abilities, Anger Point, Justified, Rattled, Weak Armor, Poison Touch.</summary>
    void AfterHit(Battler user, Battler target, MoveInfo move, int type, bool crit, List<BattleEvent> events)
    {
        if (move.MakesContact && !user.Mon.Fainted)
        {
            var ailment = target.Ability switch
            {
                Abilities.Static => Ailment.Paralysis,
                Abilities.FlameBody => Ailment.Burn,
                Abilities.PoisonPoint => Ailment.Poison,
                Abilities.EffectSpore => _rng.Next(3) switch { 0 => Ailment.Poison, 1 => Ailment.Paralysis, _ => Ailment.Sleep },
                _ => Ailment.None,
            };
            if (ailment != Ailment.None && Roll(30) && CanInflict(user, ailment, target))
            {
                events.Add(Popup(target));
                Inflict(user, ailment, target, events, loud: false);
            }
            if (target.Ability is Abilities.RoughSkin or Abilities.IronBarbs && !Has(user, Abilities.MagicGuard))
            {
                events.Add(Popup(target));
                Damage(user, Math.Max(1, user.Mon.MaxHp / 8), events, $"{Label(user)} was hurt!");
            }
            if (Has(user, Abilities.PoisonTouch) && !target.Mon.Fainted && Roll(30) && CanInflict(target, Ailment.Poison, user))
            {
                events.Add(Popup(user));
                Inflict(target, Ailment.Poison, user, events, loud: false);
            }
        }
        if (target.Mon.Fainted)
            return;
        if (crit && Has(target, Abilities.AngerPoint) && target.Stage(BattleStat.Attack) < 6)
        {
            events.Add(Popup(target));
            target.Stages[(int)BattleStat.Attack] = 6;
            events.Add(new MessageEvent($"{Label(target)} maxed its Attack!"));
        }
        if (type == TypeChart.Dark && Has(target, Abilities.Justified))
        {
            events.Add(Popup(target));
            ChangeStage(target, BattleStat.Attack, 1, null, events);
        }
        if (type is TypeChart.Bug or TypeChart.Ghost or TypeChart.Dark && Has(target, Abilities.Rattled))
        {
            events.Add(Popup(target));
            ChangeStage(target, BattleStat.Speed, 1, null, events);
        }
        if (move.Category == MoveCategory.Physical && Has(target, Abilities.WeakArmor))
        {
            events.Add(Popup(target));
            ChangeStage(target, BattleStat.Defense, -1, null, events);
            ChangeStage(target, BattleStat.Speed, 1, null, events);
        }
    }

    /// <summary>The user's move knocked the target out: Moxie, and Aftermath on a contact move.</summary>
    void OnKnockOut(Battler user, Battler target, MoveInfo move, List<BattleEvent> events)
    {
        if (move.MakesContact && Has(target, Abilities.Aftermath) && !user.Mon.Fainted && !Has(user, Abilities.MagicGuard))
        {
            events.Add(Popup(target));
            Damage(user, Math.Max(1, user.Mon.MaxHp / 4), events, $"{Label(user)} was caught in the aftermath!");
        }
        if (Has(user, Abilities.Moxie) && !user.Mon.Fainted)
        {
            events.Add(Popup(user));
            ChangeStage(user, BattleStat.Attack, 1, null, events);
        }
    }

    /// <summary>End-of-turn abilities: Speed Boost, Shed Skin, Slow Start wearing off.</summary>
    void AbilityEndOfTurn(Battler b, List<BattleEvent> events)
    {
        if (Has(b, Abilities.SpeedBoost) && b.TurnsOut > 0 && b.Stage(BattleStat.Speed) < 6)
        {
            events.Add(Popup(b));
            ChangeStage(b, BattleStat.Speed, 1, null, events);
        }
        if (Has(b, Abilities.ShedSkin) && b.Mon.Status != Ailment.None && Roll(30))
        {
            events.Add(Popup(b));
            b.Mon.SleepTurns = 0;
            SetStatus(b, Ailment.None, events, $"{Label(b)}'s status returned to normal!");
        }
        if (Has(b, Abilities.SlowStart) && b.TurnsOut == 4)
        {
            events.Add(Popup(b));
            events.Add(new MessageEvent($"{Label(b)} finally got its act together!"));
        }
    }

    static int StatusBlocker(Ailment ailment) => ailment switch
    {
        Ailment.Paralysis => Abilities.Limber,
        Ailment.Poison => Abilities.Immunity,
        Ailment.Burn => Abilities.WaterVeil,
        Ailment.Freeze => Abilities.MagmaArmor,
        _ => Abilities.None,
    };

    static bool BlocksStatus(Battler victim, Ailment ailment, Battler? source) =>
        ailment == Ailment.Sleep
            ? Defends(victim, source, Abilities.Insomnia) || Defends(victim, source, Abilities.VitalSpirit)
            : StatusBlocker(ailment) is var blocker and not Abilities.None && Defends(victim, source, blocker);

    static bool TypeImmune(Battler victim, Ailment ailment, bool groundImmune)
    {
        var types = victim.Types;
        return (ailment == Ailment.Burn && types.Contains(TypeChart.Fire))
            || (ailment == Ailment.Freeze && types.Contains(TypeChart.Ice))
            || (ailment == Ailment.Poison && (types.Contains(TypeChart.Poison) || types.Contains(TypeChart.Steel)))
            || (groundImmune && types.Contains(TypeChart.Ground));
    }

    static bool CanInflict(Battler victim, Ailment ailment, Battler? source) =>
        !victim.Mon.Fainted && victim.Mon.Status == Ailment.None && !TypeImmune(victim, ailment, false) && !BlocksStatus(victim, ailment, source);

    /// <summary>
    /// Gives <paramref name="victim"/> a major status from <paramref name="source"/> (null when it did it to itself or an
    /// ability did), unless its type, current status or ability prevents it; <paramref name="loud"/> says why it failed.
    /// Synchronize passes a burn, paralysis or poison back to the source.
    /// </summary>
    bool Inflict(Battler victim, Ailment ailment, Battler? source, List<BattleEvent> events, bool loud, bool groundImmune = false)
    {
        var name = Label(victim);
        if (victim.Mon.Status != Ailment.None || TypeImmune(victim, ailment, groundImmune))
        {
            if (loud)
                events.Add(new MessageEvent(victim.Mon.Status == ailment ? $"{name} is already {Describe(ailment)}!" : "But it failed!"));
            return false;
        }
        if (BlocksStatus(victim, ailment, source))
        {
            if (loud)
            {
                events.Add(Popup(victim));
                events.Add(new MessageEvent($"It doesn't affect {name}..."));
            }
            return false;
        }
        if (ailment == Ailment.Sleep)
            victim.Mon.SleepTurns = 2 + _rng.Next(3);   // 1–3 turns asleep, counted down before each move
        SetStatus(victim, ailment, events, ailment switch
        {
            Ailment.Paralysis => $"{name} is paralyzed! It may be unable to move!",
            Ailment.Sleep => $"{name} fell asleep!",
            Ailment.Freeze => $"{name} was frozen solid!",
            Ailment.Burn => $"{name} was burned!",
            _ => $"{name} was poisoned!",
        });
        if (source is not null && source != victim && ailment is Ailment.Burn or Ailment.Paralysis or Ailment.Poison
            && Has(victim, Abilities.Synchronize) && CanInflict(source, ailment, null))
        {
            events.Add(Popup(victim));
            Inflict(source, ailment, null, events, loud: false);
        }
        return true;
    }

    /// <summary>
    /// Moves <paramref name="victim"/>'s <paramref name="stat"/> stage by <paramref name="stages"/>, caused by
    /// <paramref name="source"/> (null for its own move or ability). Simple doubles and Contrary reverses the change;
    /// Clear Body, White Smoke, Hyper Cutter, Keen Eye and Big Pecks stop the foe lowering it; Defiant answers a drop
    /// with +2 Attack. Returns the change made.
    /// </summary>
    int ChangeStage(Battler victim, BattleStat stat, int stages, Battler? source, List<BattleEvent> events)
    {
        var i = (int)stat;
        if (i <= 0 || i >= victim.Stages.Length || victim.Mon.Fainted)
            return 0;
        if (Defends(victim, source, Abilities.Simple))
            stages *= 2;
        if (Defends(victim, source, Abilities.Contrary))
            stages = -stages;
        var name = Label(victim);
        var statName = StatName(stat);
        var byFoe = source is not null && source != victim;
        if (stages < 0 && byFoe)
        {
            var blocked = Defends(victim, source, Abilities.ClearBody) || Defends(victim, source, Abilities.WhiteSmoke)
                || (stat == BattleStat.Attack && Defends(victim, source, Abilities.HyperCutter))
                || (stat == BattleStat.Accuracy && Defends(victim, source, Abilities.KeenEye))
                || (stat == BattleStat.Defense && Defends(victim, source, Abilities.BigPecks));
            if (blocked)
            {
                events.Add(Popup(victim));
                events.Add(new MessageEvent($"{name}'s {statName} was not lowered!"));
                return 0;
            }
        }
        var before = victim.Stages[i];
        victim.Stages[i] = Math.Clamp(before + stages, -6, 6);
        var delta = victim.Stages[i] - before;
        events.Add(new MessageEvent(delta switch
        {
            0 when stages > 0 => $"{name}'s {statName} won't go any higher!",
            0 => $"{name}'s {statName} won't go any lower!",
            >= 2 => $"{name}'s {statName} sharply rose!",
            1 => $"{name}'s {statName} rose!",
            -1 => $"{name}'s {statName} fell!",
            _ => $"{name}'s {statName} harshly fell!",
        }));
        if (delta < 0 && byFoe && Has(victim, Abilities.Defiant))
        {
            events.Add(Popup(victim));
            ChangeStage(victim, BattleStat.Attack, 2, null, events);
        }
        return delta;
    }
}
