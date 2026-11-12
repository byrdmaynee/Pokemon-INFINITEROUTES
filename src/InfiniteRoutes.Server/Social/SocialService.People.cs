using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;
using InfiniteRoutes.Server.Data;
using InfiniteRoutes.Server.World;
using Microsoft.EntityFrameworkCore;

namespace InfiniteRoutes.Server.Social;

/// <summary>Friends and blocks, mail, and direct messages.</summary>
public sealed partial class SocialService
{
    // ---- Friends ----------------------------------------------------------------------------------------------------

    async Task Friends(WorldPlayer p)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var me = p.CharacterId;
        var links = await db.Friendships.AsNoTracking().Where(f => f.From == me || f.To == me).ToListAsync();
        var ids = links.Select(f => f.From == me ? f.To : f.From).ToList();
        var people = await db.Characters.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id);
        var rows = new List<FriendRow>();
        foreach (var f in links)
        {
            if (!people.TryGetValue(f.From == me ? f.To : f.From, out var c))
                continue;
            var online = world.OnlineCharacter(c.Id) is { } o && !(o.Elevated && o.Invisible) ? o : null;
            var state = !f.Accepted ? (f.From == me ? "sent" : "received") : online is not null ? "online" : "offline";
            rows.Add(new FriendRow(c.Name, state, f.Accepted && online is not null ? online.Zone : 0, online?.Channel ?? 0, c.LastSeen));
        }
        var order = new[] { "received", "online", "offline", "sent" };
        var blocked = await db.Blocks.AsNoTracking().Where(b => b.CharacterId == me).Select(b => b.BlockedName).ToListAsync();
        p.Link.Send(new FriendsList(rows.OrderBy(r => Array.IndexOf(order, r.State)).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            blocked.Order(StringComparer.OrdinalIgnoreCase).ToList()));
    }

    async Task FriendAdd(WorldPlayer p, IReadOnlyDictionary<string, string> a)
    {
        const string topic = "friends";
        if (await Someone(p, topic, Arg(a, "name")) is not { } them)
            return;
        await using var db = await dbs.CreateDbContextAsync();
        var me = p.CharacterId;
        var existing = await db.Friendships.FirstOrDefaultAsync(f => (f.From == me && f.To == them.Id) || (f.From == them.Id && f.To == me));
        if (existing is { Accepted: true })
            Reply(p, topic, false, $"{them.Name} is already your friend.");
        else if (existing is not null && existing.From == me)
            Reply(p, topic, false, $"You've already asked {them.Name}.");
        else if (existing is not null)
        {
            // They asked first: adding them back accepts.
            existing.Accepted = true;
            await db.SaveChangesAsync();
            Reply(p, topic, true, $"You and {them.Name} are friends now.");
            await Nudge(them.Id, topic, $"{p.Name} accepted your friend request.");
        }
        else if (await Blocks(db, them.Id, me) || await Blocks(db, me, them.Id))
            Reply(p, topic, false, $"You can't add {them.Name}.");
        else if (await db.Friendships.CountAsync(f => f.From == me || f.To == me) >= FriendList.MaxFriends)
            Reply(p, topic, false, $"Your friends list is full ({FriendList.MaxFriends}).");
        else
        {
            db.Friendships.Add(new Friendship { From = me, To = them.Id, Created = DateTime.UtcNow });
            await db.SaveChangesAsync();
            Reply(p, topic, true, $"Friend request sent to {them.Name}.");
            await Nudge(them.Id, topic, $"{p.Name} wants to be friends (Friends tab).");
        }
        await Friends(p);
    }

    async Task FriendAccept(WorldPlayer p, IReadOnlyDictionary<string, string> a)
    {
        const string topic = "friends";
        if (await Someone(p, topic, Arg(a, "name")) is not { } them)
            return;
        await using var db = await dbs.CreateDbContextAsync();
        var request = await db.Friendships.FirstOrDefaultAsync(f => f.From == them.Id && f.To == p.CharacterId && !f.Accepted);
        if (request is null)
            Reply(p, topic, false, $"{them.Name} hasn't asked to be friends.");
        else
        {
            request.Accepted = true;
            await db.SaveChangesAsync();
            Reply(p, topic, true, $"You and {them.Name} are friends now.");
            await Nudge(them.Id, topic, $"{p.Name} accepted your friend request.");
        }
        await Friends(p);
        await Badges(p);
    }

    /// <summary>Removes a friend, or declines or withdraws a request.</summary>
    async Task FriendRemove(WorldPlayer p, IReadOnlyDictionary<string, string> a)
    {
        if (await Someone(p, "friends", Arg(a, "name")) is not { } them)
            return;
        await using var db = await dbs.CreateDbContextAsync();
        var me = p.CharacterId;
        var removed = await db.Friendships.Where(f => (f.From == me && f.To == them.Id) || (f.From == them.Id && f.To == me)).ExecuteDeleteAsync();
        Reply(p, "friends", removed > 0, removed > 0 ? $"{them.Name} is off your list." : $"{them.Name} wasn't on your list.");
        await Friends(p);
        await Badges(p);
    }

    /// <summary>Blocks someone (no whispers, messages, mail, requests or offers from them; ends any friendship) or unblocks them.</summary>
    async Task Block(WorldPlayer p, IReadOnlyDictionary<string, string> a, bool on)
    {
        if (await Someone(p, "friends", Arg(a, "name")) is not { } them)
            return;
        await using var db = await dbs.CreateDbContextAsync();
        var me = p.CharacterId;
        if (on)
        {
            if (!await Blocks(db, me, them.Id))
                db.Blocks.Add(new Data.Block { CharacterId = me, BlockedId = them.Id, BlockedName = them.Name });
            await db.Friendships.Where(f => (f.From == me && f.To == them.Id) || (f.From == them.Id && f.To == me)).ExecuteDeleteAsync();
            await db.SaveChangesAsync();
            Reply(p, "friends", true, $"Blocked {them.Name}.");
        }
        else
        {
            await db.Blocks.Where(b => b.CharacterId == me && b.BlockedId == them.Id).ExecuteDeleteAsync();
            Reply(p, "friends", true, $"Unblocked {them.Name}.");
        }
        world.SetBlocked(p, await db.Blocks.AsNoTracking().Where(b => b.CharacterId == me).Select(b => b.BlockedId).ToListAsync());
        await Friends(p);
    }

    // ---- Mail -------------------------------------------------------------------------------------------------------

    static LetterRow Row(Letter l) => new(l.Id, l.From, l.Subject, l.Body, l.Sent, l.Read, l.Money, l.Item, l.Count, l.Pokemon, l.Claimed);

    async Task Mails(WorldPlayer p)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var letters = await db.Letters.AsNoTracking().Where(l => l.To == p.CharacterId).OrderByDescending(l => l.Id).Take(100).ToListAsync();
        p.Link.Send(new MailList(letters.Select(Row).ToList()));
    }

    /// <summary>Sends a letter, with money and/or an item stack attached if wanted (Pokémon travel by trade or the market).</summary>
    async Task MailSend(WorldPlayer p, IReadOnlyDictionary<string, string> a)
    {
        const string topic = "mail";
        if (await Someone(p, topic, Arg(a, "to")) is not { } them)
            return;
        var subject = Clean(Arg(a, "subject"), Mailbox.MaxSubjectLength);
        var body = Clean(a.TryGetValue("body", out var b) ? b : "", Mailbox.MaxBodyLength);
        if (subject.Length == 0)
        {
            Reply(p, topic, false, "Give the letter a subject.");
            return;
        }
        await using var db = await dbs.CreateDbContextAsync();
        if (await Blocks(db, them.Id, p.CharacterId))
        {
            Reply(p, topic, false, $"{them.Name} isn't taking mail from you.");
            return;
        }
        var hour = DateTime.UtcNow.AddHours(-1);
        if (await db.Letters.CountAsync(l => l.FromId == p.CharacterId && l.Sent > hour) >= LettersPerHour)
        {
            Reply(p, topic, false, "You've sent a lot of mail; try again later.");
            return;
        }
        int money = Math.Max(0, Int(a, "money")), item = Int(a, "item"), count = item > 0 ? Math.Max(1, Int(a, "count", 1)) : 0;
        if (money > 0 || item > 0)
        {
            if (Edit(p, s =>
                {
                    if (s.Progress.Money < money)
                        return $"You have ₽{s.Progress.Money:N0}.";
                    if (item > 0 && !TakeItems(s.Bag, item, count))
                        return "You don't have that many.";
                    s.Progress.Money -= money;
                    return null;
                }) is { } error)
            {
                Reply(p, topic, false, error);
                return;
            }
        }
        db.Letters.Add(LetterFrom(p.Name, p.CharacterId, them.Id, subject, body, money, Math.Max(0, item), count));
        await db.SaveChangesAsync();
        Reply(p, topic, true, money > 0 || item > 0 ? $"Sent to {them.Name} with {Goods(money, item, count, null)}." : $"Sent to {them.Name}.");
        await Nudge(them.Id, topic, $"New mail from {p.Name}: {subject}");
    }

    async Task MailRead(WorldPlayer p, IReadOnlyDictionary<string, string> a)
    {
        var id = (long)Int(a, "id");
        await using var db = await dbs.CreateDbContextAsync();
        await db.Letters.Where(l => l.Id == id && l.To == p.CharacterId).ExecuteUpdateAsync(u => u.SetProperty(l => l.Read, true));
        await Badges(p);
    }

    /// <summary>Collects a letter's attachments into the game (a Pokémon joins the party, or the PC when it's full).</summary>
    async Task MailClaim(WorldPlayer p, IReadOnlyDictionary<string, string> a)
    {
        const string topic = "mail";
        var id = (long)Int(a, "id");
        await using var db = await dbs.CreateDbContextAsync();
        var l = await db.Letters.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.To == p.CharacterId);
        if (l is null || l.Claimed || (l.Money <= 0 && l.Item <= 0 && l.Pokemon is null))
        {
            Reply(p, topic, false, "There's nothing to collect.");
            return;
        }
        // Mark it collected first: only one collect can win.
        if (await db.Letters.Where(x => x.Id == id && !x.Claimed).ExecuteUpdateAsync(u => u.SetProperty(x => x.Claimed, true).SetProperty(x => x.Read, true)) == 0)
        {
            Reply(p, topic, false, "Already collected.");
            return;
        }
        var where = "";
        var error = Edit(p, s =>
        {
            Pokemon? mon = null;
            if (l.Pokemon is not null && (rules is null || SaveGame.PokemonFromJson(l.Pokemon) is not { } save || (mon = Pokemon.FromSave(rules.Data, save)) is null))
                return "That Pokémon can't be read.";
            if (mon is not null && s.Party.Count >= GameState.PartySize && s.Box.Count >= GameState.BoxCapacity)
                return "Your party and PC are full.";
            s.Progress.Money += l.Money;
            if (l.Item > 0)
                s.Bag.Add(l.Item, l.Count);
            if (mon is not null)
                where = " " + s.Keep(mon);
            return null;
        });
        if (error is not null)
        {
            await db.Letters.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Claimed, false));
            Reply(p, topic, false, error);
            return;
        }
        Reply(p, topic, true, $"Collected {Goods(l.Money, l.Item, l.Count, l.Pokemon)}.{where}");
        await Mails(p);
        await Badges(p);
    }

    async Task MailDelete(WorldPlayer p, IReadOnlyDictionary<string, string> a)
    {
        var id = (long)Int(a, "id");
        await using var db = await dbs.CreateDbContextAsync();
        var deleted = await db.Letters.Where(l => l.Id == id && l.To == p.CharacterId
            && (l.Claimed || (l.Money <= 0 && l.Item <= 0 && l.Pokemon == null))).ExecuteDeleteAsync();
        Reply(p, "mail", deleted > 0, deleted > 0 ? "Deleted." : "Collect what's attached before deleting it.");
        await Mails(p);
        await Badges(p);
    }

    // ---- Direct messages --------------------------------------------------------------------------------------------

    readonly Dictionary<long, Queue<DateTime>> _dmTimes = [];

    async Task DmSend(WorldPlayer p, IReadOnlyDictionary<string, string> a)
    {
        const string topic = "dm";
        var text = Clean(a.TryGetValue("text", out var t) ? t : "", MaxDm).Replace('\n', ' ');
        if (text.Length == 0)
            return;
        if (p.MutedUntil is { } until && until > DateTime.UtcNow)
        {
            Reply(p, topic, false, $"You're muted until {until:HH:mm} UTC.");
            return;
        }
        lock (_dmTimes)
        {
            var times = _dmTimes.TryGetValue(p.CharacterId, out var q) ? q : _dmTimes[p.CharacterId] = new Queue<DateTime>();
            while (times.Count > 0 && (DateTime.UtcNow - times.Peek()).TotalSeconds > 10)
                times.Dequeue();
            if (times.Count >= 8)
            {
                Reply(p, topic, false, "Slow down a little.");
                return;
            }
            times.Enqueue(DateTime.UtcNow);
        }
        if (await Someone(p, topic, Arg(a, "to")) is not { } them)
            return;
        await using var db = await dbs.CreateDbContextAsync();
        if (await Blocks(db, them.Id, p.CharacterId))
        {
            Reply(p, topic, false, $"{them.Name} isn't taking messages from you.");
            return;
        }
        var message = new DirectMessage { From = p.CharacterId, To = them.Id, Text = text, Sent = DateTime.UtcNow };
        db.DirectMessages.Add(message);
        await db.SaveChangesAsync();
        var row = new DmRow(p.Name, text, message.Sent);
        p.Link.Send(new DmReceived(them.Name, row));
        if (world.OnlineCharacter(them.Id) is { } other)
        {
            other.Link.Send(new DmReceived(p.Name, row));
            await Badges(other);
        }
    }

    async Task DmThreadList(WorldPlayer p)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var me = p.CharacterId;
        var all = await db.DirectMessages.AsNoTracking().Where(m => m.From == me || m.To == me).OrderByDescending(m => m.Id).Take(2000).ToListAsync();
        var ids = all.Select(m => m.From == me ? m.To : m.From).Distinct().ToList();
        var names = await db.Characters.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);
        var threads = all.GroupBy(m => m.From == me ? m.To : m.From)
            .Where(g => names.ContainsKey(g.Key))
            .Select(g => new DmThreadRow(names[g.Key], g.First().Text, g.First().Sent, g.Count(m => m.To == me && !m.Read)))
            .ToList();
        p.Link.Send(new DmThreads(threads));
    }

    async Task DmOpen(WorldPlayer p, IReadOnlyDictionary<string, string> a)
    {
        if (await Someone(p, "dm", Arg(a, "with")) is not { } them)
            return;
        await using var db = await dbs.CreateDbContextAsync();
        var me = p.CharacterId;
        var messages = await db.DirectMessages.AsNoTracking()
            .Where(m => (m.From == me && m.To == them.Id) || (m.From == them.Id && m.To == me))
            .OrderByDescending(m => m.Id).Take(200).ToListAsync();
        await db.DirectMessages.Where(m => m.From == them.Id && m.To == me && !m.Read).ExecuteUpdateAsync(u => u.SetProperty(m => m.Read, true));
        messages.Reverse();
        p.Link.Send(new DmConversation(them.Name, messages.Select(m => new DmRow(m.From == me ? p.Name : them.Name, m.Text, m.Sent)).ToList()));
        await Badges(p);
    }
}
