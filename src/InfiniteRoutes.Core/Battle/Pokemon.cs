using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Core.Battle;

/// <summary>The ROM data a battle needs: species, moves, learnsets and item names, all indexed by their ids.</summary>
public sealed class BattleData(IReadOnlyList<SpeciesInfo> species, IReadOnlyList<MoveInfo> moves, IReadOnlyList<IReadOnlyList<LevelMove>> learnsets,
    IReadOnlyList<string>? itemNames = null)
{
    public string ItemName(int id) => itemNames is not null && id >= 0 && id < itemNames.Count ? itemNames[id] : $"Item {id}";

    readonly Dictionary<int, SpeciesInfo> _species = species.ToDictionary(s => s.Id);
    readonly Dictionary<int, MoveInfo> _moves = moves.ToDictionary(m => m.Id);

    public SpeciesInfo Species(int id) => _species.TryGetValue(id, out var s) ? s : throw new KeyNotFoundException($"No species {id}.");

    public MoveInfo? Move(int id) => _moves.GetValueOrDefault(id);

    public IReadOnlyList<LevelMove> Learnset(int species) => species >= 0 && species < learnsets.Count ? learnsets[species] : [];

    /// <summary>Struggle: used when no move has PP left.</summary>
    public const int Struggle = 165;
}

public enum Gender { Male, Female, Genderless }

/// <summary>
/// The 25 natures in the games' order. Nature n raises stat n / 5 and lowers stat n % 5 of
/// (Attack, Defense, Speed, Sp. Atk, Sp. Def); when both are the same it is neutral.
/// </summary>
public enum Nature
{
    Hardy, Lonely, Brave, Adamant, Naughty, Bold, Docile, Relaxed, Impish, Lax, Timid, Hasty, Serious, Jolly, Naive,
    Modest, Mild, Quiet, Bashful, Rash, Calm, Gentle, Sassy, Careful, Quirky,
}

public sealed class MoveSlot(MoveInfo move)
{
    public MoveInfo Move { get; } = move;
    public int Pp { get; set; } = move.Pp;
    public int MaxPp => Move.Pp;
}

/// <summary>A Pokémon's stats in HP, Attack, Defense, Sp. Atk, Sp. Def, Speed order (as <see cref="Stat"/>).</summary>
public enum Stat { Hp, Attack, Defense, SpAttack, SpDefense, Speed }

/// <summary>One Pokémon: species, level, individual values, nature, moves and current HP and status.</summary>
public sealed class Pokemon
{
    public SpeciesInfo Species { get; private set; }
    public int Level { get; private set; }
    public int Exp { get; private set; }
    public Nature Nature { get; }
    public Gender Gender { get; }
    public bool Shiny { get; }
    public int[] Ivs { get; }
    public int[] Evs { get; } = new int[6];
    public List<MoveSlot> Moves { get; } = new();
    public int Hp { get; set; }
    public Ailment Status { get; set; }
    /// <summary>Turns left asleep while <see cref="Status"/> is <see cref="Ailment.Sleep"/>.</summary>
    public int SleepTurns { get; set; }

    public int MaxHp => StatValue(Stat.Hp);
    public bool Fainted => Hp <= 0;
    public string Name => Species.Name;
    public IReadOnlyList<int> Types => Species.TypeIds ?? [0];

    Pokemon(SpeciesInfo species, int level, Nature nature, Gender gender, bool shiny, int[] ivs)
    {
        Species = species;
        Level = level;
        Nature = nature;
        Gender = gender;
        Shiny = shiny;
        Ivs = ivs;
        Exp = ExpCurve.ForLevel(species.GrowthRate, level);
        Hp = MaxHp;
    }

    /// <summary>A wild-style Pokémon: random IVs, nature and gender, knowing the last four moves it learned by <paramref name="level"/>.</summary>
    public static Pokemon Create(BattleData data, int speciesId, int level, Random rng)
    {
        var species = data.Species(speciesId);
        var ivs = Enumerable.Range(0, 6).Select(_ => rng.Next(32)).ToArray();
        var gender = species.GenderRatio switch
        {
            255 => Gender.Genderless,
            254 => Gender.Female,
            0 => Gender.Male,
            var ratio => rng.Next(256) < ratio ? Gender.Female : Gender.Male,
        };
        var mon = new Pokemon(species, Math.Clamp(level, 1, 100), (Nature)rng.Next(25), gender, rng.Next(8192) == 0, ivs);
        foreach (var lm in data.Learnset(speciesId).Where(m => m.Level <= mon.Level).Reverse().DistinctBy(m => m.Move).Take(4).Reverse())
            if (data.Move(lm.Move) is { } move)
                mon.Moves.Add(new MoveSlot(move));
        return mon;
    }

