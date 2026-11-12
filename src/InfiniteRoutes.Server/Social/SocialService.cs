using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;
using InfiniteRoutes.Server.Accounts;
using InfiniteRoutes.Server.Data;
using InfiniteRoutes.Server.Game;
using InfiniteRoutes.Server.World;
using Microsoft.EntityFrameworkCore;

namespace InfiniteRoutes.Server.Social;

/// <summary>
/// The player menu's server side (BUILD_SPEC §7.6–7.7): the Global Market, trade offers, friends and blocks, mail and
/// direct messages, all kept in the database so they work between players who aren't online together.
/// </summary>
/// <remarks>
/// Goods only ever move one way at a time and the database decides who got there first: whatever a player gives up is
/// taken from their game (under the world's lock, saved at once) before the database records it, and handed back if
/// the database says no. Goods for someone else — a buyer, a seller's earnings, a trade partner, returns — travel as
/// mail, collected into the game from the Mail tab (so a full party or an offline player is never a problem).
/// </remarks>
public sealed partial class SocialService(IDbContextFactory<IrDb> dbs, WorldService world, AccountService accounts,
    ILogger<SocialService> log, GameRules? rules = null)
{
    public const int MaxDm = 300, MaxOffersOut = 10, OfferDays = 3, LettersPerHour = 30;
    /// <summary>Who letters from the game itself are signed by.</summary>
    public const string MarketName = "Global Market", TradeName = "Trade Desk";

    DateTime _lastSweep = DateTime.MinValue;

    public async Task Handle(WorldPlayer p, SocialAction action)
    {
        var a = action.Args ?? new Dictionary<string, string>();
        try
        {
            await Sweep();
            switch (action.Verb)
            {
                case "market-search": await MarketSearch(p, a); break;
                case "market-mine": await MarketMine(p); break;
                case "market-sell": await MarketSell(p, a); break;
                case "market-buy": await MarketBuy(p, a); break;
                case "market-cancel": await MarketCancel(p, a); break;
                case "trades": await Trades(p); break;
                case "trade-offer": await TradeOffer(p, a); break;
                case "trade-accept": await TradeAccept(p, a); break;
                case "trade-decline": await TradeClose(p, a, decline: true); break;
                case "trade-cancel": await TradeClose(p, a, decline: false); break;
                case "friends": await Friends(p); break;
                case "friend-add": await FriendAdd(p, a); break;
                case "friend-accept": await FriendAccept(p, a); break;
                case "friend-remove": await FriendRemove(p, a); break;
                case "block": await Block(p, a, on: true); break;
                case "unblock": await Block(p, a, on: false); break;
                case "mails": await Mails(p); break;
                case "mail-send": await MailSend(p, a); break;
                case "mail-read": await MailRead(p, a); break;
                case "mail-claim": await MailClaim(p, a); break;
                case "mail-delete": await MailDelete(p, a); break;
                case "dm-send": await DmSend(p, a); break;
                case "dm-threads": await DmThreadList(p); break;
                case "dm-open": await DmOpen(p, a); break;
                case "badges": await Badges(p); break;
                default: Reply(p, "", false, "Unknown action."); break;
            }
        }
        catch (DbUpdateException e)
        {
            log.LogWarning(e, "{Verb} for {Name} failed", action.Verb, p.Name);
            Reply(p, "", false, "That didn't go through; try again.");
        }
    }

    // ---- Joining and leaving ----------------------------------------------------------------------------------------

    /// <summary>A player came online: their blocks apply to whispers, they hear what's waiting, friends hear they're on.</summary>
    public async Task Joined(WorldPlayer p)
    {
        await using (var db = await dbs.CreateDbContextAsync())
        {
            var blocked = await db.Blocks.AsNoTracking().Where(b => b.CharacterId == p.CharacterId).Select(b => b.BlockedId).ToListAsync();
            world.SetBlocked(p, blocked);
        }
        await Sweep();
        await Badges(p);
        if (!(p.Elevated && p.Invisible))
            await TellFriends(p, $"{p.Name} is now online.");
    }

    public async Task Left(WorldPlayer p)
    {
        if (!p.Invisible)
            await TellFriends(p, $"{p.Name} went offline.");
    }

    async Task TellFriends(WorldPlayer p, string text)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var me = p.CharacterId;
        var ids = await db.Friendships.AsNoTracking().Where(f => f.Accepted && (f.From == me || f.To == me))
            .Select(f => f.From == me ? f.To : f.From).ToListAsync();
        foreach (var id in ids)
            world.OnlineCharacter(id)?.Link.Send(new ChatLine(ChatKind.System, "", text));
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    static void Reply(WorldPlayer p, string topic, bool ok, string text) => p.Link.Send(new SocialReply(topic, ok, text));

    static string Arg(IReadOnlyDictionary<string, string> a, string key) => a.TryGetValue(key, out var v) ? v.Trim() : "";

    static int Int(IReadOnlyDictionary<string, string> a, string key, int fallback = 0) =>
        int.TryParse(Arg(a, key), out var v) ? v : fallback;

    static string Clean(string text, int max)
    {
        var clean = new string(text.Where(c => !char.IsControl(c) || c == '\n').ToArray()).Trim();
        return clean.Length > max ? clean[..max] : clean;
    }

    string ItemName(int id) => rules?.Data.ItemName(id) ?? $"Item {id}";

    string PokemonName(string? json)
    {
        if (json is null || SaveGame.PokemonFromJson(json) is not { } save)
            return "a Pokémon";
        var species = rules?.Data.HasSpecies(save.Species) == true ? rules.Data.Species(save.Species).Name : $"#{save.Species}";
        return save.Nickname is { } nick ? $"{nick} ({species} Lv{save.Level})" : $"{species} Lv{save.Level}";
    }

    /// <summary>"₽500, Potion ×3 and Snivy Lv5" — what a bundle of goods is, for letters and replies.</summary>
    string Goods(int money, int item, int count, string? pokemon)
    {
        var parts = new List<string>();
        if (money > 0)
            parts.Add($"₽{money:N0}");
        if (item > 0 && count > 0)
            parts.Add($"{ItemName(item)} ×{count}");
        if (pokemon is not null)
            parts.Add(PokemonName(pokemon));
        return parts.Count == 0 ? "nothing" : parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts[..^1])} and {parts[^1]}";
    }

    /// <summary>A character by name, or null (with a reply saying so).</summary>
    async Task<Character?> Someone(WorldPlayer p, string topic, string name)
    {
        var c = name.Length == 0 ? null : await accounts.CharacterNamed(name);
        if (c is null)
            Reply(p, topic, false, $"There's no player called {name}.");
        else if (c.Id == p.CharacterId)
        {
            Reply(p, topic, false, "That's you.");
            return null;
        }
        return c;
    }

    /// <summary>Whether <paramref name="owner"/> has blocked <paramref name="other"/>.</summary>
    static Task<bool> Blocks(IrDb db, long owner, long other) =>
        db.Blocks.AnyAsync(b => b.CharacterId == owner && b.BlockedId == other);

    /// <summary>Takes <paramref name="count"/> of an item from the Bag; false (and nothing taken) when there aren't enough.</summary>
    static bool TakeItems(Bag bag, int item, int count)
    {
        if (count <= 0 || bag.Count(item) < count)
            return false;
        for (var i = 0; i < count; i++)
            bag.Take(item);
        return true;
    }

    /// <summary>
    /// Takes a Pokémon out of the game to give away (<paramref name="where"/> "party" or "box"); its JSON, or why not.
    /// The party keeps at least one Pokémon that can fight.
    /// </summary>
    static (string? Json, string? Error) TakePokemon(GameState s, string where, int index)
    {
        var list = where == "box" ? s.Box : s.Party;
        if (index < 0 || index >= list.Count)
            return (null, "There's no Pokémon there.");
        var mon = list[index];
        if (where != "box" && !s.Party.Any(m => m != mon && !m.Fainted))
            return (null, "You need to keep a Pokémon that can fight in your party.");
        list.RemoveAt(index);
        return (SaveGame.PokemonJson(mon.ToSave()), null);
    }

    /// <summary>Changes an online player's game; null when it worked, else why not (with nothing changed).</summary>
    string? Edit(WorldPlayer p, Func<GameState, string?> edit)
    {
        var (_, _, error) = world.EditState(p, edit);
        return error;
    }

    /// <summary>Puts goods back into a player's game (an undo after the database said no).</summary>
    void GiveBack(WorldPlayer p, int money, int item, int count, string? pokemon) =>
        world.EditState(p, s =>
        {
            s.Progress.Money += money;
            if (item > 0)
                s.Bag.Add(item, count);
            if (pokemon is not null && rules is not null && SaveGame.PokemonFromJson(pokemon) is { } save && Pokemon.FromSave(rules.Data, save) is { } mon)
                s.Keep(mon);
            return null;
        });

    static Letter LetterFrom(string from, long fromId, long to, string subject, string body, int money = 0, int item = 0, int count = 0, string? pokemon = null) =>
        new()
        {
            To = to, FromId = fromId, From = from, Subject = subject, Body = body, Sent = DateTime.UtcNow,
            Money = Math.Max(0, money), Item = count > 0 ? item : 0, Count = item > 0 ? count : 0, Pokemon = pokemon,
        };

    /// <summary>Tells a player, if they're online, that something arrived (and refreshes their counts).</summary>
    async Task Nudge(long characterId, string topic, string text)
    {
        if (world.OnlineCharacter(characterId) is not { } p)
            return;
        Reply(p, topic, true, text);
        await Badges(p);
    }

    async Task Badges(WorldPlayer p)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var me = p.CharacterId;
        var now = DateTime.UtcNow;
        p.Link.Send(new SocialBadges(
            await db.Letters.CountAsync(l => l.To == me && !l.Read),
            await db.DirectMessages.CountAsync(m => m.To == me && !m.Read),
            await db.Friendships.CountAsync(f => f.To == me && !f.Accepted),
            await db.Trades.CountAsync(t => t.To == me && t.Expires > now)));
    }

    /// <summary>At most once a minute: expired listings and trade offers go back to their owners by mail.</summary>
    async Task Sweep()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastSweep).TotalSeconds < 60)
            return;
        _lastSweep = now;
        await using var db = await dbs.CreateDbContextAsync();
        foreach (var l in await db.Listings.Where(l => l.Expires <= now).ToListAsync())
        {
            db.Listings.Remove(l);
            db.Letters.Add(LetterFrom(MarketName, 0, l.Seller, "Listing expired",
                $"Nobody bought your {Goods(0, l.Item, l.Count, l.Pokemon)} before the listing ended, so here it is back.", 0, l.Item, l.Count, l.Pokemon));
        }
        foreach (var t in await db.Trades.Where(t => t.Expires <= now).ToListAsync())
        {
            db.Trades.Remove(t);
            db.Letters.Add(LetterFrom(TradeName, 0, t.From, $"Trade with {t.ToName} expired",
                $"{t.ToName} didn't answer your offer in time; here's what you offered.", t.GiveMoney, t.GiveItem, t.GiveCount, t.GivePokemon));
        }
        await db.SaveChangesAsync();
    }
}
