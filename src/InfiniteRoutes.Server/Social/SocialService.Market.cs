using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;
using InfiniteRoutes.Server.Data;
using InfiniteRoutes.Server.World;
using Microsoft.EntityFrameworkCore;

namespace InfiniteRoutes.Server.Social;

/// <summary>
/// The Global Market (fixed prices for 1–7 days; a 2% listing fee up front and 5% tax on the sale, as in
/// <see cref="Market"/>) and trade offers between two players.
/// </summary>
public sealed partial class SocialService
{
    static ListingRow Row(Listing l) => new(l.Id, l.SellerName, l.Item, l.Count, l.Pokemon, l.Price, l.Listed, l.Expires);

    async Task MarketSearch(WorldPlayer p, IReadOnlyDictionary<string, string> a)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var q = db.Listings.AsNoTracking().Where(l => l.Expires > now);
        var text = Arg(a, "text").ToLowerInvariant();
        if (text.Length > 0)
            q = q.Where(l => l.Name.Contains(text) || l.SellerName.ToLower().Contains(text));
        q = Arg(a, "kind") switch
        {
            "item" => q.Where(l => l.Pokemon == null),
            "pokemon" => q.Where(l => l.Pokemon != null),
            _ => q,
        };
        if (Int(a, "max") is > 0 and var max)
            q = q.Where(l => l.Price <= max);
        q = Arg(a, "sort") switch
        {
            "price" => q.OrderBy(l => l.Price),
            "price-desc" => q.OrderByDescending(l => l.Price),
            "ending" => q.OrderBy(l => l.Expires),
            _ => q.OrderByDescending(l => l.Id),
        };
        p.Link.Send(new MarketPage((await q.Take(100).ToListAsync()).Select(Row).ToList(), false));
    }

    async Task MarketMine(WorldPlayer p)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var mine = await db.Listings.AsNoTracking().Where(l => l.Seller == p.CharacterId).OrderByDescending(l => l.Id).ToListAsync();
        p.Link.Send(new MarketPage(mine.Select(Row).ToList(), true));
    }

    /// <summary>Lists an item stack (item, count) or a Pokémon (where, index) at a price for 1–7 days.</summary>
    async Task MarketSell(WorldPlayer p, IReadOnlyDictionary<string, string> a)
    {
        const string topic = "market";
        var price = Int(a, "price");
        var days = Int(a, "days", 3);
        if (price is <= 0 or > Market.MaxPrice)
        {
            Reply(p, topic, false, $"Prices run from ₽1 to ₽{Market.MaxPrice:N0}.");
            return;
        }
        if (days is < Market.MinDays or > Market.MaxDays)
        {
            Reply(p, topic, false, $"Listings last {Market.MinDays}–{Market.MaxDays} days.");
            return;
        }
        await using var db = await dbs.CreateDbContextAsync();
        if (await db.Listings.CountAsync(l => l.Seller == p.CharacterId) >= Market.MaxListingsPerSeller)
        {
            Reply(p, topic, false, $"You can have at most {Market.MaxListingsPerSeller} listings.");
            return;
        }
        var pokemon = Arg(a, "where") is "party" or "box";
        int item = Int(a, "item"), count = Int(a, "count", 1);
        var fee = Market.ListingFee(price);
        string? json = null;
        var error = Edit(p, s =>
        {
            if (s.Progress.Money < fee)
                return $"The listing fee is ₽{fee:N0}; you have ₽{s.Progress.Money:N0}.";
            if (pokemon)
            {
                var (taken, why) = TakePokemon(s, Arg(a, "where"), Int(a, "index", -1));
                if (taken is null)
                    return why;
                json = taken;
            }
            else if (item <= 0 || !TakeItems(s.Bag, item, count))
                return "You don't have that many.";
            s.Progress.Money -= fee;
            return null;
        });
        if (error is not null)
        {
            Reply(p, topic, false, error);
            return;
        }
        var now = DateTime.UtcNow;
        var name = pokemon ? PokemonName(json) : ItemName(item);
        db.Listings.Add(new Listing
        {
            Seller = p.CharacterId, SellerName = p.Name, Item = pokemon ? 0 : item, Count = pokemon ? 0 : count, Pokemon = json,
            Name = name.ToLowerInvariant(), Price = price, Listed = now, Expires = now.AddDays(days),
        });
        await db.SaveChangesAsync();
        Reply(p, topic, true, $"Listed {(pokemon ? name : $"{name} ×{count}")} for ₽{price:N0} ({days} days). The fee was ₽{fee:N0}.");
        await MarketMine(p);
    }

    async Task MarketBuy(WorldPlayer p, IReadOnlyDictionary<string, string> a)
    {
        const string topic = "market";
        var id = (long)Int(a, "id");
        await using var db = await dbs.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var l = await db.Listings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.Expires > now);
        if (l is null)
        {
            Reply(p, topic, false, "That listing is gone.");
            return;
        }
        if (l.Seller == p.CharacterId)
        {
            Reply(p, topic, false, "That's your own listing.");
            return;
        }
        // Pay first; the first buyer the database lets delete the listing has it, and anyone else gets their money back.
        if (Edit(p, s =>
            {
                if (s.Progress.Money < l.Price)
                    return $"It costs ₽{l.Price:N0}; you have ₽{s.Progress.Money:N0}.";
                s.Progress.Money -= l.Price;
                return null;
            }) is { } error)
        {
            Reply(p, topic, false, error);
            return;
        }
        if (await db.Listings.Where(x => x.Id == id).ExecuteDeleteAsync() == 0)
        {
            GiveBack(p, l.Price, 0, 0, null);
            Reply(p, topic, false, "Someone else bought it first.");
            return;
        }
        var what = Goods(0, l.Item, l.Count, l.Pokemon);
        var proceeds = Market.Proceeds(l.Price);
        db.Letters.Add(LetterFrom(MarketName, 0, p.CharacterId, $"You bought {what}",
            $"You bought {what} from {l.SellerName} for ₽{l.Price:N0}. Collect it below.", 0, l.Item, l.Count, l.Pokemon));
        db.Letters.Add(LetterFrom(MarketName, 0, l.Seller, $"Sold: {what}",
            $"{p.Name} bought your {what} for ₽{l.Price:N0}. After the 5% sales tax you receive ₽{proceeds:N0}.", proceeds));
        await db.SaveChangesAsync();
        log.LogInformation("{Buyer} bought listing {Id} ({What}) from {Seller} for {Price}", p.Name, id, what, l.SellerName, l.Price);
        Reply(p, topic, true, $"Bought {what} for ₽{l.Price:N0}. It's in your mail.");
        await Badges(p);
        await Nudge(l.Seller, topic, $"{p.Name} bought your {what}. Your earnings are in your mail.");
    }

    async Task MarketCancel(WorldPlayer p, IReadOnlyDictionary<string, string> a)
    {
        var id = (long)Int(a, "id");
        await using var db = await dbs.CreateDbContextAsync();
        var l = await db.Listings.FirstOrDefaultAsync(x => x.Id == id && x.Seller == p.CharacterId);
        if (l is null || await db.Listings.Where(x => x.Id == id).ExecuteDeleteAsync() == 0)
        {
            Reply(p, "market", false, "That listing is gone (sold or expired?).");
            return;
        }
        db.Letters.Add(LetterFrom(MarketName, 0, p.CharacterId, "Listing taken down",
            $"You took down your listing; here's your {Goods(0, l.Item, l.Count, l.Pokemon)} back (the fee isn't refunded).", 0, l.Item, l.Count, l.Pokemon));
        await db.SaveChangesAsync();
        Reply(p, "market", true, "Taken down; the goods are back in your mail.");
        await Badges(p);
        await MarketMine(p);
    }

    // ---- Trade offers -----------------------------------------------------------------------------------------------

    static TradeRow Row(TradeOffer t) => new(t.Id, t.FromName, t.ToName, t.GiveMoney, t.GiveItem, t.GiveCount, t.GivePokemon,
        t.AskMoney, t.AskItem, t.AskCount, t.AskSpecies, t.Note, t.Expires);

    async Task Trades(WorldPlayer p)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var me = p.CharacterId;
        var all = await db.Trades.AsNoTracking().Where(t => (t.To == me || t.From == me) && t.Expires > now).OrderByDescending(t => t.Id).ToListAsync();
        p.Link.Send(new TradeList(all.Where(t => t.To == me).Select(Row).ToList(), all.Where(t => t.From == me).Select(Row).ToList()));
    }

    /// <summary>
    /// Offers another player money, items and/or a Pokémon (held by the server meanwhile) for money, items and/or a
    /// Pokémon of theirs (any, or a species).
    /// </summary>
    async Task TradeOffer(WorldPlayer p, IReadOnlyDictionary<string, string> a)
    {
        const string topic = "trade";
        if (await Someone(p, topic, Arg(a, "to")) is not { } them)
            return;
        await using var db = await dbs.CreateDbContextAsync();
        if (await Blocks(db, them.Id, p.CharacterId))
        {
            Reply(p, topic, false, $"{them.Name} isn't taking offers from you.");
            return;
        }
        var now = DateTime.UtcNow;
        if (await db.Trades.CountAsync(t => t.From == p.CharacterId && t.Expires > now) >= MaxOffersOut)
        {
            Reply(p, topic, false, $"You can have at most {MaxOffersOut} offers waiting.");
            return;
        }
        int giveMoney = Math.Max(0, Int(a, "give-money")), giveItem = Int(a, "give-item"), giveCount = giveItem > 0 ? Math.Max(1, Int(a, "give-count", 1)) : 0;
        int askMoney = Math.Max(0, Int(a, "ask-money")), askItem = Int(a, "ask-item"), askCount = askItem > 0 ? Math.Max(1, Int(a, "ask-count", 1)) : 0;
        var askSpecies = Math.Max(-1, Int(a, "ask-species", -1));
        var givesPokemon = Arg(a, "give-where") is "party" or "box";
        if (askSpecies > 0 && rules?.Data.HasSpecies(askSpecies) != true)
        {
            Reply(p, topic, false, "There's no such species.");
            return;
        }
        if (giveMoney == 0 && giveItem <= 0 && !givesPokemon)
        {
            Reply(p, topic, false, "Offer something: money, an item or a Pokémon.");
            return;
        }
        if (giveMoney > Market.MaxPrice || askMoney > Market.MaxPrice)
        {
            Reply(p, topic, false, $"At most ₽{Market.MaxPrice:N0} each way.");
            return;
        }
        string? json = null;
        if (Edit(p, s =>
            {
                if (s.Progress.Money < giveMoney)
                    return $"You have ₽{s.Progress.Money:N0}.";
                if (giveItem > 0 && s.Bag.Count(giveItem) < giveCount)
                    return "You don't have that many.";
                if (givesPokemon)
                {
                    var (taken, why) = TakePokemon(s, Arg(a, "give-where"), Int(a, "give-index", -1));
                    if (taken is null)
                        return why;
                    json = taken;
                }
                if (giveItem > 0)
                    TakeItems(s.Bag, giveItem, giveCount);
                s.Progress.Money -= giveMoney;
                return null;
            }) is { } error)
        {
            Reply(p, topic, false, error);
            return;
        }
        var offer = new TradeOffer
        {
            From = p.CharacterId, FromName = p.Name, To = them.Id, ToName = them.Name,
            GiveMoney = giveMoney, GiveItem = Math.Max(0, giveItem), GiveCount = giveCount, GivePokemon = json,
            AskMoney = askMoney, AskItem = Math.Max(0, askItem), AskCount = askCount, AskSpecies = askSpecies,
            Note = Clean(Arg(a, "note"), 120), Created = now, Expires = now.AddDays(OfferDays),
        };
        db.Trades.Add(offer);
        await db.SaveChangesAsync();
        Reply(p, topic, true, $"Offered {them.Name} {Goods(giveMoney, giveItem, giveCount, json)}. It stays open {OfferDays} days.");
        await Trades(p);
        await Nudge(them.Id, topic, $"{p.Name} offered you a trade (Market → Trade).");
    }

    /// <summary>Accepts an offer: gives what it asks (a Pokémon from where/index when it asks for one) and gets what it offers, by mail.</summary>
    async Task TradeAccept(WorldPlayer p, IReadOnlyDictionary<string, string> a)
    {
        const string topic = "trade";
        var id = (long)Int(a, "id");
        await using var db = await dbs.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var t = await db.Trades.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.To == p.CharacterId && x.Expires > now);
        if (t is null)
        {
            Reply(p, topic, false, "That offer is gone.");
            return;
        }
        string? json = null;
        if (Edit(p, s =>
            {
                if (s.Progress.Money < t.AskMoney)
                    return $"It asks ₽{t.AskMoney:N0}; you have ₽{s.Progress.Money:N0}.";
                if (t.AskItem > 0 && s.Bag.Count(t.AskItem) < t.AskCount)
                    return $"It asks {ItemName(t.AskItem)} ×{t.AskCount}.";
                if (t.AskSpecies >= 0)
                {
                    var where = Arg(a, "where");
                    var index = Int(a, "index", -1);
                    var list = where == "box" ? s.Box : s.Party;
                    if (index < 0 || index >= list.Count)
                        return "Pick the Pokémon to send.";
                    if (t.AskSpecies > 0 && list[index].Species.Id != t.AskSpecies)
                        return $"It asks for a {rules?.Data.Species(t.AskSpecies).Name}.";
                    var (taken, why) = TakePokemon(s, where, index);
                    if (taken is null)
                        return why;
                    json = taken;
                }
                if (t.AskItem > 0)
                    TakeItems(s.Bag, t.AskItem, t.AskCount);
                s.Progress.Money -= t.AskMoney;
                return null;
            }) is { } error)
        {
            Reply(p, topic, false, error);
            return;
        }
        if (await db.Trades.Where(x => x.Id == id).ExecuteDeleteAsync() == 0)
        {
            GiveBack(p, t.AskMoney, t.AskItem, t.AskCount, json);
            Reply(p, topic, false, "That offer was withdrawn.");
            return;
        }
        var got = Goods(t.GiveMoney, t.GiveItem, t.GiveCount, t.GivePokemon);
        var gave = Goods(t.AskMoney, t.AskItem, t.AskCount, json);
        db.Letters.Add(LetterFrom(TradeName, 0, p.CharacterId, $"Trade with {t.FromName}",
            $"You traded {gave} for {got}. Collect it below.", t.GiveMoney, t.GiveItem, t.GiveCount, t.GivePokemon));
        db.Letters.Add(LetterFrom(TradeName, 0, t.From, $"Trade with {p.Name}",
            $"{p.Name} accepted your offer: you get {gave} for your {got}. Collect it below.", t.AskMoney, t.AskItem, t.AskCount, json));
        await db.SaveChangesAsync();
        log.LogInformation("Trade {Id}: {From} gave {Got}, {To} gave {Gave}", id, t.FromName, got, p.Name, gave);
        Reply(p, topic, true, $"Traded! {got} is in your mail.");
        await Trades(p);
        await Badges(p);
        await Nudge(t.From, topic, $"{p.Name} accepted your trade. It's in your mail.");
    }

    /// <summary>Declines an offer made to you, or withdraws one you made: what was offered goes back to its owner by mail.</summary>
    async Task TradeClose(WorldPlayer p, IReadOnlyDictionary<string, string> a, bool decline)
    {
        const string topic = "trade";
        var id = (long)Int(a, "id");
        var me = p.CharacterId;
        await using var db = await dbs.CreateDbContextAsync();
        var t = await db.Trades.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && (decline ? x.To == me : x.From == me));
        if (t is null || await db.Trades.Where(x => x.Id == id).ExecuteDeleteAsync() == 0)
        {
            Reply(p, topic, false, "That offer is gone.");
            return;
        }
        db.Letters.Add(LetterFrom(TradeName, 0, t.From, decline ? $"{t.ToName} declined your trade" : "Trade offer withdrawn",
            $"Here's what you offered back: {Goods(t.GiveMoney, t.GiveItem, t.GiveCount, t.GivePokemon)}.", t.GiveMoney, t.GiveItem, t.GiveCount, t.GivePokemon));
        await db.SaveChangesAsync();
        Reply(p, topic, true, decline ? "Declined." : "Withdrawn; your goods are back in your mail.");
        await Trades(p);
        await Badges(p);
        if (decline)
            await Nudge(t.From, topic, $"{p.Name} declined your trade. Your goods are back in your mail.");
    }
}
