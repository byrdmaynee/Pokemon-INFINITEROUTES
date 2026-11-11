using InfiniteRoutes.Core.Battle;

namespace InfiniteRoutes.Server.Admin;

/// <summary>
/// Whether a Pokémon could have been obtained normally (BUILD_SPEC §11.3's legality indicator). Staff below the
/// <see cref="Permissions.PokemonIllegal"/> permission can't save one that fails; those with it can, with a reason,
/// and it's audited. What's checked: the species exists; level 1–100; IVs 0–31; EVs at most 255 each and 510 in
/// all; one to four different known moves, each learnable by level-up at or below its level (TMs, eggs and tutors
/// aren't known yet, so those moves are reported); no hidden ability (none are obtainable here yet); a real held item;
/// a nickname of at most 10 characters.
/// </summary>
public static class Legality
{
    public static List<string> Check(PokemonSave p, BattleData data)
    {
        var issues = new List<string>();
        if (!data.HasSpecies(p.Species))
        {
            issues.Add($"There's no species #{p.Species}.");
            return issues;
        }
        var species = data.Species(p.Species);
        if (p.Level is < 1 or > 100)
            issues.Add("The level must be from 1 to 100.");
        if (p.Ivs.Length != 6 || p.Ivs.Any(v => v is < 0 or > 31))
            issues.Add("IVs must be from 0 to 31.");
        if (p.Evs.Length != 6 || p.Evs.Any(v => v is < 0 or > 255))
            issues.Add("EVs must be from 0 to 255 each.");
        else if (p.Evs.Sum() > 510)
            issues.Add($"EVs add up to {p.Evs.Sum()}; the most is 510.");
        if (p.Moves.Count is < 1 or > 4)
            issues.Add("A Pokémon knows one to four moves.");
        if (p.Moves.Select(m => m.Move).Distinct().Count() != p.Moves.Count)
            issues.Add("The same move is in two slots.");
        var learnable = data.Learnset(p.Species).Where(l => l.Level <= p.Level).Select(l => l.Move).ToHashSet();
        foreach (var m in p.Moves)
        {
            if (data.Move(m.Move) is not { } move)
                issues.Add($"There's no move #{m.Move}.");
            else if (!learnable.Contains(m.Move))
                issues.Add($"{species.Name} doesn't learn {move.Name} by level {p.Level} (TMs, eggs and tutors aren't checked yet).");
        }
        if (p.AbilitySlot == 2)
            issues.Add("Hidden abilities can't be obtained here yet.");
        if (p.HeldItem < 0 || p.HeldItem > 0 && data.ItemName(p.HeldItem).StartsWith("Item ", StringComparison.Ordinal))
            issues.Add($"There's no item #{p.HeldItem}.");
        if ((p.Nickname?.Length ?? 0) > Pokemon.MaxNicknameLength)
            issues.Add($"Nicknames are at most {Pokemon.MaxNicknameLength} characters.");
        return issues;
    }
}
