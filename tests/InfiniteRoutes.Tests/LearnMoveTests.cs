using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Tests;

public class LearnMoveTests
{
    static MoveInfo Move(int id, int pp) =>
        new(id, $"Move{id}", 0, MoveKind.Damage, MoveCategory.Physical, 40, 100, pp, 0, 1, 1, Ailment.None, 0, 0, 0, 0, 0, 0, 0, [], 0);

    static BattleData Data()
    {
        var species = new SpeciesInfo(1, "Mon", new BaseStats(45, 49, 49, 45, 65, 65), ["12"], 45, new BaseStats(0, 0, 0, 0, 0, 0),
            127, 20, 70, 3, [1], ["Overgrow"], null, 64, 6, 81, 1, [12]);
        IReadOnlyList<LevelMove> learnset = [new(1, 1), new(1, 2), new(1, 3), new(1, 4), new(6, 5)];
        return new BattleData([species], Enumerable.Range(1, 5).Select(i => Move(i, 10 + i)).ToList(), [[], learnset]);
    }

    [Fact]
    public void A_fifth_move_waits_for_the_player_to_pick_one_to_forget()
    {
        var data = Data();
        var mon = Pokemon.Create(data, 1, 5, new Random(1));
        Assert.Equal([1, 2, 3, 4], mon.Moves.Select(m => m.Move.Id));

        var levels = mon.GainExp(ExpCurve.ForLevel(mon.Species.GrowthRate, 6) - mon.Exp, data);
        var skipped = Assert.Single(Assert.Single(levels).Skipped);
        Assert.Equal(5, skipped.Id);
        Assert.Equal(4, mon.Moves.Count);   // nothing replaced on its own

        mon.Moves[1].Pp = 0;
        Assert.Equal(2, mon.LearnMove(skipped, 1)?.Id);
        Assert.Equal([1, 5, 3, 4], mon.Moves.Select(m => m.Move.Id));
        Assert.Equal(15, mon.Moves[1].Pp);   // full PP for the new move
        Assert.Null(mon.LearnMove(skipped, 0));   // already known
        Assert.Null(mon.LearnMove(data.Move(2)!, 7));   // no such slot
    }
}
