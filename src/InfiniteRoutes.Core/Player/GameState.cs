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

    /// <summary>The first Pokémon that can still fight, or null when all have fainted.</summary>
    public Pokemon? Lead => Party.FirstOrDefault(p => !p.Fainted);

    /// <summary>Catches it takes before a party Pokémon can follow the player as their Active Buddy.</summary>
    public const int BuddyCatches = 3;

    /// <summary>The party Pokémon chosen as Active Buddy (it stays chosen while it's in the party).</summary>
    public Pokemon? Buddy { get; set; }

    public bool CanHaveBuddy => Progress.Caught >= BuddyCatches;

    /// <summary>The Pokémon following the player now: the buddy, while it's in the party and hasn't fainted.</summary>
    public Pokemon? ActiveBuddy => Buddy is { Fainted: false } b && Party.Contains(b) && CanHaveBuddy ? b : null;

    /// <summary>Makes party member <paramref name="index"/> the Active Buddy, or (-1) puts the buddy away.</summary>
    public (bool Ok, string Message) SetBuddy(int index)
    {
        if (index < 0)
        {
            var had = Buddy;
            Buddy = null;
            return (true, had is null ? "No Pokémon was following you." : $"{had.Name} went back into its Poké Ball.");
        }
        if (!CanHaveBuddy)
            return (false, $"Catch {BuddyCatches} Pokémon to unlock Active Buddy ({Math.Min(Progress.Caught, BuddyCatches)}/{BuddyCatches}).");
        if (index >= Party.Count)
            return (false, "There's no Pokémon there.");
        var mon = Party[index];
        if (mon.Fainted)
            return (false, $"{mon.Name} has fainted and can't follow you.");
        Buddy = mon;
        return (true, $"{mon.Name} is now your Active Buddy!");
    }

    /// <summary>Adds a caught Pokémon to the party, or the PC when the party is full. Returns where it went.</summary>
    public string Keep(Pokemon mon)
    {
        if (Party.Count < PartySize)
        {
            Party.Add(mon);
            return $"{mon.Name} was added to your party.";
        }
        Box.Add(mon);
        return $"{mon.Name} was sent to the PC.";
    }

    /// <summary>
    /// Finishes every quest whose objectives are met (and starts the ones they lead to). Returns them so their rewards
    /// can be handed out: by mail offline, straight into the game online (<see cref="GrantReward"/>).
    /// </summary>
    public List<QuestInfo> UpdateQuests(DateTime now)
    {
        Progress.PartyCount = Party.Count;
        Progress.TopLevel = Party.Select(m => m.Level).DefaultIfEmpty().Max();
        return Quests.Update(Progress, now);
    }

    /// <summary>Puts a quest's reward in the wallet and Bag. Returns what was received, for a message.</summary>
    public string GrantReward(QuestInfo quest, BattleData data)
    {
        var got = new List<string>();
        if (quest.Reward.Money > 0)
        {
            Progress.Money += quest.Reward.Money;
            got.Add($"₽{quest.Reward.Money:N0}");
        }
        foreach (var (item, count) in quest.Reward.ItemList)
        {
            Bag.Add(item, count);
            got.Add($"{data.ItemName(item)} ×{count}");
        }
        return string.Join(", ", got);
    }

    /// <summary>
    /// The player talked to an NPC (or a trainer spotted them). In order: an unbeaten trainer battles (gym leaders only
    /// once the badges before theirs are earned), a gift not yet collected is handed over, a quest giver hands out
    /// their quest. Online the server decides this; offline the client does.
    /// </summary>
    public NpcOutcome Deal(NpcDeal deal, IReadOnlyList<Data.TrainerInfo> trainers, BattleData data, DateTime now) =>
        Deal(deal, id => id >= 0 && id < trainers.Count ? trainers[id] : null, data, now);

    /// <summary>
    /// <see cref="Deal(NpcDeal, IReadOnlyList{Data.TrainerInfo}, BattleData, DateTime)"/> with trainers found by
    /// <paramref name="trainer"/> (the ROM's, or a hand-made route's).
    /// </summary>
    public NpcOutcome Deal(NpcDeal deal, Func<int, Data.TrainerInfo?> trainer, BattleData data, DateTime now)
    {
        if (deal.Trainer >= 0 && trainer(deal.Trainer) is { Pokemon.Count: > 0 } foe && !Progress.DefeatedTrainers.Contains(foe.Id))
        {
            var t = foe;
            if (Badges.Gate(Progress, t) is { } refusal)
                return new(NpcDealKind.Refused, refusal);
            return Lead is null
                ? new(NpcDealKind.Refused, "Your Pokémon are in no shape to battle. Heal them at a Pokémon Center first.")
                : new(NpcDealKind.Battle, "", t);
        }
        if (deal.GiftItem > 0 && !Progress.Gifts.Contains(deal.Key))
        {
            Progress.Gifts.Add(deal.Key);
            var count = Math.Max(1, deal.GiftCount);
            Bag.Add(deal.GiftItem, count);
            return new(NpcDealKind.Gift, $"You received {data.ItemName(deal.GiftItem)}{(count > 1 ? $" ×{count}" : "")}!");
        }
        if (Quests.Offer(deal.Key, Progress, now) is { } quest && Quests.Start(quest.Id, Progress, now))
            return new(NpcDealKind.Quest, $"New quest: {quest.Title}\n{quest.Description}", Quest: quest);
        return new(NpcDealKind.None);
    }

    public void HealParty()
    {
        foreach (var mon in Party)
            mon.HealFully();
    }

    /// <summary>
    /// Records a finished battle: prize money, trainers beaten and badges, wild wins, catches. Returns lines to show
    /// the player. (Online the server does this; offline the client does.)
    /// </summary>
    public List<string> RecordBattle(SingleBattle battle, DateTime now)
    {
        var lines = new List<string>();
        switch (battle.Outcome)
        {
            case BattleOutcome.Won when battle.Trainer is { } trainer:
                Progress.Money += battle.PrizeMoney;
                if (Progress.Defeated(trainer, now) is { } badge)
                    lines.Add($"You received the {badge.Name} from Leader {trainer.Name}!");
                break;
            case BattleOutcome.Won:
                Progress.WildWins++;
                Progress.Money += battle.PrizeMoney;   // Pay Day coins
                break;
            case BattleOutcome.Caught:
                Progress.Caught++;
                var species = (battle.Caught ?? battle.Foe.Mon).Species.Id;
                Progress.CaughtSpecies[species] = Progress.CaughtSpecies.GetValueOrDefault(species) + 1;
                lines.Add(Keep(battle.Caught ?? battle.Foe.Mon));
                break;
        }
        return lines;
    }

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
