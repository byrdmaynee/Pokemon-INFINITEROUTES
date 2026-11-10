using InfiniteRoutes.Core.Battle;

namespace InfiniteRoutes.Core.Player;

/// <summary>Where the player stands: zone, tile and facing (0 down, 1 up, 2 left, 3 right, as the ROM stores it).</summary>
public sealed record PlayerLocation(int Zone, int X, int Y, int Facing = 0);

/// <summary>
/// One character's game: party, PC box, Bag, progress, quests, mail, friends and the market listings they can see.
/// Offline the client keeps one in memory and saves it; online the server owns it (BUILD_SPEC §5).
/// </summary>
public sealed class GameState(QuestLog quests)
{
    public const int PartySize = 6;
    /// <summary>The PC holds <see cref="BoxCount"/> boxes of <see cref="BoxSize"/>, as in Black/White.</summary>
    public const int BoxSize = 30, BoxCount = 24, BoxCapacity = BoxSize * BoxCount;

    public List<Pokemon> Party { get; } = new();
    /// <summary>The PC: deposited Pokémon and catches that didn't fit in the party, in order (box n holds 30n…30n+29).</summary>
    public List<Pokemon> Box { get; } = new();

    /// <summary>Steps of Repel left: wild Pokémon weaker than the lead stay away.</summary>
    public int RepelSteps { get; set; }

    /// <summary>Where the player last stepped from the overworld into a building or cave: Escape Rope's destination.</summary>
    public PlayerLocation? LastOutdoor { get; set; }

    /// <summary>
    /// Uses a Repel from the Bag; false (and nothing used) while one is still working.
    /// </summary>
    public (bool Ok, string Message) UseRepel(int item, string name)
    {
        var steps = ItemEffect.RepelSteps(item);
        if (steps == 0 || Bag.Count(item) <= 0)
            return (false, "That can't be used now.");
        if (RepelSteps > 0)
            return (false, "The effects of the last Repel are still lingering.");
        Bag.Take(item);
        RepelSteps = steps;
        return (true, $"You used the {name}. Weak wild Pokémon will stay away for {steps} steps.");
    }

    /// <summary>A step while a Repel works: true when this one wore it off.</summary>
    public bool RepelStep() => RepelSteps > 0 && --RepelSteps == 0;

    /// <summary>The PC boxes' names ("Box 1" … unless renamed).</summary>
    public List<string> BoxNames { get; } = Enumerable.Range(1, BoxCount).Select(i => $"Box {i}").ToList();

    public const int MaxBoxNameLength = 8;

    /// <summary>Renames box <paramref name="box"/>; a blank name puts back "Box n".</summary>
    public void RenameBox(int box, string name)
    {
        if (box < 0 || box >= BoxCount)
            return;
        name = name.Trim();
        if (name.Length > MaxBoxNameLength)
            name = name[..MaxBoxNameLength];
        BoxNames[box] = name.Length == 0 ? $"Box {box + 1}" : name;
    }

    /// <summary>Sorts the Pokémon in box <paramref name="box"/> by Pokédex number, level (highest first) or name.</summary>
    public void SortBox(int box, BoxSort by)
    {
        var start = box * BoxSize;
        if (box < 0 || box >= BoxCount || start >= Box.Count)
            return;
        var count = Math.Min(BoxSize, Box.Count - start);
        var sorted = (by switch
        {
            BoxSort.Level => Box.GetRange(start, count).OrderByDescending(m => m.Level).ThenBy(m => m.Species.Id),
            BoxSort.Name => Box.GetRange(start, count).OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => Box.GetRange(start, count).OrderBy(m => m.Species.Id).ThenByDescending(m => m.Level),
        }).ToList();
        for (var i = 0; i < count; i++)
            Box[start + i] = sorted[i];
    }
    public Bag Bag { get; } = new();
    public PlayerProgress Progress { get; init; } = new();
    public QuestLog Quests { get; } = quests;
    public Mailbox Mail { get; } = new();
    public FriendList Friends { get; } = new();
    public Market Market { get; } = new();
    /// <summary>Null until the player has been placed in the world (a new game starts at home).</summary>
    public PlayerLocation? Location { get; set; }

    /// <summary>
    /// Moves a party Pokémon into the PC, healed as the PC does. The party must keep at least one Pokémon that can
    /// still fight.
    /// </summary>
    public (bool Ok, string Message) Deposit(int partyIndex)
    {
        if (partyIndex < 0 || partyIndex >= Party.Count)
            return (false, "There's no Pokémon there.");
        var mon = Party[partyIndex];
        if (!Party.Where((m, i) => i != partyIndex && !m.Fainted).Any())
            return (false, $"{mon.Name} is your last Pokémon that can battle!");
        if (Box.Count >= BoxCapacity)
            return (false, "The PC boxes are full.");
        Party.RemoveAt(partyIndex);
        mon.HealFully();
        Box.Add(mon);
        Progress.PartyCount = Party.Count;
        return (true, $"{mon.Name} was stored in Box {(Box.Count - 1) / BoxSize + 1}.");
    }

    /// <summary>Moves a Pokémon from the PC to the end of the party.</summary>
    public (bool Ok, string Message) Withdraw(int boxIndex)
    {
        if (boxIndex < 0 || boxIndex >= Box.Count)
            return (false, "There's no Pokémon there.");
        if (Party.Count >= PartySize)
            return (false, "Your party is full! Deposit a Pokémon first.");
        var mon = Box[boxIndex];
        Box.RemoveAt(boxIndex);
        Party.Add(mon);
        Progress.PartyCount = Party.Count;
        return (true, $"{mon.Name} joined your party.");
    }

    /// <summary>Releases a Pokémon from the PC for good.</summary>
    public (bool Ok, string Message) Release(int boxIndex)
    {
        if (boxIndex < 0 || boxIndex >= Box.Count)
            return (false, "There's no Pokémon there.");
        var mon = Box[boxIndex];
        Box.RemoveAt(boxIndex);
        return (true, $"{mon.Name} was released. Bye-bye, {mon.Name}!");
    }
}

public enum BoxSort { Number, Level, Name }
