using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;

namespace InfiniteRoutes.Tests;

/// <summary>The player menu's server side: market, trades, friends and blocks, mail, direct messages.</summary>
public class SocialTests
{
    static SocialAction Act(string verb, params (string Key, string Value)[] args) => new(verb, args.ToDictionary(a => a.Key, a => a.Value));

    /// <summary>Two players (each with the starter game: ₽3,000, Snivy, 10 Poké Balls, 5 Potions …) on one server.</summary>
    static async Task<(TestServer Server, TestClient A, TestClient B)> Two()
    {
        var server = await TestServer.Start(channels: 1, rules: AdminChatTests.Rules());
        return (server, await Join(server, "Ann"), await Join(server, "Ben"));
    }

    static async Task<TestClient> Join(TestServer server, string name)
    {
        var (token, character) = await server.NewPlayer(name);
        var client = await TestClient.Connect(server.GamePort);
        await client.Send(new Hello(Protocol.Version, token, character));
        await client.Expect<Welcome>();
        await client.Expect<StateSnapshot>();
        await client.Expect<SocialBadges>();
        return client;
    }

    /// <summary>Does something and returns the server's answer for that part of the menu.</summary>
    static async Task<SocialReply> Do(TestClient c, SocialAction action)
    {
        c.Drain<SocialReply>();
        await c.Send(action);
        return await c.Expect<SocialReply>(r => r.Topic != "" || !r.Ok);
    }

    /// <summary>The game as it is after the next change the server sends.</summary>
    static async Task<SaveGame> NextState(TestClient c) => SaveGame.FromJson((await c.Expect<StateSnapshot>()).Json)!;

    static int Count(SaveGame s, int item) => s.Bag.Where(b => b.Item == item).Sum(b => b.Count);

    static async Task<LetterRow> Letter(TestClient c, Func<LetterRow, bool> match)
    {
        c.Drain<MailList>();
        await c.Send(Act("mails"));
        return (await c.Expect<MailList>()).Letters.First(match);
    }

    [Fact]
    public async Task Market_listings_sell_to_the_first_buyer_and_pay_the_seller_by_mail()
    {
        var (server, ann, ben) = await Two();
        await using var _s = server;
        await using var _a = ann;
        await using var _b = ben;
        const int potion = ItemEffect.Potion;

        // Ann lists two Potions for ₽100: a ₽10 fee, the Potions leave her Bag.
        ann.Drain<StateSnapshot>();
        var listed = await Do(ann, Act("market-sell", ("item", $"{potion}"), ("count", "2"), ("price", "100"), ("days", "2")));
        Assert.True(listed.Ok, listed.Text);
        var annState = await NextState(ann);
        Assert.Equal((2990, 3), (annState.Progress.Money, Count(annState, potion)));
        Assert.Single((await ann.Expect<MarketPage>(m => m.Mine)).Listings);

        // Her last Pokémon that can fight can't be sold; her own listing can't be bought.
        Assert.False((await Do(ann, Act("market-sell", ("where", "party"), ("index", "0"), ("price", "50")))).Ok);
        await ben.Send(Act("market-search", ("text", "item")));
        var listing = (await ben.Expect<MarketPage>(m => !m.Mine)).Listings.Single();
        Assert.Equal(("AnnC", 2, 100), (listing.Seller, listing.Count, listing.Price));
        Assert.Contains("own listing", (await Do(ann, Act("market-buy", ("id", $"{listing.Id}")))).Text);

        // Ben buys: pays ₽100, the Potions come by mail; Ann gets ₽95 (5% tax) by mail.
        ben.Drain<StateSnapshot>();
        Assert.True((await Do(ben, Act("market-buy", ("id", $"{listing.Id}")))).Ok);
        Assert.Equal(2900, (await NextState(ben)).Progress.Money);
        Assert.Contains("gone", (await Do(ben, Act("market-buy", ("id", $"{listing.Id}")))).Text);
        var bought = await Letter(ben, l => l.Item == potion);
        Assert.Equal(("Global Market", 2), (bought.From, bought.Count));
        Assert.Equal(95, (await Letter(ann, l => l.Money > 0)).Money);

        // Collecting puts it in the game, once.
        ben.Drain<StateSnapshot>();
        Assert.True((await Do(ben, Act("mail-claim", ("id", $"{bought.Id}")))).Ok);
        Assert.Equal(7, Count(await NextState(ben), potion));
        Assert.False((await Do(ben, Act("mail-claim", ("id", $"{bought.Id}")))).Ok);

        // A listing taken down comes back by mail (the fee doesn't).
        Assert.True((await Do(ann, Act("market-sell", ("item", $"{potion}"), ("count", "1"), ("price", "999")))).Ok);
        var mine = (await ann.Expect<MarketPage>(m => m.Mine && m.Listings.Count == 1)).Listings[0];
        Assert.True((await Do(ann, Act("market-cancel", ("id", $"{mine.Id}")))).Ok);
        Assert.Equal(1, (await Letter(ann, l => l.Subject == "Listing taken down")).Count);
    }

