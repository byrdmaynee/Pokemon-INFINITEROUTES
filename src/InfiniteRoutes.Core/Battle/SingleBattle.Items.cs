using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Core.Battle;

/// <summary>
/// Held items in battle: power and stat items, Choice items, Life Orb, gems, berries (HP, status, pinch and
/// type-resist), Leftovers, Focus Sash, Rocky Helmet, Air Balloon, weather rocks and the rest of <see cref="HeldItems"/>.
/// Consumed items (berries, gems, Focus Sash, herbs) are gone for good, as in the games.
/// </summary>
public sealed partial class SingleBattle
{
    /// <summary>The item in effect: none for a Pokémon with Klutz.</summary>
    static int ItemOf(Battler b) => Has(b, Abilities.Klutz) ? 0 : b.Mon.HeldItem;

    string ItemName(Battler b) => _data.ItemName(b.Mon.HeldItem);

    static void Consume(Battler b)
    {
        if (HeldItems.IsBerry(b.Mon.HeldItem))
            b.LastBerry = b.Mon.HeldItem;
        b.Mon.HeldItem = 0;
        b.LostItem = true;
    }

    /// <summary>Unnerve on the other side keeps berries uneaten.</summary>
    bool CanEatBerry(Battler b) => HeldItems.IsBerry(ItemOf(b)) && !Has(Opponent(b), Abilities.Unnerve);

    /// <summary>Species items: Light Ball (Pikachu), Thick Club (Cubone, Marowak), DeepSeaTooth (Clamperl), Soul Dew (Latias, Latios).</summary>
    static int ItemAttack(Battler b, bool physical, int attack)
    {
        var item = ItemOf(b);
        var species = b.Mon.Species.Id;
        if ((physical && item == HeldItems.ChoiceBand) || (!physical && item == HeldItems.ChoiceSpecs))
            attack = attack * 3 / 2;
        if ((item == HeldItems.LightBall && species == 25) || (physical && item == HeldItems.ThickClub && species is 104 or 105)
            || (!physical && item == HeldItems.DeepSeaTooth && species == 366))
            attack *= 2;
        if (!physical && item == HeldItems.SoulDew && species is 380 or 381)
            attack = attack * 3 / 2;
        return attack;
    }

    /// <summary>Metal Powder (Ditto), DeepSeaScale (Clamperl), Soul Dew (Latias, Latios).</summary>
    static int ItemDefense(Battler b, bool physical, int defense)
    {
        var item = ItemOf(b);
        var species = b.Mon.Species.Id;
        if ((physical && item == HeldItems.MetalPowder && species == 132) || (!physical && item == HeldItems.DeepSeaScale && species == 366))
            return defense * 2;
        if (!physical && item == HeldItems.SoulDew && species is 380 or 381)
            return defense * 3 / 2;
        return defense;
    }

    /// <summary>Type items, Plates and incenses ×1.2; Muscle Band and Wise Glasses ×1.1.</summary>
    static int ItemPower(Battler b, MoveInfo move, int type, int power)
    {
        var item = ItemOf(b);
        if (HeldItems.BoostedType(item) == type)
            power = power * 6 / 5;
        if ((item == HeldItems.MuscleBand && move.Category == MoveCategory.Physical)
            || (item == HeldItems.WiseGlasses && move.Category == MoveCategory.Special))
            power = power * 11 / 10;
        return power;
    }

    /// <summary>Choice Scarf ×1.5, Iron Ball/Macho Brace/Power items ×0.5, Quick Powder (Ditto) ×2, Unburden ×2 once its item is gone.</summary>
    static int ItemSpeed(Battler b, int speed)
    {
        var item = ItemOf(b);
        if (item == HeldItems.ChoiceScarf)
            speed = speed * 3 / 2;
        if (HeldItems.Slows(item))
            speed /= 2;
        if (item == HeldItems.QuickPowder && b.Mon.Species.Id == 132)
            speed *= 2;
        if (Has(b, Abilities.Unburden) && b.LostItem && b.Mon.HeldItem == 0)
            speed *= 2;
        return speed;
    }

    /// <summary>Quick Claw (20%) moves first and Lagging Tail/Full Incense last within a priority bracket: +1, −1 or 0.</summary>
    int ItemOrder(Battler b)
    {
        var item = ItemOf(b);
        if (item is HeldItems.LaggingTail or HeldItems.FullIncense)
            return -1;
        return item == HeldItems.QuickClaw && Roll(20) ? 1 : 0;
    }

    /// <summary>A gem matching the move's type is used up to power it ×1.5; true when one was.</summary>
    bool UseGem(Battler user, MoveInfo move, int type, List<BattleEvent> events)
    {
        if (HeldItems.GemType(ItemOf(user)) != type || move.Category == MoveCategory.Status || move.Power <= 0)
            return false;
        events.Add(new MessageEvent($"The {ItemName(user)} strengthened {move.Name}'s power!"));
        Consume(user);
        return true;
    }

