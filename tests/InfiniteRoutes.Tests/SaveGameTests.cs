using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Core.Player;

namespace InfiniteRoutes.Tests;

public class SaveGameTests
{
    static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0);
    static readonly BaseStats NoEvs = new(0, 0, 0, 0, 0, 0);

    static SpeciesInfo Species(int id, string name) =>
        new(id, name, new BaseStats(45, 49, 49, 45, 65, 65), ["12"], 45, NoEvs, 127, 20, 70, 3, [1], ["Overgrow"], null, 64, 6, 81, 1, [12]);

    static MoveInfo Move(int id, string name, int pp) =>
        new(id, name, 0, MoveKind.Damage, MoveCategory.Physical, 40, 100, pp, 0, 1, 1, Ailment.None, 0, 0, 0, 0, 0, 0, 0, [], 0);

    static BattleData Data()
    {
        var learnsets = new List<IReadOnlyList<LevelMove>> { Array.Empty<LevelMove>(), Array.Empty<LevelMove>() };
        learnsets.AddRange(Enumerable.Range(0, 3).Select(_ => (IReadOnlyList<LevelMove>)[new LevelMove(1, 33), new LevelMove(1, 45)]));
        return new BattleData([Species(2, "Bulba"), Species(3, "Ivy"), Species(4, "Venus")], [Move(33, "Tackle", 35), Move(45, "Growl", 40)], learnsets);
    }

    static TrainerInfo Leader(int id, string name) => new(id, name, 0, "Leader", 0, [], 0, 4, [new TrainerPokemon(2, 0, 5, 0, 0, 0, null)]);

    static readonly TrainerInfo[] Trainers = [new(1, "Jimmy", 0, "Youngster", 0, [], 0, 4, []), Leader(11, "Chili")];

    [Fact]
    public void A_game_survives_a_save_and_load()
    {
        var data = Data();
        var rng = new Random(7);
        var state = new GameState(new QuestLog(StarterQuests.Unova(Trainers))) { Location = new PlayerLocation(319, 754, 640, 1) };
        var lead = Pokemon.Create(data, 2, 12, rng);
        lead.Hp = 3;
        lead.Status = Ailment.Poison;
        lead.Moves[0].Pp = 7;
        lead.Evs[1] = 40;
        state.Party.Add(lead);
        state.Party.Add(Pokemon.Create(data, 3, 20, rng));
        state.Box.Add(Pokemon.Create(data, 4, 33, rng));
        state.Bag.Add(ItemEffect.PokeBall, 9);
        state.Bag.Add(ItemEffect.Potion, 2);
        state.Progress.Money = 4321;
        state.Progress.Caught = 3;
        state.Progress.PlayTime = TimeSpan.FromMinutes(95);
        state.Progress.Defeated(Trainers[0], Now);
        state.Progress.Defeated(Trainers[1], Now);
        state.Quests.Start(StarterQuests.FieldWork, state.Progress, Now);
        state.Progress.WildWins = 4;   // after the quest started: counts 4 of 10
        state.Quests.Start(StarterQuests.FirstCatch, state.Progress, Now);
        state.Progress.Caught = 4;   // a catch after it started finishes it
        state.Quests.Update(state.Progress, Now);
        state.Quests.TrackedId = StarterQuests.FieldWork;
        state.Mail.Send("Prof", "Hi", "Hello", Now, new MailAttachment(Money: 50));
        state.Mail.Send("Market", "Bought", "Here", Now, new MailAttachment(Pokemon: Pokemon.Create(data, 3, 9, rng)));
        state.Mail.Messages[1].Read = true;
        state.Friends.Set(new Friend("Kestrel", FriendState.Online, "Unova", 1, "Castelia City"));
        state.Friends.Request("Mako", "Me");
        state.Friends.Ignore("Spammer");
        state.Market.List("Me", ListingKind.Pokemon, 0, 1, Pokemon.Create(data, 4, 15, rng), 900, 3, Now, out _);
        state.Market.List("Seller", ListingKind.Item, ItemEffect.GreatBall, 4, null, 2000, 2, Now, out _);

        var json = SaveGame.From(state, "IRAO:abc", Now).ToJson();
        var loaded = SaveGame.FromJson(json)!.Restore(data, new QuestLog(StarterQuests.Unova(Trainers)));

        Assert.Equal(state.Location, loaded.Location);
        Assert.Equal(state.Party.Select(Describe), loaded.Party.Select(Describe));
        Assert.Equal(state.Box.Select(Describe), loaded.Box.Select(Describe));
        Assert.Equal(7, loaded.Party[0].Moves[0].Pp);
        Assert.Equal(Ailment.Poison, loaded.Party[0].Status);
        Assert.Equal(lead.StatValue(Stat.Attack), loaded.Party[0].StatValue(Stat.Attack));   // IVs, EVs, nature
        Assert.Equal(state.Bag.Items, loaded.Bag.Items);
        Assert.Equal(4321, loaded.Progress.Money);
        Assert.Equal(TimeSpan.FromMinutes(95), loaded.Progress.PlayTime);
        Assert.Equal([0], loaded.Progress.Badges.Keys);
        Assert.Equal([1, 11], loaded.Progress.DefeatedTrainers.Order());
        Assert.Equal(2, loaded.Party.Count);
        Assert.Equal(StarterQuests.FieldWork, loaded.Quests.Tracked?.Info.Id);
        Assert.Equal(4, loaded.Quests.Tracked!.Progress(0, loaded.Progress));   // the baseline survived
        Assert.True(loaded.Quests.IsDone(StarterQuests.FirstCatch));
        Assert.Equal(["Bought", "Hi"], loaded.Mail.Messages.Select(m => m.Subject));
        Assert.Equal("Ivy", loaded.Mail.Messages[0].Attachments[0].Pokemon?.Name);
        Assert.True(loaded.Mail.Messages[1].Read);
        Assert.Equal(3, loaded.Mail.Send("x", "y", "z", Now).Id);   // ids carry on
        Assert.Equal(["Kestrel", "Mako"], loaded.Friends.Friends.Select(f => f.Name));
        Assert.True(loaded.Friends.IsIgnored("Spammer"));
        Assert.Equal(2, loaded.Market.Listings.Count);
        Assert.Equal("Venus", loaded.Market.Listings.Single(l => l.Seller == "Me").Pokemon?.Name);
    }

    static string Describe(Pokemon m) =>
        $"{m.Species.Id} L{m.Level} X{m.Exp} {m.Nature} {m.Gender} {m.Shiny} {string.Join("/", m.Ivs)} {string.Join("/", m.Evs)} {m.Hp} {m.Status} {string.Join(",", m.Moves.Select(s => $"{s.Move.Id}:{s.Pp}"))}";

    [Fact]
    public void Unknown_species_and_moves_are_dropped_not_fatal()
    {
        var data = Data();
        var save = new PokemonSave(999, 5, 0, Nature.Hardy, Gender.Male, false, new int[6], new int[6], 10, Ailment.None, 0, []);
        Assert.Null(Pokemon.FromSave(data, save));
        var known = save with { Species = 2, Moves = [new MoveSave(33, 99), new MoveSave(777, 5)], Hp = 9999 };
        var mon = Pokemon.FromSave(data, known)!;
        Assert.Equal([33], mon.Moves.Select(m => m.Move.Id));
        Assert.Equal(35, mon.Moves[0].Pp);   // clamped to max PP
        Assert.Equal(mon.MaxHp, mon.Hp);
    }

    [Fact]
    public void The_PC_stores_and_returns_Pokemon()
    {
        var data = Data();
        var rng = new Random(3);
        var state = new GameState(new QuestLog([]));
        var lead = Pokemon.Create(data, 2, 10, rng);
        var hurt = Pokemon.Create(data, 3, 10, rng);
        state.Party.AddRange([lead, hurt]);
        hurt.Hp = 1;
        hurt.Status = Ailment.Burn;

        Assert.True(state.Deposit(1).Ok);
        Assert.Equal([lead], state.Party);
        Assert.Equal(hurt.MaxHp, state.Box[0].Hp);   // the PC heals
        Assert.Equal(Ailment.None, state.Box[0].Status);
        Assert.Equal(1, state.Progress.PartyCount);
        Assert.False(state.Deposit(0).Ok);   // never the last one

        lead.Hp = 0;
        state.Party.Add(Pokemon.Create(data, 4, 10, rng));
        Assert.False(state.Deposit(1).Ok);   // the only one left standing
        Assert.True(state.Deposit(0).Ok);    // a fainted one may go

        while (state.Party.Count < GameState.PartySize)
            state.Party.Add(Pokemon.Create(data, 2, 5, rng));
        Assert.False(state.Withdraw(0).Ok);  // party full
        state.Party.RemoveAt(5);
        Assert.True(state.Withdraw(0).Ok);
        Assert.Same(hurt, state.Party[^1]);
        Assert.True(state.Release(0).Ok);
        Assert.Empty(state.Box);
        Assert.False(state.Release(0).Ok);
    }

    [Fact]
    public void Writing_keeps_a_backup_and_reading_falls_back_to_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ir-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "save.json");
            var data = Data();
            var state = new GameState(new QuestLog([]));
            state.Party.Add(Pokemon.Create(data, 2, 5, new Random(1)));
            state.Progress.Money = 100;
            SaveGame.From(state, "g", Now).Write(path);
            state.Progress.Money = 200;
            SaveGame.From(state, "g", Now).Write(path);
            Assert.Equal(200, SaveGame.Read(path)?.Progress.Money);
            Assert.True(File.Exists(path + ".bak"));

            File.WriteAllText(path, "{ broken");
            Assert.Equal(100, SaveGame.Read(path)?.Progress.Money);   // the backup
            File.Delete(path + ".bak");
            Assert.Null(SaveGame.Read(path));
            Assert.Null(SaveGame.Read(Path.Combine(dir, "missing.json")));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Nicknames_and_box_names_are_saved_and_boxes_sort()
    {
        var data = Data();
        var rng = new Random(9);
        var state = new GameState(new QuestLog(StarterQuests.Unova(Trainers)));
        var lead = Pokemon.Create(data, 2, 12, rng);
        lead.Rename("  Leafy McLeafface  ");
        Assert.Equal("Leafy McLe", lead.Name);   // trimmed to ten characters
        state.Party.Add(lead);
        state.Box.Add(Pokemon.Create(data, 4, 5, rng));
        state.Box.Add(Pokemon.Create(data, 2, 30, rng));
        state.Box.Add(Pokemon.Create(data, 3, 9, rng));
        state.RenameBox(0, "Grass types");
        Assert.Equal("Grass ty", state.BoxNames[0]);

        state.SortBox(0, BoxSort.Number);
        Assert.Equal([2, 3, 4], state.Box.Select(m => m.Species.Id));
        state.SortBox(0, BoxSort.Level);
        Assert.Equal([30, 9, 5], state.Box.Select(m => m.Level));

        var loaded = SaveGame.FromJson(SaveGame.From(state, "IRAO", Now).ToJson())!.Restore(data, new QuestLog(StarterQuests.Unova(Trainers)));
        Assert.Equal("Leafy McLe", loaded.Party[0].Name);
        Assert.Equal("Grass ty", loaded.BoxNames[0]);
        Assert.Equal("Box 2", loaded.BoxNames[1]);

        lead.Rename("");
        Assert.Equal("Bulba", lead.Name);
    }
}