    [Fact]
    public async Task Trade_offers_hold_the_goods_and_settle_both_sides_by_mail()
    {
        var (server, ann, ben) = await Two();
        await using var _s = server;
        await using var _a = ann;
        await using var _b = ben;
        const int ball = ItemEffect.PokeBall;

        // Ann offers ₽500 for 2 Poké Balls; the money is held.
        ann.Drain<StateSnapshot>();
        Assert.True((await Do(ann, Act("trade-offer", ("to", "BenC"), ("give-money", "500"), ("ask-item", $"{ball}"), ("ask-count", "2"), ("note", "for balls")))).Ok);
        Assert.Equal(2500, (await NextState(ann)).Progress.Money);
        Assert.Equal(1, (await ben.Expect<SocialBadges>(b => b.Trades > 0)).Trades);

        await ben.Send(Act("trades"));
        var offer = (await ben.Expect<TradeList>(t => t.Incoming.Count > 0)).Incoming.Single();
        Assert.Equal(("AnnC", 500, "for balls"), (offer.From, offer.GiveMoney, offer.Note));
        ben.Drain<StateSnapshot>();
        Assert.True((await Do(ben, Act("trade-accept", ("id", $"{offer.Id}")))).Ok);
        Assert.Equal(8, Count(await NextState(ben), ball));
        Assert.Equal(500, (await Letter(ben, l => l.Subject.StartsWith("Trade with"))).Money);
        Assert.Equal(2, (await Letter(ann, l => l.Subject.StartsWith("Trade with"))).Count);
        Assert.False((await Do(ben, Act("trade-accept", ("id", $"{offer.Id}")))).Ok);

        // Declined: the offer comes back. Asking for a Pokémon Ben can't spare (his only one) is refused on accepting.
        Assert.True((await Do(ann, Act("trade-offer", ("to", "BenC"), ("give-item", $"{ball}"), ("give-count", "1"), ("ask-species", "0")))).Ok);
        await ben.Send(Act("trades"));
        offer = (await ben.Expect<TradeList>(t => t.Incoming.Count > 0)).Incoming.Single();
        Assert.Contains("fight", (await Do(ben, Act("trade-accept", ("id", $"{offer.Id}"), ("where", "party"), ("index", "0")))).Text);
        Assert.True((await Do(ben, Act("trade-decline", ("id", $"{offer.Id}")))).Ok);
        Assert.Equal(1, (await Letter(ann, l => l.Subject.Contains("declined"))).Count);
    }

