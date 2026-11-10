using InfiniteRoutes.Core;
using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Core.Player;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// The offline test world's game: battle data from the extracted ROM and the player's <see cref="GameState"/> (party,
/// PC box, Bag, progress, quests, mail, friends and the market). It is saved to <see cref="SavePath"/> after battles,
/// warps, Pokémon Center visits, menu use, every minute and on quit, and loaded again on the next launch.
/// Friends and market sellers are sample data until the server exists (M4/M5).
/// </summary>
public static class OfflineSession
{
    /// <summary>Snivy, Black/White's grass starter; the test world hands it out at level 5.</summary>
    const int StarterSpecies = 495;
    const int StarterLevel = 5;
    public const int PartySize = GameState.PartySize;
    public const string PlayerName = "Player";
    const string Juniper = "Prof. Juniper", MarketDesk = "Global Market", QuestDesk = "Quest Board";

    static GameAssets? _assets;
    static DateTime _playClock = DateTime.Now;

    public static BattleData Data { get; private set; } = null!;
    public static IReadOnlyList<EncounterTable> Encounters { get; private set; } = [];
    public static IReadOnlyList<TrainerInfo> Trainers { get; private set; } = [];
    public static GameState State { get; private set; } = new(new QuestLog([]));
    public static List<Pokemon> Party => State.Party;
    /// <summary>Caught Pokémon that didn't fit in the party.</summary>
    public static List<Pokemon> Box => State.Box;
    public static Bag Bag => State.Bag;
    public static PlayerProgress Progress => State.Progress;
    /// <summary>Trainers already beaten: they don't challenge again.</summary>
    public static HashSet<int> DefeatedTrainers => Progress.DefeatedTrainers;
    public static int Money { get => Progress.Money; set => Progress.Money = value; }
    public static QuestLog Quests => State.Quests;
    public static Mailbox Mail => State.Mail;
    public static FriendList Friends => State.Friends;
    public static Market Market => State.Market;
    public static Random Rng { get; } = new();

    /// <summary>Saves go here instead of the user's save folder (smoke tests); set before the world opens.</summary>
    public static string? SaveDirOverride { get; set; }

    /// <summary>The save file for the loaded game: one per ROM (game code and SHA-1) and save slot.</summary>
    public static string SavePath { get; private set; } = "";

    public const int SlotCount = 3;

    /// <summary>The save slot in play (1–3); the last one used is picked again at launch.</summary>
    public static int Slot { get; private set; } = 1;

    /// <summary>Save files share this path: slot 1 is "{base}.json" (as before slots existed), slot n "{base}-n.json".</summary>
    static string _saveBase = "";

    public static string SlotPath(int slot) => slot <= 1 ? _saveBase + ".json" : $"{_saveBase}-{slot}.json";

    static string SlotMarker => _saveBase + ".slot";

    /// <summary>What's saved in <paramref name="slot"/>, or null for an empty slot.</summary>
    public static SaveGame? PeekSlot(int slot) => slot == Slot && LastSaved is null ? null : SaveGame.Read(SlotPath(slot));

