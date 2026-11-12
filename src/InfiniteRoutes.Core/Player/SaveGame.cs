using System.Text.Json;
using System.Text.Json.Serialization;
using InfiniteRoutes.Core.Battle;

namespace InfiniteRoutes.Core.Player;

public sealed record ItemSave(int Item, int Count);

public sealed record AttachmentSave(int Money, int Item, int Count, PokemonSave? Pokemon);

public sealed record MailSave(int Id, string From, string Subject, string Body, DateTime Sent, bool Read, bool Claimed, List<AttachmentSave> Attachments);

public sealed record ListingSave(int Id, string Seller, ListingKind Kind, int Item, int Count, PokemonSave? Pokemon, int Price, DateTime Listed, DateTime Expires);

public sealed record ProgressSave(Dictionary<int, DateTime> Badges, List<int> DefeatedTrainers, int Money, int Caught, int WildWins,
    int CenterVisits, DateTime Started, TimeSpan PlayTime, List<string>? Gifts = null, Dictionary<int, int>? CaughtSpecies = null);

/// <summary>
/// An offline save: a JSON snapshot of a <see cref="GameState"/>. Pokémon are stored by species and move ids, so a save
/// only loads against the same game's data; anything that no longer resolves is dropped rather than failing the load.
/// </summary>
public sealed record SaveGame(int Version, string Game, DateTime Saved, PlayerLocation? Location, List<PokemonSave> Party,
    List<PokemonSave> Box, List<ItemSave> Bag, ProgressSave Progress, QuestLogSave Quests, List<MailSave> Mail,
    List<Friend> Friends, List<string> Ignored, List<ListingSave> Market, List<string>? BoxNames = null, int RepelSteps = 0,
    PlayerLocation? LastOutdoor = null, int Buddy = -1)
{
    public const int CurrentVersion = 1;

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static SaveGame From(GameState state, string game, DateTime now)
    {
        var p = state.Progress;
        return new SaveGame(CurrentVersion, game, now, state.Location,
            state.Party.Select(m => m.ToSave()).ToList(),
            state.Box.Select(m => m.ToSave()).ToList(),
            state.Bag.Items.Select(i => new ItemSave(i.Item, i.Count)).ToList(),
            new ProgressSave(new(p.Badges), [.. p.DefeatedTrainers], p.Money, p.Caught, p.WildWins, p.CenterVisits, p.Started, p.PlayTime,
                [.. p.Gifts.Order()], new(p.CaughtSpecies)),
            state.Quests.ToSave(),
            state.Mail.Messages.Select(m => new MailSave(m.Id, m.From, m.Subject, m.Body, m.Sent, m.Read, m.Claimed,
                m.Attachments.Select(a => new AttachmentSave(a.Money, a.Item, a.Count, a.Pokemon?.ToSave())).ToList())).ToList(),
            [.. state.Friends.Friends],
            [.. state.Friends.Ignored],
            state.Market.Listings.Select(l => new ListingSave(l.Id, l.Seller, l.Kind, l.Item, l.Count, l.Pokemon?.ToSave(), l.Price, l.Listed, l.Expires)).ToList(),
            [.. state.BoxNames], state.RepelSteps, state.LastOutdoor, state.Buddy is { } b ? state.Party.IndexOf(b) : -1);
    }

    /// <summary>Rebuilds the game. <paramref name="quests"/> is a fresh log over the current quest catalog.</summary>
    public GameState Restore(BattleData data, QuestLog quests)
    {
        var p = Progress;
        var state = new GameState(quests)
        {
            Progress = new PlayerProgress
            {
                Money = Math.Max(0, p.Money), Caught = p.Caught, WildWins = p.WildWins, CenterVisits = p.CenterVisits,
                Started = p.Started, PlayTime = p.PlayTime,
            },
            Location = Location,
            RepelSteps = Math.Max(0, RepelSteps),
            LastOutdoor = LastOutdoor,
        };
        foreach (var (badge, when) in p.Badges)
            state.Progress.Badges[badge] = when;
        state.Progress.DefeatedTrainers.UnionWith(p.DefeatedTrainers);
        state.Progress.Gifts.UnionWith(p.Gifts ?? []);
        foreach (var (species, count) in p.CaughtSpecies ?? [])
            state.Progress.CaughtSpecies[species] = count;
        state.Party.AddRange(Party.Select(s => Pokemon.FromSave(data, s)).OfType<Pokemon>().Take(GameState.PartySize));
        state.Box.AddRange(Box.Select(s => Pokemon.FromSave(data, s)).OfType<Pokemon>());
        foreach (var item in Bag)
            state.Bag.Add(item.Item, item.Count);
        for (var i = 0; i < Math.Min(BoxNames?.Count ?? 0, GameState.BoxCount); i++)
            state.RenameBox(i, BoxNames![i]);
        state.Progress.PartyCount = state.Party.Count;
        state.Progress.TopLevel = state.Party.Select(m => m.Level).DefaultIfEmpty().Max();
        if (Buddy >= 0 && Buddy < state.Party.Count)
            state.Buddy = state.Party[Buddy];
        quests.Load(Quests);
        state.Mail.Load(Mail.Select(m => new MailMessage
        {
            Id = m.Id, From = m.From, Subject = m.Subject, Body = m.Body, Sent = m.Sent, Read = m.Read, Claimed = m.Claimed,
            Attachments = m.Attachments.Select(a => new MailAttachment(a.Money, a.Item, a.Count, a.Pokemon is { } mon ? Pokemon.FromSave(data, mon) : null))
                .Where(a => !a.IsEmpty).ToList(),
        }));
        foreach (var friend in Friends)
            state.Friends.Set(friend);
        foreach (var name in Ignored)
            state.Friends.Ignore(name);
        state.Market.Load(Market
            .Select(l => new MarketListing(l.Id, l.Seller, l.Kind, l.Item, l.Count, l.Pokemon is { } mon ? Pokemon.FromSave(data, mon) : null, l.Price, l.Listed, l.Expires))
            .Where(l => l.Kind == ListingKind.Item || l.Pokemon is not null));
        return state;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The Pokémon following the player in this save (see <see cref="GameState.ActiveBuddy"/>), or null.</summary>
    public PokemonSave? ActiveBuddy =>
        Buddy >= 0 && Buddy < Party.Count && Party[Buddy].Hp > 0 && Progress.Caught >= GameState.BuddyCatches ? Party[Buddy] : null;

    public static SaveGame? FromJson(string json) => JsonSerializer.Deserialize<SaveGame>(json, Json);

    /// <summary>One Pokémon as JSON, the way saves write it (the admin window sends Pokémon this way).</summary>
    public static string PokemonJson(PokemonSave pokemon) => JsonSerializer.Serialize(pokemon, Json);

    public static PokemonSave? PokemonFromJson(string json) => JsonSerializer.Deserialize<PokemonSave>(json, Json);

    /// <summary>The save's JSON without its timestamp, so two saves of the same game compare equal.</summary>
    public static string Comparable(string json) =>
        FromJson(json) is { } save ? JsonSerializer.Serialize(save with { Saved = default }, Json) : json;

    /// <summary>
    /// Writes the save safely: to a temporary file first, then swapped in, keeping the previous save as <c>.bak</c>,
    /// so a crash mid-write never leaves a broken save behind.
    /// </summary>
    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson());
        if (File.Exists(path))
            File.Replace(temp, path, path + ".bak");
        else
            File.Move(temp, path);
    }

    /// <summary>Reads a save, falling back to the <c>.bak</c> copy when the main file is missing or unreadable. Null when neither works.</summary>
    public static SaveGame? Read(string path)
    {
        foreach (var file in new[] { path, path + ".bak" })
        {
            try
            {
                if (File.Exists(file) && FromJson(File.ReadAllText(file)) is { Version: > 0 and <= CurrentVersion } save)
                    return save;
            }
            catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
            {
            }
        }
        return null;
    }
}
