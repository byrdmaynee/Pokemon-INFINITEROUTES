using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Core.Battle;

/// <summary>The ROM data a battle needs: species, moves, learnsets and item names and prices, all indexed by their ids.</summary>
public sealed class BattleData(IReadOnlyList<SpeciesInfo> species, IReadOnlyList<MoveInfo> moves, IReadOnlyList<IReadOnlyList<LevelMove>> learnsets,
    IReadOnlyList<string>? itemNames = null, IReadOnlyList<int>? itemPrices = null)
{
    public string ItemName(int id) => itemNames is not null && id >= 0 && id < itemNames.Count ? itemNames[id] : $"Item {id}";

    /// <summary>The item's shop price in ₽; 0 when it isn't sold.</summary>
    public int ItemPrice(int id) => itemPrices is not null && id >= 0 && id < itemPrices.Count ? itemPrices[id] : 0;

    readonly Dictionary<int, SpeciesInfo> _species = species.ToDictionary(s => s.Id);
    readonly Dictionary<int, MoveInfo> _moves = moves.ToDictionary(m => m.Id);

    public bool HasSpecies(int id) => _species.ContainsKey(id);

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

    /// <summary>The item it holds (see <see cref="HeldItems"/>); 0 for none.</summary>
    public int HeldItem { get; set; }

    /// <summary>Which of the species' abilities this Pokémon has: 0 or 1, or 2 for its hidden ability.</summary>
    public int AbilitySlot { get; private set; }

    /// <summary>The ability's id (see <see cref="Abilities"/>); 0 when the species data has none.</summary>
    public int AbilityId => AbilitySlot == 2 && Species.HiddenAbilityId != 0 ? Species.HiddenAbilityId
        : Species.AbilityIds is { Count: > 0 } ids ? ids[Math.Min(AbilitySlot, ids.Count - 1)] : 0;

    public string AbilityName => AbilitySlot == 2 && Species.HiddenAbility is { } hidden ? hidden
        : Species.Abilities.Count > 0 ? Species.Abilities[Math.Min(AbilitySlot, Species.Abilities.Count - 1)] : "";

    /// <summary>Sets the ability slot; 2 (hidden) only sticks when the species has a hidden ability.</summary>
    public void SetAbilitySlot(int slot) =>
        AbilitySlot = slot == 2 && Species.HiddenAbilityId != 0 ? 2 : Math.Clamp(slot, 0, Math.Max(0, Species.Abilities.Count - 1));

    public int MaxHp => StatValue(Stat.Hp);
    public bool Fainted => Hp <= 0;
    /// <summary>Its nickname, or the species name.</summary>
    public string Name => Nickname ?? Species.Name;

    /// <summary>The nickname the player gave it; null for none.</summary>
    public string? Nickname { get; private set; }

    /// <summary>The longest nickname, as in Black/White.</summary>
    public const int MaxNicknameLength = 10;

    /// <summary>Gives it a nickname (trimmed, at most <see cref="MaxNicknameLength"/> characters); blank or the species name clears it.</summary>
    public void Rename(string? nickname)
    {
        var name = (nickname ?? "").Trim();
        if (name.Length > MaxNicknameLength)
            name = name[..MaxNicknameLength];
        Nickname = name.Length == 0 || name == Species.Name ? null : name;
    }
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

    /// <summary>
    /// A wild-style Pokémon: random IVs, nature, gender and (one of its two) ability, knowing the last four moves it
    /// learned by <paramref name="level"/>.
    /// </summary>
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
        if (species.Abilities.Count > 1)
            mon.AbilitySlot = rng.Next(2);
        // Wild held items: the common one half the time (always, when both slots name the same item), the rare one 5%.
        if (species.WildItems is [var common, var rare, ..] && (common | rare) != 0)
        {
            var roll = rng.Next(100);
            mon.HeldItem = common != 0 && common == rare ? common : roll < 50 ? common : roll < 55 ? rare : 0;
        }
        return mon;
    }

    /// <summary>Everything needed to rebuild this Pokémon later (saves, mail, market listings).</summary>
    public PokemonSave ToSave() =>
        new(Species.Id, Level, Exp, Nature, Gender, Shiny, [.. Ivs], [.. Evs], Hp, Status, SleepTurns,
            Moves.Select(m => new MoveSave(m.Move.Id, m.Pp)).ToList(), AbilitySlot, HeldItem, Nickname, AdminOrigin);

    /// <summary>
    /// Made or changed by staff (BUILD_SPEC §11.4): kept with the Pokémon for good, so anything an admin put into the
    /// game can always be found. Only staff tools show it.
    /// </summary>
    public bool AdminOrigin { get; set; }

    /// <summary>Rebuilds a saved Pokémon; null when its species isn't in <paramref name="data"/> (a different ROM).</summary>
    public static Pokemon? FromSave(BattleData data, PokemonSave save)
    {
        if (!data.HasSpecies(save.Species) || save.Ivs.Length != 6 || save.Evs.Length != 6)
            return null;
        var species = data.Species(save.Species);
        var level = Math.Clamp(save.Level, 1, 100);
        var mon = new Pokemon(species, level, save.Nature, save.Gender, save.Shiny, [.. save.Ivs])
        {
            Exp = Math.Max(save.Exp, ExpCurve.ForLevel(species.GrowthRate, level)),
            Status = save.Status,
            SleepTurns = save.SleepTurns,
        };
        mon.SetAbilitySlot(save.AbilitySlot);
        mon.HeldItem = Math.Max(0, save.HeldItem);
        mon.Rename(save.Nickname);
        save.Evs.CopyTo(mon.Evs, 0);
        foreach (var m in save.Moves.Take(4))
            if (data.Move(m.Move) is { } move)
                mon.Moves.Add(new MoveSlot(move) { Pp = Math.Clamp(m.Pp, 0, move.Pp) });
        mon.Hp = Math.Clamp(save.Hp, 0, mon.MaxHp);
        mon.AdminOrigin = save.AdminOrigin;
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

    public const int MaxMoves = 4;

    /// <summary>
    /// Learns <paramref name="move"/> in place of the move in slot <paramref name="forget"/>, with full PP. Returns the
    /// forgotten move, or null (nothing changes) when the slot is empty or the move is already known.
    /// </summary>
    public MoveInfo? LearnMove(MoveInfo move, int forget)
    {
        if (forget < 0 || forget >= Moves.Count || Moves.Any(s => s.Move.Id == move.Id))
            return null;
        var old = Moves[forget].Move;
        Moves[forget] = new MoveSlot(move);
        return old;
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
                if (Moves.Count < MaxMoves)
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

public sealed record MoveSave(int Move, int Pp);

public sealed record PokemonSave(int Species, int Level, int Exp, Nature Nature, Gender Gender, bool Shiny, int[] Ivs, int[] Evs,
    int Hp, Ailment Status, int SleepTurns, List<MoveSave> Moves, int AbilitySlot = 0, int HeldItem = 0, string? Nickname = null,
    bool AdminOrigin = false);

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