    /// <summary>Saves the current game, then continues the one in <paramref name="slot"/> (a new game when it's empty).</summary>
    public static void SwitchSlot(int slot)
    {
        if (slot < 1 || slot > SlotCount || slot == Slot || _assets is null)
            return;
        Save();
        Slot = slot;
        SavePath = SlotPath(slot);
        try
        {
            File.WriteAllText(SlotMarker, slot.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Godot.GD.PushWarning($"Couldn't remember the save slot: {e.Message}");
        }
        State = new GameState(new QuestLog([]));
        LastSaved = null;
        Continued = Load();
        if (Party.Count == 0)
            NewGame();
    }
    public static DateTime? LastSaved { get; private set; }
    /// <summary>Whether this launch continued a save (false for a new game).</summary>
    public static bool Continued { get; private set; }
    /// <summary>Time played in total, this session included.</summary>
    public static TimeSpan PlayTime => Progress.PlayTime + (DateTime.Now - _playClock);

    /// <summary>
    /// Loads the battle data for <paramref name="assets"/> (once), then the save for that game, or starts a new game
    /// with a starter and items when there is none.
    /// </summary>
    public static bool Ensure(GameAssets assets)
    {
        if (!ReferenceEquals(assets, _assets))
        {
            var moves = assets.LoadMoves();
            if (moves.Count == 0)
                return false;
            Data = new BattleData(assets.LoadSpecies(), moves, assets.LoadLearnsets(), assets.LoadText("items"), assets.LoadItemPrices());
            Encounters = assets.LoadEncounters();
            Trainers = assets.LoadTrainers();
            _assets = assets;
            _saveBase = Path.Combine(SaveDirOverride ?? Path.Combine(AppPaths.SaveDir, "offline"), $"{assets.Stamp.GameCode}-{assets.Stamp.Sha1[..Math.Min(12, assets.Stamp.Sha1.Length)]}");
            Slot = File.Exists(SlotMarker) && int.TryParse(File.ReadAllText(SlotMarker).Trim(), out var last) && last is >= 1 and <= SlotCount ? last : 1;
            SavePath = SlotPath(Slot);
            State = new GameState(new QuestLog([]));
            Continued = Load();
        }
        if (Party.Count == 0)
            NewGame();
        return true;
    }

    static QuestLog NewQuestLog() => new(StarterQuests.Unova(Trainers));

    static bool Load()
    {
        if (SaveGame.Read(SavePath) is not { } save)
            return false;
        var state = save.Restore(Data, NewQuestLog());
        if (state.Party.Count == 0)
            return false;
        State = state;
        LastSaved = save.Saved;
        _playClock = DateTime.Now;
        return true;
    }

    /// <summary>Writes the save. False (and the game carries on) when the disk refuses.</summary>
    public static bool Save()
    {
        if (_assets is null || Party.Count == 0)
            return false;
        var now = DateTime.Now;
        Progress.PlayTime = PlayTime;
        _playClock = now;
        try
        {
            SaveGame.From(State, $"{_assets.Stamp.GameCode}:{_assets.Stamp.Sha1}", now).Write(SavePath);
            LastSaved = now;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Godot.GD.PushWarning($"Couldn't save to {SavePath}: {e.Message}");
            return false;
        }
    }

    /// <summary>Throws away the current game (and its save) and starts over with a new starter.</summary>
    public static void StartOver()
    {
        foreach (var file in new[] { SavePath, SavePath + ".bak" })
            if (File.Exists(file))
                File.Delete(file);
        State = new GameState(new QuestLog([]));
        LastSaved = null;
        Continued = false;
        NewGame();
    }

    static void NewGame()
    {
        var now = DateTime.Now;
        _playClock = now;
        State = new GameState(NewQuestLog());
        Party.Add(Pokemon.Create(Data, StarterSpecies, StarterLevel, Rng));
        Bag.Add(ItemEffect.PokeBall, 10);
        Bag.Add(ItemEffect.GreatBall, 3);
        Bag.Add(ItemEffect.Potion, 5);
        Bag.Add(ItemEffect.Antidote, 2);
        Bag.Add(ItemEffect.ParlyzHeal, 2);
        Bag.Add(ItemEffect.ReviveItem, 1);
        Bag.Add(HeldItems.OranBerry, 2);
        Bag.Add(OverworldScreen.SuperRod);   // the test world hands it out at the start
        Progress.PartyCount = Party.Count;
        foreach (var id in StarterQuests.Initial)
            Quests.Start(id, Progress, now);
        Mail.Send(Juniper, "Welcome to Unova!",
            $"Hello, {PlayerName}! {Party[0].Name} is yours now. Take good care of each other.\n\nYour quests are in the menu (X → Quests): the one you track shows at the top right. Rewards arrive here in your Mail, so check back after finishing one.\n\nYour game saves by itself as you play.\n\nI've enclosed a little something for the road.",
            now, new MailAttachment(Money: 500), new MailAttachment(Item: ItemEffect.Potion, Count: 3));
        AddSampleFriends(now);
        SeedMarket(now);
    }

    /// <summary>The first Pokémon that can still fight, or null when all have fainted.</summary>
    public static Pokemon? Lead => Party.FirstOrDefault(p => !p.Fainted);

    /// <summary>Adds a caught Pokémon to the party, or the box when the party is full. Returns where it went.</summary>
    public static string Keep(Pokemon mon)
    {
        if (Party.Count < PartySize)
        {
            Party.Add(mon);
            return $"{mon.Name} was added to your party.";
        }
        Box.Add(mon);
        return $"{mon.Name} was sent to the PC.";
    }

    public static void HealParty()
    {
        foreach (var mon in Party)
            mon.HealFully();
    }

    /// <summary>Records a finished battle: prize money, trainers and badges, catches. Returns lines to show the player.</summary>
    public static List<string> RecordBattle(SingleBattle battle)
    {
        var lines = new List<string>();
        switch (battle.Outcome)
        {
            case BattleOutcome.Won when battle.Trainer is { } trainer:
                Money += battle.PrizeMoney;
                if (Progress.Defeated(trainer, DateTime.Now) is { } badge)
                    lines.Add($"You received the {badge.Name} from Leader {trainer.Name}!");
                break;
            case BattleOutcome.Won:
                Progress.WildWins++;
                Money += battle.PrizeMoney;   // Pay Day coins
                break;
            case BattleOutcome.Caught:
                Progress.Caught++;
                lines.Add(Keep(battle.Foe.Mon));
                break;
        }
        return lines;
    }

    public static void RecordCenterVisit()
    {
        HealParty();
        Progress.CenterVisits++;
    }

    /// <summary>
    /// Finishes quests whose objectives are met and mails their rewards. Returns one line per finished quest, for a
    /// toast. Also settles the market (expired listings, sample buyers).
    /// </summary>
    public static List<string> CheckQuests()
    {
        var now = DateTime.Now;
        Progress.PartyCount = Party.Count;
        var lines = new List<string>();
        foreach (var quest in Quests.Update(Progress, now))
        {
            var attachments = new List<MailAttachment>();
            if (quest.Reward.Money > 0)
                attachments.Add(new MailAttachment(Money: quest.Reward.Money));
            attachments.AddRange(quest.Reward.ItemList.Select(i => new MailAttachment(Item: i.Item, Count: i.Count)));
            Mail.Send(QuestDesk, $"Quest complete: {quest.Title}", $"Well done! You completed \"{quest.Title}\". Your reward is attached.", now, [.. attachments]);
            lines.Add($"Quest complete: {quest.Title}");
        }
        SettleMarket(now);
        return lines;
    }

    /// <summary>Collects a letter's attachments into the wallet, Bag and party. Returns what was received.</summary>
    public static string Claim(MailMessage message)
    {
        var got = new List<string>();
        foreach (var a in Mail.Claim(message))
        {
            if (a.Money > 0)
            {
                Money += a.Money;
                got.Add($"₽{a.Money:N0}");
            }
            if (a.Item > 0 && a.Count > 0)
            {
                Bag.Add(a.Item, a.Count);
                got.Add($"{Data.ItemName(a.Item)} ×{a.Count}");
            }
            if (a.Pokemon is { } mon)
                got.Add(Keep(mon));
        }
        return got.Count == 0 ? "Nothing to collect." : "Received " + string.Join(", ", got) + ".";
    }

    // ── Market ────────────────────────────────────────────────────────────────────────────────────────────────

    public static string ListingName(MarketListing l) =>
        l.Kind == ListingKind.Pokemon && l.Pokemon is { } mon ? $"{mon.Name} Lv{mon.Level}" : $"{Data.ItemName(l.Item)} ×{l.Count}";

    /// <summary>Roughly what something is worth, for sample listings and to guide the player's prices.</summary>
    public static int SuggestedPrice(ListingKind kind, int item, int count, Pokemon? mon)
    {
        if (kind == ListingKind.Pokemon && mon is not null)
            return (mon.Species.Stats.Total * 4 + mon.Level * 120) * (mon.Shiny ? 10 : 1);
        var each = item switch
        {
            ItemEffect.MasterBall => 50000, ItemEffect.UltraBall => 1200, ItemEffect.GreatBall => 600, ItemEffect.PokeBall => 200,
            ItemEffect.Potion => 300, ItemEffect.SuperPotion => 700, ItemEffect.HyperPotion => 1200, ItemEffect.MaxPotion => 2500,
            ItemEffect.FullRestore => 3000, ItemEffect.ReviveItem => 1500, ItemEffect.MaxRevive => 4000, ItemEffect.FullHeal => 600,
            _ => 250,
        };
        return each * Math.Max(1, count);
    }

    public static string Buy(MarketListing listing)
    {
        var money = Money;
        var result = Market.Buy(listing.Id, PlayerName, ref money, DateTime.Now, out var bought);
        Money = money;
        if (result != BuyResult.Bought || bought is null)
            return result switch
            {
                BuyResult.NotEnoughMoney => $"You need ₽{listing.Price:N0} but have ₽{Money:N0}.",
                BuyResult.OwnListing => "That's your own listing.",
                _ => "That listing is gone.",
            };
        Mail.Send(MarketDesk, $"Purchase: {ListingName(bought)}", $"You bought {ListingName(bought)} from {bought.Seller} for ₽{bought.Price:N0}.",
            DateTime.Now, Goods(bought));
        return $"Bought {ListingName(bought)} for ₽{bought.Price:N0}. It's waiting in your Mail.";
    }

    /// <summary>Lists <paramref name="count"/> of a Bag item. Returns a message for the player.</summary>
    public static string SellItem(int item, int count, int price, int days)
    {
        if (count <= 0 || Bag.Count(item) < count)
            return "You don't have that many.";
        var fee = Market.ListingFee(price);
        if (Money < fee)
            return $"The listing fee is ₽{fee:N0}.";
        if (Market.List(PlayerName, ListingKind.Item, item, count, null, price, days, DateTime.Now, out _) is { } error)
            return error;
        for (var i = 0; i < count; i++)
            Bag.Take(item);
        Money -= fee;
        return $"Listed {Data.ItemName(item)} ×{count} for ₽{price:N0} (fee ₽{fee:N0}). Proceeds arrive by mail when it sells.";
    }

    /// <summary>Lists a party Pokémon (never the last one). Returns a message for the player.</summary>
    public static string SellPokemon(Pokemon mon, int price, int days)
    {
        if (Party.Count <= 1 || !Party.Contains(mon))
            return "You can't list your last Pokémon.";
        var fee = Market.ListingFee(price);
        if (Money < fee)
            return $"The listing fee is ₽{fee:N0}.";
        if (Market.List(PlayerName, ListingKind.Pokemon, 0, 1, mon, price, days, DateTime.Now, out _) is { } error)
            return error;
        Party.Remove(mon);
        Money -= fee;
        return $"Listed {mon.Name} Lv{mon.Level} for ₽{price:N0} (fee ₽{fee:N0}).";
    }

    public static string CancelListing(MarketListing listing)
    {
        if (Market.Cancel(listing.Id, PlayerName) is not { } cancelled)
            return "That listing is gone.";
        Mail.Send(MarketDesk, $"Listing cancelled: {ListingName(cancelled)}", "Your listing was taken down. Your goods are attached; the listing fee isn't refunded.",
            DateTime.Now, Goods(cancelled));
        return $"Cancelled. {ListingName(cancelled)} is waiting in your Mail.";
    }

    static MailAttachment Goods(MarketListing l) =>
        l.Kind == ListingKind.Pokemon ? new MailAttachment(Pokemon: l.Pokemon) : new MailAttachment(Item: l.Item, Count: l.Count);

    /// <summary>
    /// Returns expired listings by mail. Offline there are no other players, so a sample buyer takes the player's
    /// fairly priced listings (up to 25% over the suggested price) after two minutes.
    /// </summary>
    static void SettleMarket(DateTime now)
    {
        foreach (var l in Market.Expire(now))
            if (l.Seller == PlayerName)
                Mail.Send(MarketDesk, $"Unsold: {ListingName(l)}", "Your listing ended without a buyer. Your goods are attached.", now, Goods(l));
        foreach (var l in Market.Listings.Where(l => l.Seller == PlayerName && now - l.Listed >= TimeSpan.FromMinutes(2)
                     && l.Price <= SuggestedPrice(l.Kind, l.Item, l.Count, l.Pokemon) * 5 / 4).ToList())
        {
            var buyer = SampleSellers[Rng.Next(SampleSellers.Length)];
            var cash = int.MaxValue;
            if (Market.Buy(l.Id, buyer, ref cash, now, out _) != BuyResult.Bought)
                continue;
            var proceeds = Market.Proceeds(l.Price);
            Mail.Send(MarketDesk, $"Sold: {ListingName(l)}", $"{buyer} bought your {ListingName(l)} for ₽{l.Price:N0}. After the 5% sales tax you receive ₽{proceeds:N0}.",
                now, new MailAttachment(Money: proceeds));
        }
    }

    static readonly string[] SampleSellers = ["Kestrel", "Mako", "Juno_W", "BerryBroker", "Tamsin", "Orrin-77", "Vesper", "Calloway"];

    static void SeedMarket(DateTime now)
    {
        int[] items = [ItemEffect.PokeBall, ItemEffect.GreatBall, ItemEffect.UltraBall, ItemEffect.Potion, ItemEffect.SuperPotion,
            ItemEffect.HyperPotion, ItemEffect.FullHeal, ItemEffect.ReviveItem, ItemEffect.MaxRevive];
        for (var i = 0; i < 14; i++)
        {
            var item = items[Rng.Next(items.Length)];
            var count = Rng.Next(1, 11);
            Seed(ListingKind.Item, item, count, null);
        }
        // Pokémon from the Unova dex (Snivy 495 … Genesect 649), skipping legendaries at the end.
        for (var i = 0; i < 12; i++)
        {
            var species = Rng.Next(495, 638);
            var mon = Pokemon.Create(Data, species, Rng.Next(5, 36), Rng);
            Seed(ListingKind.Pokemon, 0, 1, mon);
        }

        void Seed(ListingKind kind, int item, int count, Pokemon? mon)
        {
            var price = SuggestedPrice(kind, item, count, mon) * Rng.Next(80, 141) / 100;
            Market.List(SampleSellers[Rng.Next(SampleSellers.Length)], kind, item, count, mon, Math.Max(1, price), Rng.Next(1, 8),
                now.AddMinutes(-Rng.Next(1, 600)), out _);
        }
    }

    static void AddSampleFriends(DateTime now)
    {
        Friends.Set(new Friend("Kestrel", FriendState.Online, "Unova", 1, "Castelia City", Note: "sample"));
        Friends.Set(new Friend("Mako", FriendState.Away, "Unova", 2, "Route 4", Note: "sample"));
        Friends.Set(new Friend("Tamsin", FriendState.Offline, "Unova", LastSeen: now.AddHours(-5), Note: "sample"));
        Friends.Set(new Friend("Orrin-77", FriendState.RequestReceived, Note: "sample"));
    }
}