    /// <summary>A type-resist berry halves a super-effective hit of its type (Chilan: any Normal hit), once.</summary>
    bool UseResistBerry(Battler target, int type, double effectiveness, List<BattleEvent> events)
    {
        var item = ItemOf(target);
        if (HeldItems.ResistBerryType(item) != type || !(effectiveness > 1 || item == HeldItems.ChilanBerry) || !CanEatBerry(target))
            return false;
        events.Add(new MessageEvent($"The {ItemName(target)} weakened the damage to {Label(target)}!"));
        Consume(target);
        return true;
    }

    /// <summary>Focus Sash (at full HP, used up) or Focus Band (10%) leaves the target 1 HP from a knockout hit.</summary>
    bool HangsOn(Battler target, int damage, out string message)
    {
        message = "";
        if (damage < target.Mon.Hp)
            return false;
        var item = ItemOf(target);
        if (item == HeldItems.FocusSash && target.Mon.Hp == target.Mon.MaxHp)
        {
            message = $"{Label(target)} hung on using its {ItemName(target)}!";
            Consume(target);
            return true;
        }
        if (item == HeldItems.FocusBand && Roll(10))
        {
            message = $"{Label(target)} hung on using its {ItemName(target)}!";
            return true;
        }
        return false;
    }

    /// <summary>Reactions to a hit: Rocky Helmet, Absorb Bulb, Cell Battery, Air Balloon popping, then the target's HP berries.</summary>
    void ItemAfterHit(Battler user, Battler target, MoveInfo move, int type, List<BattleEvent> events)
    {
        var item = ItemOf(target);
        if (item == HeldItems.RockyHelmet && move.MakesContact && !user.Mon.Fainted && !Has(user, Abilities.MagicGuard))
            Damage(user, Math.Max(1, user.Mon.MaxHp / 6), events, $"{Label(user)} was hurt by {Label(target)}'s {ItemName(target)}!");
        if (target.Mon.Fainted)
            return;
        if (item == HeldItems.AirBalloon)
        {
            events.Add(new MessageEvent($"{Label(target)}'s {ItemName(target)} popped!"));
            Consume(target);
        }
        else if ((item == HeldItems.AbsorbBulb && type == TypeChart.Water) || (item == HeldItems.CellBattery && type == TypeChart.Electric))
        {
            events.Add(new MessageEvent($"{Label(target)} used its {ItemName(target)}!"));
            var stat = item == HeldItems.AbsorbBulb ? BattleStat.SpAttack : BattleStat.Attack;
            Consume(target);
            ChangeStage(target, stat, 1, null, events);
        }
        HpBerry(target, events);
    }

    /// <summary>
    /// HP berries at half HP (Oran +10, Sitrus +¼, Figy–Iapapa +⅛) and stat berries at a quarter (half with Gluttony).
    /// </summary>
    void HpBerry(Battler b, List<BattleEvent> events)
    {
        if (b.Mon.Fainted || !CanEatBerry(b))
            return;
        var item = ItemOf(b);
        var name = Label(b);
        var hp = b.Mon.Hp;
        var max = b.Mon.MaxHp;
        if (hp * 2 <= max && item is HeldItems.OranBerry or HeldItems.SitrusBerry or >= HeldItems.FigyBerry and <= HeldItems.IapapaBerry)
        {
            var heal = item switch { HeldItems.OranBerry => 10, HeldItems.SitrusBerry => max / 4, _ => max / 8 };
            var berry = ItemName(b);
            Consume(b);
            Heal(b, Math.Max(1, heal), events, $"{name} restored its health using its {berry}!");
            return;
        }
        var pinch = Has(b, Abilities.Gluttony) ? hp * 2 <= max : hp * 4 <= max;
        if (pinch && HeldItems.PinchStat(item) is var stat and not BattleStat.None)
        {
            events.Add(new MessageEvent($"{name} ate its {ItemName(b)}!"));
            Consume(b);
            ChangeStage(b, stat, 1, null, events);
        }
    }

    /// <summary>Status berries cure their condition as soon as it's given (Lum any; Persim and Lum confusion).</summary>
    void StatusBerry(Battler b, List<BattleEvent> events)
    {
        if (b.Mon.Fainted || !CanEatBerry(b))
            return;
        var item = ItemOf(b);
        var cures = HeldItems.CuredBy(item);
        if (b.Mon.Status != Ailment.None && cures is { } ailment && (ailment == Ailment.None || ailment == b.Mon.Status))
        {
            var berry = ItemName(b);
            var was = b.Mon.Status;
            Consume(b);
            b.Mon.SleepTurns = 0;
            SetStatus(b, Ailment.None, events, $"{Label(b)}'s {berry} cured its {Describe(was)} status!");
            return;
        }
        if (b.ConfusionTurns > 0 && item is HeldItems.PersimBerry or HeldItems.LumBerry)
        {
            events.Add(new MessageEvent($"{Label(b)}'s {ItemName(b)} snapped it out of its confusion!"));
            Consume(b);
            b.ConfusionTurns = 0;
        }
    }

