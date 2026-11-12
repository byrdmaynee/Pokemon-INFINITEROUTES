using InfiniteRoutes.Core.Battle;

namespace InfiniteRoutes.Core.Player;

/// <summary>
/// Side quests handed out by Unova's townspeople. The givers are the ROM's own NPCs (by zone and NPC id in Black/White;
/// they keep saying their own lines and then offer the quest); the quests themselves are ours, since the games have
/// none. Givers show a "!" while they have a quest for the player.
/// </summary>
public static class SideQuests
{
    // National Dex numbers of the species asked for.
    const int Munna = 517, Patrat = 504, Lillipup = 506, Pidove = 519, Ducklett = 580, Woobat = 527, Roggenrola = 524;

    static string At(int zone, int npc) => NpcDeal.KeyOf(zone, npc);

    public static IEnumerable<QuestInfo> Unova() =>
    [
        new("side-tall-grass-tales", "Tall Grass Tales",
            "A girl in Nuvema Town dreams of the Pokémon past the edge of town. Catch a Patrat and a Lillipup on Route 1 so she can hear about them.",
            QuestCategory.Side,
            [new(ObjectiveKind.CatchSpecies, "Catch a Patrat", Target: Patrat), new(ObjectiveKind.CatchSpecies, "Catch a Lillipup", Target: Lillipup)],
            new(Money: 500, Items: [(ItemEffect.PokeBall, 5)]), Giver: At(389, 2)),
        new("side-scenic-survey", "Scenic Survey",
            "The man enjoying the view in Accumula Town would love a list of the Pokémon living nearby. Register five different species.",
            QuestCategory.Side, [new(ObjectiveKind.DexCount, "Catch 5 different species", 5)],
            new(Items: [(ItemEffect.GreatBall, 5)]), Giver: At(397, 0)),
        new("daily-grass-patrol", "Daily: Grass Patrol",
            "The hiker on Route 1 keeps an eye on the tall grass. Help him out by defeating wild Pokémon today.",
            QuestCategory.Daily, [new(ObjectiveKind.WinWild, "Defeat 6 wild Pokémon", 6)],
            new(Money: 600, Items: [(ItemEffect.Potion, 2)]), Giver: At(317, 1)),
        new("side-type-matchups", "Type Matchups",
            "Striaton's TM enthusiast says a well-trained Pokémon learns the best moves. Raise a Pokémon to level 15.",
            QuestCategory.Side, [new(ObjectiveKind.ReachLevel, "Raise a Pokémon to Lv. 15", 15)],
            new(Money: 1500, Items: [(ItemEffect.SuperPotion, 2)]), Giver: At(6, 3)),
        new("side-dream-researcher", "The Dreamyard",
            "A Striaton local tells of an unusual Pokémon in the Dreamyard that can make dreams appear. Catch a Munna there.",
            QuestCategory.Side, [new(ObjectiveKind.CatchSpecies, "Catch a Munna", Target: Munna)],
            new(Money: 2000, Items: [(ItemEffect.GreatBall, 3)]), Giver: At(6, 2), MinBadges: 1),
        new("side-warehouse-rhythm", "Warehouse Rhythm",
            "Nacrene's accordion player wants an audience of strong Trainers. Beat five Trainers and come back with a story.",
            QuestCategory.Side, [new(ObjectiveKind.DefeatTrainers, "Defeat 5 Trainers", 5)],
            new(Money: 2500, Items: [(ItemEffect.SuperPotion, 3)]), Giver: At(16, 0), MinBadges: 1),
        new("side-cave-dwellers", "Cave Dwellers",
            "A Nacrene local heard that Wellspring Cave is full of Pokémon. Catch a Roggenrola and a Woobat.",
            QuestCategory.Side,
            [new(ObjectiveKind.CatchSpecies, "Catch a Roggenrola", Target: Roggenrola), new(ObjectiveKind.CatchSpecies, "Catch a Woobat", Target: Woobat)],
            new(Money: 2000, Items: [(ItemEffect.ReviveItem, 2)]), Giver: At(16, 3), MinBadges: 1),
        new("side-teamwork", "Teamwork Training",
            "An athlete in Nimbasa City says teamwork wins matches. Raise a Pokémon to level 30.",
            QuestCategory.Side, [new(ObjectiveKind.ReachLevel, "Raise a Pokémon to Lv. 30", 30)],
            new(Money: 4000, Items: [(ItemEffect.HyperPotion, 2)]), Giver: At(62, 0), MinBadges: 3),
        new("daily-battle-destination", "Daily: Battle Destination",
            "Nimbasa City is a battle town. Win wild battles today to keep your team sharp.",
            QuestCategory.Daily, [new(ObjectiveKind.WinWild, "Defeat 8 wild Pokémon", 8)],
            new(Money: 1500, Items: [(ItemEffect.SuperPotion, 2)]), Giver: At(62, 1), MinBadges: 3),
        new("side-surfs-up", "Surf's Up",
            "A Driftveil man wishes his Pokémon could swim. Show him a Water-type: catch a Ducklett near the Driftveil Drawbridge.",
            QuestCategory.Side, [new(ObjectiveKind.CatchSpecies, "Catch a Ducklett", Target: Ducklett)],
            new(Money: 3000, Items: [(ItemEffect.UltraBall, 3)]), Giver: At(96, 2), MinBadges: 4),
        new("side-cold-storage", "Kids of the Cold Storage",
            "Kids near Driftveil's Cold Storage are catching every Pokémon they can find. Beat their record: twenty different species.",
            QuestCategory.Side, [new(ObjectiveKind.DexCount, "Catch 20 different species", 20)],
            new(Money: 3000, Items: [(ItemEffect.UltraBall, 5)]), Giver: At(96, 1), MinBadges: 4),
        new("side-flight-plan", "Flight Plan",
            "Mistralton's aviation fan admires how birds fly. Catch a Pidove so he can study its wings.",
            QuestCategory.Side, [new(ObjectiveKind.CatchSpecies, "Catch a Pidove", Target: Pidove)],
            new(Money: 2500, Items: [(ItemEffect.MaxRepel, 3)]), Giver: At(107, 1), MinBadges: 5),
        new("side-two-dragons", "Dance of Two Dragons",
            "Icirrus City's dancers honour the two legendary dragons. Prove your strength: raise a Pokémon to level 45.",
            QuestCategory.Side, [new(ObjectiveKind.ReachLevel, "Raise a Pokémon to Lv. 45", 45)],
            new(Money: 6000, Items: [(ItemEffect.FullRestore, 2)]), Giver: At(113, 6), MinBadges: 6),
        new("side-bond-of-trust", "Bond of Trust",
            "A veteran Trainer in Opelucid wonders whether Trainers and Pokémon belong together. Show her: catch forty different species.",
            QuestCategory.Side, [new(ObjectiveKind.DexCount, "Catch 40 different species", 40)],
            new(Money: 10000, Items: [(ItemEffect.UltraBall, 10), (ItemEffect.MaxRevive, 2)]), Giver: At(120, 2), MinBadges: 7),
    ];
}
