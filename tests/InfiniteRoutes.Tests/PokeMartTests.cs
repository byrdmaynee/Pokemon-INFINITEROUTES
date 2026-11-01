using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Player;

namespace InfiniteRoutes.Tests;

public class PokeMartTests
{
    static BattleData Data()
    {
        var names = Enumerable.Range(0, 30).Select(i => $"Item{i}").ToList();
        var prices = new int[30];
        prices[ItemEffect.PokeBall] = 200;
        prices[ItemEffect.Potion] = 300;
        return new BattleData([], [], [], names, prices);
    }

    [Fact]
    public void Shelves_grow_with_badges()
    {
        Assert.DoesNotContain(ItemEffect.GreatBall, PokeMart.StandardStock(0));
        Assert.Contains(ItemEffect.GreatBall, PokeMart.StandardStock(1));
        Assert.Contains(ItemEffect.FullRestore, PokeMart.StandardStock(8));
        Assert.Equal([ItemEffect.HealBall], PokeMart.SpecialtyStock("Accumula Town"));
        Assert.NotEmpty(PokeMart.SpecialtyStock("Somewhere new"));
    }

    [Fact]
    public void Buying_and_selling_move_money_and_items()
    {
        var data = Data();
        var state = new GameState(new QuestLog([]));
        state.Progress.Money = 2500;

        Assert.True(PokeMart.Buy(state, data, ItemEffect.PokeBall, 10).Ok);
        Assert.Equal(500, state.Progress.Money);
        Assert.Equal(10, state.Bag.Count(ItemEffect.PokeBall));
        Assert.Equal(1, state.Bag.Count(ItemEffect.PremierBall));   // the bonus

        Assert.False(PokeMart.Buy(state, data, ItemEffect.Potion, 2).Ok);   // ₽600 > ₽500
        Assert.False(PokeMart.Buy(state, data, ItemEffect.MasterBall, 1).Ok);   // not for sale
        Assert.Equal(500, state.Progress.Money);

        Assert.True(PokeMart.Sell(state, data, ItemEffect.PokeBall, 99).Ok);   // capped at what you have
        Assert.Equal(1500, state.Progress.Money);
        Assert.Equal(0, state.Bag.Count(ItemEffect.PokeBall));
        Assert.False(PokeMart.Sell(state, data, ItemEffect.PremierBall, 1).Ok);   // worth nothing
    }
}