    [Fact]
    public async Task Friends_see_each_other_come_and_go_and_blocks_stop_everything()
    {
        var (server, ann, ben) = await Two();
        await using var _s = server;
        await using var _a = ann;

        Assert.True((await Do(ann, Act("friend-add", ("name", "BenC")))).Ok);
        Assert.Equal(1, (await ben.Expect<SocialBadges>(b => b.Requests > 0)).Requests);
        await ben.Send(Act("friends"));
        Assert.Equal("received", (await ben.Expect<FriendsList>()).Friends.Single().State);
        Assert.True((await Do(ben, Act("friend-accept", ("name", "AnnC")))).Ok);
        var list = await ben.Expect<FriendsList>(f => f.Friends.Any(x => x.State == "online"));
        Assert.Equal(("AnnC", "online"), (list.Friends[0].Name, list.Friends[0].State));

        // Ben leaves: Ann hears it; he's offline on her list.
        await ben.DisposeAsync();
        Assert.Contains("went offline", (await ann.Expect<ChatLine>(l => l.Text.Contains("BenC"))).Text);
        await ann.Send(Act("friends"));
        Assert.Equal("offline", (await ann.Expect<FriendsList>(f => f.Friends.Count == 1 && f.Friends[0].State == "offline")).Friends[0].State);

        // Ben comes back and blocks Ann: the friendship ends, and her whispers, messages, mail and offers don't reach him.
        ben = await Join(server, "Ben2");
        await using var _b = ben;
        Assert.True((await Do(ben, Act("block", ("name", "AnnC")))).Ok);
        Assert.Equal(["AnnC"], (await ben.Expect<FriendsList>(f => f.Blocked.Count > 0)).Blocked);
        Assert.True((await Do(ann, Act("friend-add", ("name", "Ben2C")))).Ok == false);
        Assert.Contains("isn't taking", (await Do(ann, Act("dm-send", ("to", "Ben2C"), ("text", "hi")))).Text);
        Assert.Contains("isn't taking", (await Do(ann, Act("mail-send", ("to", "Ben2C"), ("subject", "hi")))).Text);
        Assert.Contains("isn't taking", (await Do(ann, Act("trade-offer", ("to", "Ben2C"), ("give-money", "1")))).Text);
        await ann.Send(new Say(ChatKind.Whisper, "psst", "Ben2C"));
        Assert.Contains("isn't online", (await ann.Expect<ChatLine>(l => l.Kind == ChatKind.System && l.Text.Contains("Ben2C"))).Text);
        Assert.True((await Do(ben, Act("unblock", ("name", "AnnC")))).Ok);
    }

    [Fact]
    public async Task Mail_and_direct_messages_wait_until_read()
    {
        var (server, ann, ben) = await Two();
        await using var _s = server;
        await using var _a = ann;
        await using var _b = ben;

        // A letter with ₽100 and a Potion: taken from Ann at once, collected by Ben.
        ann.Drain<StateSnapshot>();
        Assert.True((await Do(ann, Act("mail-send", ("to", "BenC"), ("subject", "Gift"), ("body", "for you\nfrom Ann"), ("money", "100"),
            ("item", $"{ItemEffect.Potion}"), ("count", "1")))).Ok);
        Assert.Equal(2900, (await NextState(ann)).Progress.Money);
        Assert.Equal(1, (await ben.Expect<SocialBadges>(b => b.Mail > 0)).Mail);
        var letter = await Letter(ben, l => l.Subject == "Gift");
        Assert.Equal(("AnnC", "for you\nfrom Ann", 100, 1), (letter.From, letter.Body, letter.Money, letter.Count));
        Assert.Contains("Collect", (await Do(ben, Act("mail-delete", ("id", $"{letter.Id}")))).Text);
        Assert.True((await Do(ben, Act("mail-claim", ("id", $"{letter.Id}")))).Ok);
        Assert.True((await Do(ben, Act("mail-delete", ("id", $"{letter.Id}")))).Ok);
        Assert.False((await Do(ann, Act("mail-send", ("to", "BenC"), ("subject", "Too much"), ("money", "999999")))).Ok);

        // Direct messages arrive live, are kept, and count as unread until the conversation is opened.
        await ann.Send(Act("dm-send", ("to", "BenC"), ("text", "hello Ben")));
        var received = await ben.Expect<DmReceived>();
        Assert.Equal(("AnnC", "AnnC", "hello Ben"), (received.With, received.Message.From, received.Message.Text));
        Assert.Equal("BenC", (await ann.Expect<DmReceived>()).With);
        Assert.Equal(1, (await ben.Expect<SocialBadges>(b => b.Messages > 0)).Messages);
        await ben.Send(Act("dm-threads"));
        Assert.Equal(("AnnC", 1), (await ben.Expect<DmThreads>()).Threads.Select(t => (t.With, t.Unread)).Single());
        await ben.Send(Act("dm-open", ("with", "AnnC")));
        Assert.Equal(["hello Ben"], (await ben.Expect<DmConversation>()).Messages.Select(m => m.Text));
        Assert.Equal(0, (await ben.Expect<SocialBadges>(b => b.Messages == 0)).Messages);
    }
}