    public int StatValue(Stat stat)
    {
        var b = stat switch
        {
            Stat.Hp => Species.Stats.Hp,
            Stat.Attack => Species.Stats.Attack,
            Stat.Defense => Species.Stats.Defense,
            Stat.SpAttack => Species.Stats.SpAttack,
            Stat.SpDefense => Species.Stats.SpDefense,
            _ => Species.Stats.Speed,
        };
        var i = (int)stat;
        var core = (2 * b + Ivs[i] + Evs[i] / 4) * Level / 100;
        if (stat == Stat.Hp)
            return Species.Stats.Hp == 1 ? 1 : core + Level + 10;   // Shedinja
        return (core + 5) * NatureTenths(stat) / 10;
    }

    /// <summary>11 when the nature raises <paramref name="stat"/>, 9 when it lowers it, else 10.</summary>
    int NatureTenths(Stat stat)
    {
        int index = stat switch { Stat.Attack => 0, Stat.Defense => 1, Stat.Speed => 2, Stat.SpAttack => 3, Stat.SpDefense => 4, _ => -1 };
        int up = (int)Nature / 5, down = (int)Nature % 5;
        return up == down || index < 0 ? 10 : index == up ? 11 : index == down ? 9 : 10;
    }

    public void HealFully()
    {
        Hp = MaxHp;
        Status = Ailment.None;
        SleepTurns = 0;
        foreach (var slot in Moves)
            slot.Pp = slot.MaxPp;
    }

    /// <summary>Adds experience; returns the levels gained (each with the moves learned there, already added if a slot was free).</summary>
    public List<(int Level, List<MoveInfo> Learned, List<MoveInfo> Skipped)> GainExp(int amount, BattleData data)
    {
        var gained = new List<(int, List<MoveInfo>, List<MoveInfo>)>();
        Exp += amount;
        while (Level < 100 && Exp >= ExpCurve.ForLevel(Species.GrowthRate, Level + 1))
        {
            var oldMax = MaxHp;
            Level++;
            if (!Fainted)
                Hp = Math.Min(MaxHp, Hp + MaxHp - oldMax);
            List<MoveInfo> learned = [], skipped = [];
            foreach (var lm in data.Learnset(Species.Id).Where(m => m.Level == Level))
            {
                if (data.Move(lm.Move) is not { } move || Moves.Any(s => s.Move.Id == move.Id))
                    continue;
                if (Moves.Count < 4)
                {
                    Moves.Add(new MoveSlot(move));
                    learned.Add(move);
                }
                else
                    skipped.Add(move);
            }
            gained.Add((Level, learned, skipped));
        }
        if (Level == 100)
            Exp = Math.Min(Exp, ExpCurve.ForLevel(Species.GrowthRate, 100));
        return gained;
    }
}

/// <summary>Total experience needed to reach a level, for the six growth rates (personal byte 0x15).</summary>
public static class ExpCurve
{
    public static int ForLevel(int growthRate, int level)
    {
        if (level <= 1)
            return 0;
        long n = level, n3 = n * n * n;
        return (int)(growthRate switch
        {
            1 => n <= 50 ? n3 * (100 - n) / 50 : n <= 68 ? n3 * (150 - n) / 100 : n <= 98 ? n3 * ((1911 - 10 * n) / 3) / 500 : n3 * (160 - n) / 100,
            2 => n <= 15 ? n3 * ((n + 1) / 3 + 24) / 50 : n <= 36 ? n3 * (n + 14) / 50 : n3 * (n / 2 + 32) / 50,
            3 => 6 * n3 / 5 - 15 * n * n + 100 * n - 140,
            4 => 4 * n3 / 5,
            5 => 5 * n3 / 4,
            _ => n3,
        });
    }
}