    /// <summary>After the user's move: Life Orb recoil (not with Sheer Force's boost or Magic Guard) and Shell Bell healing.</summary>
    void ItemAfterMove(Battler user, MoveInfo move, int dealt, List<BattleEvent> events)
    {
        if (dealt <= 0 || user.Mon.Fainted)
            return;
        var item = ItemOf(user);
        if (item == HeldItems.ShellBell && user.Mon.Hp < user.Mon.MaxHp)
            Heal(user, Math.Max(1, dealt / 8), events, $"{Label(user)} restored a little HP using its {ItemName(user)}!");
        if (item == HeldItems.LifeOrb && !Has(user, Abilities.MagicGuard) && !(Has(user, Abilities.SheerForce) && HasSecondary(move)))
            Damage(user, Math.Max(1, user.Mon.MaxHp / 10), events, $"{Label(user)} lost some of its HP!");
        HpBerry(user, events);
    }

    /// <summary>Leppa Berry: a move that just ran out of PP gets 10 back.</summary>
    void LeppaBerry(Battler user, int slot, List<BattleEvent> events)
    {
        if (slot < 0 || user.Mon.Moves[slot].Pp > 0 || ItemOf(user) != HeldItems.LeppaBerry || !CanEatBerry(user))
            return;
        var pp = user.Mon.Moves[slot];
        pp.Pp = Math.Min(pp.MaxPp, 10);
        events.Add(new MessageEvent($"{Label(user)} restored {pp.Move.Name}'s PP using its {ItemName(user)}!"));
        Consume(user);
    }

    /// <summary>White Herb puts lowered stats back once.</summary>
    void WhiteHerb(Battler b, List<BattleEvent> events)
    {
        if (ItemOf(b) != HeldItems.WhiteHerb || !b.Stages.Any(s => s < 0))
            return;
        for (var i = 0; i < b.Stages.Length; i++)
            b.Stages[i] = Math.Max(0, b.Stages[i]);
        events.Add(new MessageEvent($"{Label(b)} returned its stats to normal using its {ItemName(b)}!"));
        Consume(b);
    }

    /// <summary>End of turn: Leftovers, Black Sludge, Toxic and Flame Orb, Harvest.</summary>
    void ItemEndOfTurn(Battler b, List<BattleEvent> events)
    {
        var item = ItemOf(b);
        var name = Label(b);
        if (item == HeldItems.Leftovers || (item == HeldItems.BlackSludge && b.Types.Contains(TypeChart.Poison)))
        {
            if (b.Mon.Hp < b.Mon.MaxHp)
                Heal(b, Math.Max(1, b.Mon.MaxHp / 16), events, $"{name} restored a little HP using its {ItemName(b)}!");
        }
        else if (item == HeldItems.BlackSludge && !Has(b, Abilities.MagicGuard))
        {
            Damage(b, Math.Max(1, b.Mon.MaxHp / 8), events, $"{name} is hurt by its {ItemName(b)}!");
            CheckFaints(events);
        }
        else if (item is HeldItems.ToxicOrb or HeldItems.FlameOrb && b.Mon.Status == Ailment.None)
            Inflict(b, item == HeldItems.ToxicOrb ? Ailment.Poison : Ailment.Burn, null, events, loud: false);

        // Harvest regrows the berry it ate: half the time, always in sun.
        if (Has(b, Abilities.Harvest) && b.Mon.HeldItem == 0 && b.LastBerry != 0 && (ActiveWeather == Weather.Sun || Roll(50)))
        {
            b.Mon.HeldItem = b.LastBerry;
            b.LastBerry = 0;
            events.Add(Popup(b));
            events.Add(new MessageEvent($"{name} harvested one {ItemName(b)}!"));
        }
    }

    /// <summary>Entry: Air Balloon floats its holder, Frisk finds the foe's item, Unnerve unsettles the foe.</summary>
    void ItemEntry(Battler b, List<BattleEvent> events)
    {
        var other = Opponent(b);
        if (ItemOf(b) == HeldItems.AirBalloon)
            events.Add(new MessageEvent($"{Label(b)} floats in the air with its {ItemName(b)}!"));
        if (Has(b, Abilities.Frisk) && other.Mon.HeldItem != 0)
        {
            events.Add(Popup(b));
            events.Add(new MessageEvent($"{Label(b)} frisked {Label(other)} and found its {ItemName(other)}!"));
        }
        if (Has(b, Abilities.Unnerve))
        {
            events.Add(Popup(b));
            events.Add(new MessageEvent(other.Side == BattleSide.Player ? "Your team is too nervous to eat Berries!"
                : Trainer is null ? $"{Label(other)} is too nervous to eat Berries!" : "The foe's team is too nervous to eat Berries!"));
        }
    }

    /// <summary>The Choice item user's locked move, or null when it may pick freely.</summary>
    static int? ChoiceLock(Battler b) =>
        ItemOf(b) is HeldItems.ChoiceBand or HeldItems.ChoiceSpecs or HeldItems.ChoiceScarf && b.ChoiceMove != 0
        && b.Mon.Moves.Any(m => m.Move.Id == b.ChoiceMove && m.Pp > 0)
            ? b.ChoiceMove : null;
}
