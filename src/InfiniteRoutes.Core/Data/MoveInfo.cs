namespace InfiniteRoutes.Core.Data;

public enum MoveCategory { Status, Physical, Special }

/// <summary>What a move does, as the ROM classifies it (move byte 1); decides who its stat changes and ailments hit.</summary>
public enum MoveKind
{
    Damage, Ailment, StatChange, Heal, DamageAilment, Swagger, DamageLowerFoe, DamageRaiseUser, DamageDrain,
    OneHitKo, FieldEffect, SideEffect, ForceSwitch, Unique,
}

/// <summary>Major status conditions plus the volatile ones a move can inflict, numbered as in the ROM's move data.</summary>
public enum Ailment { None, Paralysis, Sleep, Freeze, Burn, Poison, Confusion, Infatuation, Trap, Nightmare, Torment = 12, Disable, Yawn, HealBlock, NoTypeImmunity = 17, LeechSeed, Embargo, PerishSong, Ingrain }

/// <summary>Battle stats a move can raise or lower, numbered as in the ROM (0 none … 7 evasion, 8 all but accuracy/evasion).</summary>
public enum BattleStat { None, Attack, Defense, SpAttack, SpDefense, Speed, Accuracy, Evasion, All }

/// <summary>A stat stage change a move makes, with its chance in percent (0 = always, as part of a status move).</summary>
public sealed record StatChange(BattleStat Stat, int Stages, int Chance);

/// <summary>
/// One move as extracted from the ROM. <see cref="Type"/> indexes the ROM's type list (0 Normal … 16 Dark).
/// <see cref="Recoil"/> is a percentage of damage dealt: negative is recoil, positive drains. <see cref="Heal"/> is the
/// percentage of max HP the user recovers. <see cref="Effect"/> is the ROM's effect script id for moves with
/// special behaviour the battle engine doesn't know yet.
/// </summary>
public sealed record MoveInfo(
    int Id,
    string Name,
    int Type,
    MoveKind Kind,
    MoveCategory Category,
    int Power,
    int Accuracy,
    int Pp,
    int Priority,
    int MinHits,
    int MaxHits,
    Ailment Ailment,
    int AilmentChance,
    int CritStage,
    int FlinchChance,
    int Effect,
    int Recoil,
    int Heal,
    int Target,
    IReadOnlyList<StatChange> StatChanges,
    int Flags)
{
    /// <summary>Accuracy 101 in the ROM means the move never misses.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeverMisses => Accuracy > 100;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool MakesContact => (Flags & 1) != 0;
}

/// <summary>A move a species learns on reaching <see cref="Level"/>.</summary>
public sealed record LevelMove(int Level, int Move);
