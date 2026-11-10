using InfiniteRoutes.Core.Battle;

namespace InfiniteRoutes.Core.Player;

/// <summary>
/// The Poké Mart counter in every Pokémon Center: a standard shelf that grows with the player's badges and a town
/// specialty shelf. Prices come from the ROM's item data; items sell back for half. The standard shelf is Black/White's
/// own (the lists in overlay 21: 2, 11, 14, 17, 18 and 19 items at 0, 1, 3, 5, 7 and 8 badges); the town specialty
/// shelves are our own pick for now.
/// </summary>
public static class PokeMart
{
    public const int MaxPerPurchase = 99;

    /// <summary>Standard shelf, in the games' order: (badges needed, item).</summary>
    static readonly (int Badges, int Item)[] Standard =
    [
        (0, ItemEffect.PokeBall), (1, ItemEffect.GreatBall), (5, ItemEffect.UltraBall),
        (0, ItemEffect.Potion), (1, ItemEffect.SuperPotion), (3, ItemEffect.HyperPotion), (7, ItemEffect.MaxPotion), (8, ItemEffect.FullRestore),
        (3, ItemEffect.ReviveItem),
        (1, ItemEffect.Antidote), (1, ItemEffect.ParlyzHeal), (1, ItemEffect.Awakening), (1, ItemEffect.BurnHeal), (1, ItemEffect.IceHeal),
        (5, ItemEffect.FullHeal),
        (1, ItemEffect.EscapeRope), (1, ItemEffect.Repel), (3, ItemEffect.SuperRepel), (5, ItemEffect.MaxRepel),
    ];

    /// <summary>Town specialty shelves, by location name.</summary>
    static readonly Dictionary<string, int[]> Specialty = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Accumula Town"] = [ItemEffect.HealBall],
        ["Striaton City"] = [ItemEffect.NetBall, ItemEffect.NestBall],
        ["Nacrene City"] = [ItemEffect.NestBall, ItemEffect.DuskBall],
        ["Castelia City"] = [ItemEffect.QuickBall, ItemEffect.TimerBall, ItemEffect.LuxuryBall],
        ["Nimbasa City"] = [ItemEffect.QuickBall, ItemEffect.RepeatBall],
        ["Driftveil City"] = [ItemEffect.DiveBall, ItemEffect.NetBall],
        ["Mistralton City"] = [ItemEffect.TimerBall, ItemEffect.RepeatBall],
        ["Icirrus City"] = [ItemEffect.DuskBall, ItemEffect.NetBall],
        ["Opelucid City"] = [ItemEffect.LuxuryBall, ItemEffect.TimerBall, ItemEffect.QuickBall],
    };

    static readonly int[] OtherTowns = [ItemEffect.DuskBall, ItemEffect.QuickBall, ItemEffect.TimerBall];

    public static IReadOnlyList<int> StandardStock(int badges) =>
        Standard.Where(s => badges >= s.Badges).Select(s => s.Item).ToList();

    public static IReadOnlyList<int> SpecialtyStock(string town) =>
        Specialty.TryGetValue(town, out var items) ? items : OtherTowns;

    public static int SellPrice(BattleData data, int item) => data.ItemPrice(item) / 2;

    /// <summary>
    /// Buys <paramref name="count"/> of an item from the shelf. As in the games, every ten Poké Balls bought at once
    /// come with a free Premier Ball.
    /// </summary>
    public static (bool Ok, string Message) Buy(GameState state, BattleData data, int item, int count)
    {
        var price = data.ItemPrice(item);
        if (price <= 0 || count <= 0 || count > MaxPerPurchase)
            return (false, "That can't be bought here.");
        var total = price * count;
        if (state.Progress.Money < total)
            return (false, $"You don't have enough money. {count} × {data.ItemName(item)} costs ₽{total:N0}.");
        state.Progress.Money -= total;
        state.Bag.Add(item, count);
        var message = $"You bought {count} × {data.ItemName(item)} for ₽{total:N0}.";
        if (item == ItemEffect.PokeBall && count >= 10)
        {
            state.Bag.Add(ItemEffect.PremierBall, count / 10);
            message += $" You also got {(count / 10 == 1 ? "a Premier Ball" : $"{count / 10} Premier Balls")} as a bonus!";
        }
        return (true, message);
    }

    /// <summary>Sells <paramref name="count"/> of an item from the Bag for half its price.</summary>
    public static (bool Ok, string Message) Sell(GameState state, BattleData data, int item, int count)
    {
        var each = SellPrice(data, item);
        if (each <= 0)
            return (false, $"The {data.ItemName(item)} can't be sold.");
        count = Math.Min(count, state.Bag.Count(item));
        if (count <= 0)
            return (false, $"You don't have any {data.ItemName(item)}.");
        for (var i = 0; i < count; i++)
            state.Bag.Take(item);
        state.Progress.Money += each * count;
        return (true, $"You sold {count} × {data.ItemName(item)} for ₽{each * count:N0}.");
    }
}
