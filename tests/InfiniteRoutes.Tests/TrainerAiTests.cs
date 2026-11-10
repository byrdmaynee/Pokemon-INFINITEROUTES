using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Tests;

public class TrainerAiTests
{
    static readonly BaseStats NoEvs = new(0, 0, 0, 0, 0, 0);

    static SpeciesInfo Species(int id, int type) =>
        new(id, $"Mon{id}", new BaseStats(80, 60, 60, 60, 60, 60), [type.ToString()], 45, NoEvs, 127, 20, 70, 3, [1],
            ["None"], null, 100, 6, 200, 1, [type], [0]);

    static MoveInfo Move(int id, int type, MoveCategory category, int power, MoveKind kind = MoveKind.Damage, Ailment ailment = Ailment.None) =>
        new(id, $"Move{id}", type, kind, category, power, 101, 35, 0, 1, 1, ailment, 0, 0, 0, 0, 0, 0, 0, [], 0);

    static readonly MoveInfo Tackle = Move(33, TypeChart.Normal, MoveCategory.Physical, 40);
    static readonly MoveInfo Earthquake = Move(89, TypeChart.Ground, MoveCategory.Physical, 100);
    static readonly MoveInfo ThunderWave = Move(86, TypeChart.Electric, MoveCategory.Status, 0, MoveKind.Ailment, Ailment.Paralysis);
    static readonly MoveInfo Splash = Move(150, TypeChart.Normal, MoveCategory.Status, 0);

    static (SingleBattle Battle, Pokemon Mine, Pokemon Foe) Setup(int ai, MoveInfo[] foeMoves, int myType, int[] items, int seed)
    {
        var data = new BattleData([Species(1, myType), Species(2, TypeChart.Normal)], [Tackle, Earthquake, ThunderWave, Splash], [[], [], []],
            Enumerable.Range(0, 30).Select(i => $"Item{i}").ToList());
        var trainer = new TrainerInfo(1, "Ann", 1, "Ace Trainer", 0, items, ai, 10, [new TrainerPokemon(2, 0, 40, 255, 0, 0, foeMoves.Select(m => m.Id).ToList())]);
        var foe = SingleBattle.TrainerParty(data, trainer, new Random(seed))[0];
        var mine = Pokemon.Create(data, 1, 40, new Random(seed + 1));
        mine.Moves.Add(new MoveSlot(Splash));
        mine.Hp = 10_000;
        return (new SingleBattle(data, [mine], new Bag(), [foe], trainer, new Random(seed + 2)), mine, foe);
    }

    [Fact]
    public void Smart_trainers_never_use_moves_that_cannot_work()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            // Thunder Wave can't paralyze a Ground type; Earthquake does nothing to a Flying type.
            var (battle, _, _) = Setup(SingleBattle.AiBasic, [ThunderWave, Tackle], TypeChart.Ground, [], seed);
            Assert.DoesNotContain(battle.Fight(0), e => e is MessageEvent { Text: var t } && t.Contains("Move86"));
            (battle, _, _) = Setup(SingleBattle.AiBasic | SingleBattle.AiEvaluate, [Earthquake, Tackle], TypeChart.Flying, [], seed);
            Assert.DoesNotContain(battle.Fight(0), e => e is MessageEvent { Text: var t } && t.Contains("Move89"));
        }
    }

    [Fact]
    public void Evaluating_trainers_pick_the_strongest_attack()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var (battle, _, _) = Setup(SingleBattle.AiBasic | SingleBattle.AiEvaluate, [Tackle, Earthquake], TypeChart.Normal, [], seed);
            Assert.Contains(battle.Fight(0), e => e is MessageEvent { Text: var t } && t.Contains("used Move89"));
        }
    }

    [Fact]
    public void Trainers_use_their_potions_when_low()
    {
        var (battle, _, foe) = Setup(7, [Tackle], TypeChart.Normal, [ItemEffect.HyperPotion], 1);
        foe.Hp = 5;
        var events = battle.Fight(0);
        Assert.Contains(events, e => e is MessageEvent { Text: var t } && t.Contains("used a Item25"));
        Assert.True(foe.Hp > 5);
        foe.Hp = 5;
        Assert.DoesNotContain(battle.Fight(0), e => e is MessageEvent { Text: var t } && t.Contains("used a Item25"));   // only one
    }
}
