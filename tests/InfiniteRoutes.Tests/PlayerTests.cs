using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Core.Player;

namespace InfiniteRoutes.Tests;

public class PlayerTests
{
    static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0);

    static TrainerInfo Trainer(int id, string name, string className) =>
        new(id, name, 0, className, 0, [], 0, 4, [new TrainerPokemon(504, 0, 5, 0, 0, 0, null)]);

    static readonly TrainerInfo[] Trainers =
    [
        Trainer(1, "Jimmy", "Youngster"), Trainer(11, "Chili", "Leader"), Trainer(12, "Cilan", "Leader"),
        Trainer(21, "Lenora", "Leader"), Trainer(30, "Lenora", "Waitress"),
    ];

    [Fact]
    public void Gym_leaders_award_their_badge_once()
    {
        var progress = new PlayerProgress();
        Assert.Null(progress.Defeated(Trainers[0], Now));
        Assert.Equal("Trio Badge", progress.Defeated(Trainers[1], Now)?.Name);
        Assert.Null(progress.Defeated(Trainers[2], Now));   // another Striaton leader: same badge
        Assert.Null(progress.Defeated(Trainers[4], Now));   // same name, not a leader
        Assert.Equal("Basic Badge", progress.Defeated(Trainers[3], Now)?.Name);
        Assert.Equal([0, 1], progress.Badges.Keys);
        Assert.Equal(5, progress.DefeatedTrainers.Count);
        Assert.Equal(8, Badges.Unova.Count);
    }

    [Fact]
    public void Counter_objectives_count_from_the_quests_start()
    {
        var quest = new QuestInfo("q", "Q", "", QuestCategory.Side, [new(ObjectiveKind.Catch, "Catch 2", 2)], new(Money: 100));
        var log = new QuestLog([quest]);
        var progress = new PlayerProgress { Caught = 5 };
        Assert.True(log.Start("q", progress, Now));
        Assert.False(log.Start("q", progress, Now));
        progress.Caught = 6;
        Assert.Empty(log.Update(progress, Now));
        Assert.Equal(1, log.Active[0].Progress(0, progress));
        progress.Caught = 9;
        Assert.Equal(2, log.Active[0].Progress(0, progress));   // clamped to the target
        Assert.Equal([quest], log.Update(progress, Now));
        Assert.Empty(log.Active);
        Assert.True(log.IsDone("q"));
        Assert.False(log.Start("q", progress, Now));   // non-daily quests complete once
    }

    [Fact]
    public void Story_quests_chain_and_already_met_ones_finish_at_once()
    {
        var quests = StarterQuests.Unova(Trainers);
        var log = new QuestLog(quests);
        var progress = new PlayerProgress();
        log.Start(StarterQuests.RestStop, progress, Now);
        log.Start(StarterQuests.FirstCatch, progress, Now);
        Assert.Equal(StarterQuests.FirstCatch, log.Tracked?.Info.Id);   // the story quest leads the HUD
        progress.Defeated(Trainers[0], Now);   // Jimmy beaten before the catch
        progress.Caught = 1;
        var done = log.Update(progress, Now).Select(q => q.Id).ToList();
        Assert.Equal([StarterQuests.FirstCatch, StarterQuests.FirstTrainer], done);
        Assert.Equal(StarterQuests.BadgeQuest(0), log.Tracked?.Info.Id);
        Assert.False(log.Abandon(StarterQuests.BadgeQuest(0)));   // story quests stay
        Assert.Contains("Cilan / Chili", log.Tracked!.Info.Description);
    }

    [Fact]
    public void Daily_quests_restart_the_next_day()
    {
        var log = new QuestLog(StarterQuests.Unova(Trainers));
        var progress = new PlayerProgress();
        log.Start(StarterQuests.DailyBattles, progress, Now);
        progress.WildWins = 5;
        Assert.Single(log.Update(progress, Now));
        Assert.Empty(log.Update(progress, Now.AddHours(1)));
        Assert.Empty(log.Active);
        log.Update(progress, Now.AddDays(1));
        var restarted = Assert.Single(log.Active);
        Assert.Equal(0, restarted.Progress(0, progress));   // counts again from zero
    }

    [Fact]
    public void Party_size_and_badge_count_objectives_read_the_current_state()
    {
        var progress = new PlayerProgress { PartyCount = 6 };
        Assert.Equal(6, new QuestObjective(ObjectiveKind.PartySize, "", 6).Counter(progress));
        progress.Defeated(Trainers[1], Now);
        Assert.Equal(1, new QuestObjective(ObjectiveKind.Badges, "", 8).Counter(progress));
        Assert.Equal(1, new QuestObjective(ObjectiveKind.EarnBadge, "", Target: 0).Counter(progress));
    }

    [Fact]
    public void Mail_attachments_are_claimed_once_and_guard_deletion()
    {
        var box = new Mailbox();
        var plain = box.Send("A", "Hi", "Hello", Now);
        var gift = box.Send("B", "Gift", "For you", Now, new MailAttachment(Money: 100), new MailAttachment(), new MailAttachment(Item: 4, Count: 2));
        Assert.Equal(gift, box.Messages[0]);   // newest first
        Assert.Equal(2, box.Unread);
        Assert.Equal(2, gift.Attachments.Count);   // the empty one is dropped
        Assert.False(box.Delete(gift));
        Assert.Equal(2, box.Claim(gift).Count);
        Assert.Empty(box.Claim(gift));
        Assert.True(gift.Read);
        Assert.True(box.Delete(gift));
        Assert.True(box.Delete(plain));
        Assert.Equal(Mailbox.MaxSubjectLength, box.Send("A", new string('x', 99), "", Now).Subject.Length);
    }

    [Fact]
    public void Friend_requests_validate_names_and_duplicates()
    {
        var friends = new FriendList();
        Assert.NotNull(friends.Request("ab", "Me"));
        Assert.NotNull(friends.Request("bad!name", "Me"));
        Assert.NotNull(friends.Request("me", "Me"));
        Assert.Null(friends.Request("Kestrel", "Me"));
        Assert.NotNull(friends.Request("kestrel", "Me"));
        friends.Set(new Friend("Mako", FriendState.RequestReceived));
        Assert.True(friends.Accept("mako"));
        Assert.Equal(FriendState.Offline, friends.Friends.Single(f => f.Name == "Mako").State);
        friends.Set(new Friend("Juno", FriendState.Online));
        Assert.Equal(["Juno", "Mako", "Kestrel"], friends.Friends.Select(f => f.Name));   // online, offline, pending
        friends.Ignore("Juno");
        Assert.True(friends.IsIgnored("juno"));
        Assert.Equal(0, friends.OnlineCount);
        Assert.Null(friends.Request("Juno", "Me"));   // asking again lifts the ignore
        Assert.False(friends.IsIgnored("Juno"));
    }

    [Fact]
    public void Market_lists_searches_and_sells()
    {
        var market = new Market();
        Assert.NotNull(market.List("Seller", ListingKind.Item, 4, 5, null, 0, 3, Now, out _));
        Assert.NotNull(market.List("Seller", ListingKind.Item, 4, 5, null, 100, 8, Now, out _));
        Assert.NotNull(market.List("Seller", ListingKind.Pokemon, 0, 1, null, 100, 3, Now, out _));
        Assert.Null(market.List("Seller", ListingKind.Item, 4, 5, null, 1000, 3, Now, out var balls));
        Assert.Null(market.List("Seller", ListingKind.Item, 17, 1, null, 300, 1, Now.AddMinutes(1), out var potion));

        string Name(MarketListing l) => l.Item == 4 ? "Poké Ball" : "Potion";
        Assert.Equal([potion, balls], market.Search(new(), Name, Now.AddMinutes(2)));
        Assert.Equal([potion, balls], market.Search(new(Sort: MarketSort.PriceLow), Name, Now.AddMinutes(2)));
        Assert.Equal([balls], market.Search(new(Text: "poké"), Name, Now.AddMinutes(2)));
        Assert.Equal([potion], market.Search(new(MaxPrice: 500), Name, Now.AddMinutes(2)));
        Assert.Empty(market.Search(new(Kind: ListingKind.Pokemon), Name, Now));

        var money = 500;
        Assert.Equal(BuyResult.NotEnoughMoney, market.Buy(balls!.Id, "Buyer", ref money, Now, out _));
        Assert.Equal(BuyResult.OwnListing, market.Buy(balls.Id, "Seller", ref money, Now, out _));
        Assert.Equal(BuyResult.Bought, market.Buy(potion!.Id, "Buyer", ref money, Now, out var bought));
        Assert.Equal(200, money);
        Assert.Equal(potion, bought);
        Assert.Equal(BuyResult.NotFound, market.Buy(potion.Id, "Buyer", ref money, Now, out _));

        Assert.Equal(20, Market.ListingFee(1000));
        Assert.Equal(Market.MinListingFee, Market.ListingFee(100));
        Assert.Equal(950, Market.Proceeds(1000));

        Assert.Null(market.Cancel(balls.Id, "Buyer"));
        Assert.Equal([balls], market.Expire(Now.AddDays(3)));
        Assert.Empty(market.Listings);
    }
}
