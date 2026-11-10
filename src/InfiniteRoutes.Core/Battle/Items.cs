using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Core.Battle;

/// <summary>What an item does in battle (and from the party menu). Item ids are the games' own (1 Master Ball, 17 Potion, …).</summary>
public abstract record ItemEffect
{
    /// <summary>A Poké Ball; <see cref="Multiplier"/> is its catch-rate bonus (Master Ball: always catches).</summary>
    public sealed record Ball(double Multiplier, bool AlwaysCatches = false) : ItemEffect;

    /// <summary>Restores <see cref="Hp"/> HP (int.MaxValue = all) and optionally cures every status.</summary>
    public sealed record Heal(int Hp, bool CuresStatus = false) : ItemEffect;

    /// <summary>Cures one status (or all, with confusion, when <see cref="Status"/> is <see cref="Ailment.None"/>).</summary>
    public sealed record Cure(Ailment Status) : ItemEffect;

    /// <summary>Revives a fainted Pokémon with <see cref="Fraction"/> of its max HP.</summary>
    public sealed record Revive(double Fraction) : ItemEffect;

    public const int MasterBall = 1, UltraBall = 2, GreatBall = 3, PokeBall = 4, SafariBall = 5, NetBall = 6, DiveBall = 7,
        NestBall = 8, RepeatBall = 9, TimerBall = 10, LuxuryBall = 11, PremierBall = 12, DuskBall = 13, HealBall = 14,
        QuickBall = 15, CherishBall = 16;
    public const int Potion = 17, Antidote = 18, BurnHeal = 19, IceHeal = 20, Awakening = 21, ParlyzHeal = 22, FullRestore = 23,
        MaxPotion = 24, HyperPotion = 25, SuperPotion = 26, FullHeal = 27, ReviveItem = 28, MaxRevive = 29;
    public const int SuperRepel = 76, MaxRepel = 77, EscapeRope = 78, Repel = 79;

    /// <summary>Steps a repel keeps weaker wild Pokémon away; 0 for other items.</summary>
    public static int RepelSteps(int item) => item switch { Repel => 100, SuperRepel => 200, MaxRepel => 250, _ => 0 };

    /// <summary>The effect of a known item, or null for items the battle engine can't use yet.</summary>
    public static ItemEffect? For(int item) => item switch
    {
        MasterBall => new Ball(255, AlwaysCatches: true),
        UltraBall => new Ball(2),
        GreatBall or SafariBall => new Ball(1.5),
        >= PokeBall and <= CherishBall => new Ball(1),   // situational balls use their bonus via BallMultiplier
        Potion => new Heal(20),
        SuperPotion => new Heal(50),
        HyperPotion => new Heal(200),
        MaxPotion => new Heal(int.MaxValue),
        FullRestore => new Heal(int.MaxValue, CuresStatus: true),
        Antidote => new Cure(Ailment.Poison),
        BurnHeal => new Cure(Ailment.Burn),
        IceHeal => new Cure(Ailment.Freeze),
        Awakening => new Cure(Ailment.Sleep),
        ParlyzHeal => new Cure(Ailment.Paralysis),
        FullHeal => new Cure(Ailment.None),
        ReviveItem => new Revive(0.5),
        MaxRevive => new Revive(1),
        _ => null,
    };
}

/// <summary>The player's items: item id → count, in the order they were first added.</summary>
public sealed class Bag
{
    readonly List<int> _order = new();
    readonly Dictionary<int, int> _counts = new();

    public int Count(int item) => _counts.GetValueOrDefault(item);

    public void Add(int item, int count = 1)
    {
        if (count <= 0)
            return;
        if (!_counts.ContainsKey(item))
            _order.Add(item);
        _counts[item] = Count(item) + count;
    }

    /// <summary>Removes one; false when there is none.</summary>
    public bool Take(int item)
    {
        if (Count(item) <= 0)
            return false;
        if (--_counts[item] == 0)
        {
            _counts.Remove(item);
            _order.Remove(item);
        }
        return true;
    }

    /// <summary>Items held, in pocket order.</summary>
    public IEnumerable<(int Item, int Count)> Items => _order.Select(i => (i, _counts[i]));

    public IEnumerable<(int Item, int Count)> Balls => Items.Where(i => ItemEffect.For(i.Item) is ItemEffect.Ball);

    public IEnumerable<(int Item, int Count)> Medicine => Items.Where(i => ItemEffect.For(i.Item) is ItemEffect.Heal or ItemEffect.Cure or ItemEffect.Revive);
}

public static class ItemUse
{
    /// <summary>
    /// Uses a medicine on <paramref name="mon"/>. Returns the message to show, or null when it would have no effect
    /// (the item is then not used up).
    /// </summary>
    public static string? Apply(ItemEffect effect, Pokemon mon)
    {
        switch (effect)
        {
            case ItemEffect.Revive revive:
                if (!mon.Fainted)
                    return null;
                mon.Hp = Math.Max(1, (int)(mon.MaxHp * revive.Fraction));
                mon.Status = Ailment.None;
                return $"{mon.Name} was revived!";
            case ItemEffect.Heal heal:
                if (mon.Fainted || (mon.Hp >= mon.MaxHp && !(heal.CuresStatus && mon.Status != Ailment.None)))
                    return null;
                var before = mon.Hp;
                mon.Hp = (int)Math.Min(mon.MaxHp, (long)mon.Hp + heal.Hp);
                if (heal.CuresStatus)
                    mon.Status = Ailment.None;
                return mon.Hp > before ? $"{mon.Name}'s HP was restored by {mon.Hp - before} point(s)." : $"{mon.Name} was cured.";
            case ItemEffect.Cure cure:
                if (mon.Fainted || mon.Status == Ailment.None || (cure.Status != Ailment.None && mon.Status != cure.Status))
                    return null;
                var message = mon.Status switch
                {
                    Ailment.Poison => $"{mon.Name} was cured of its poisoning.",
                    Ailment.Burn => $"{mon.Name}'s burn was healed.",
                    Ailment.Freeze => $"{mon.Name} was thawed out.",
                    Ailment.Sleep => $"{mon.Name} woke up.",
                    _ => $"{mon.Name} was cured of paralysis.",
                };
                mon.Status = Ailment.None;
                mon.SleepTurns = 0;
                return message;
            default:
                return null;
        }
    }
}
