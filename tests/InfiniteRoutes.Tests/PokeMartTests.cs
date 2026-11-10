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
        Assert.Equal([ItemEffect.PokeBall, ItemEffect.Potion], PokeMart.StandardStock(0));   // as in Black/White
        Assert.Equal([2, 11, 14, 17, 18, 19], new[] { 0, 1, 3, 5, 7, 8 }.Select(b => PokeMart.StandardStock(b).Count));
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

    [Fact]
    public void A_repel_lasts_its_steps_and_is_saved()
    {
        var state = new GameState(new QuestLog([]));
        state.Bag.Add(ItemEffect.Repel, 2);
        Assert.True(state.UseRepel(ItemEffect.Repel, "Repel").Ok);
        Assert.False(state.UseRepel(ItemEffect.Repel, "Repel").Ok);   // one at a time
        Assert.Equal(1, state.Bag.Count(ItemEffect.Repel));
        for (var step = 0; step < 99; step++)
            Assert.False(state.RepelStep());
        state.LastOutdoor = new PlayerLocation(389, 782, 749, 0);
        var loaded = SaveGame.FromJson(SaveGame.From(state, "IRAO", DateTime.Now).ToJson())!.Restore(Data(), new QuestLog([]));
        Assert.Equal(1, loaded.RepelSteps);
        Assert.Equal(782, loaded.LastOutdoor?.X);
        Assert.True(state.RepelStep());   // the 100th step wears it off
        Assert.Equal(0, state.RepelSteps);
    }
}
