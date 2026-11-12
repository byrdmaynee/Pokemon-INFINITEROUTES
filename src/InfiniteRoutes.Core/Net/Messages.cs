using MessagePack;

namespace InfiniteRoutes.Core.Net;

/// <summary>
/// The game protocol (BUILD_SPEC §4.1, §4.3): after TLS, each side sends length-prefixed MessagePack frames (see
/// <see cref="Frames"/>), each one <see cref="Message"/> tagged with its union key. Bump <see cref="Version"/> when a
/// message changes shape; the server refuses clients on another version.
/// </summary>
public static class Protocol
{
    public const int Version = 7;   // 2: admin window; 3: battles; 4: market, trades, friends, mail, DMs; 5: Active Buddy; 6: NPCs, trainers, quests; 7: role beacons

    /// <summary>The largest frame either side accepts.</summary>
    public const int MaxFrame = 256 * 1024;
}

/// <summary>Where a player stands: a zone (which says which map), a tile in that map, and facing (0 down, 1 up, 2 left, 3 right).</summary>
[MessagePackObject]
public sealed record NetLocation([property: Key(0)] int Zone, [property: Key(1)] int X, [property: Key(2)] int Y, [property: Key(3)] int Facing);

/// <summary>Another player as seen by this one.</summary>
[MessagePackObject]
public sealed record NetPlayer(
    [property: Key(0)] int Id,
    [property: Key(1)] string Name,
    [property: Key(2)] int Appearance,
    [property: Key(3)] NetLocation Location,
    [property: Key(4)] int Buddy = 0,
    [property: Key(5)] bool BuddyShiny = false,
    [property: Key(6)] bool BuddyFemale = false,
    [property: Key(7)] string Beacon = "");

/// <summary>A player's new tile and facing; <see cref="Run"/> picks the animation.</summary>
[MessagePackObject]
public sealed record NetMove(
    [property: Key(0)] int Id,
    [property: Key(1)] int X,
    [property: Key(2)] int Y,
    [property: Key(3)] int Facing,
    [property: Key(4)] bool Run);

[Union(0, typeof(Hello))]
[Union(1, typeof(Welcome))]
[Union(2, typeof(Refused))]
[Union(3, typeof(Step))]
[Union(4, typeof(StepResult))]
[Union(5, typeof(Warp))]
[Union(6, typeof(Warped))]
[Union(7, typeof(Turn))]
[Union(8, typeof(PlayersAppeared))]
[Union(9, typeof(PlayersMoved))]
[Union(10, typeof(PlayersLeft))]
[Union(11, typeof(Ping))]
[Union(12, typeof(Pong))]
[Union(13, typeof(Notice))]
[Union(14, typeof(SwitchChannel))]
[Union(15, typeof(Say))]
[Union(16, typeof(ChatLine))]
[Union(17, typeof(Elevate))]
[Union(18, typeof(AdminCommand))]
[Union(19, typeof(AdminReply))]
[Union(20, typeof(AdminStatus))]
[Union(21, typeof(StateSnapshot))]
[Union(22, typeof(SetLead))]
[Union(23, typeof(AdminPlayers))]
[Union(24, typeof(SpectateStatus))]
[Union(25, typeof(AdminAction))]
[Union(26, typeof(AdminPlayerDetail))]
[Union(27, typeof(AdminRoles))]
[Union(28, typeof(AdminAudit))]
[Union(29, typeof(BattleStart))]
[Union(30, typeof(BattleInput))]
[Union(31, typeof(BattleTurn))]
[Union(32, typeof(BattleOver))]
[Union(33, typeof(SocialAction))]
[Union(34, typeof(SocialReply))]
[Union(35, typeof(MarketPage))]
[Union(36, typeof(TradeList))]
[Union(37, typeof(FriendsList))]
[Union(38, typeof(MailList))]
[Union(39, typeof(DmThreads))]
[Union(40, typeof(DmConversation))]
[Union(41, typeof(DmReceived))]
[Union(42, typeof(SocialBadges))]
[Union(43, typeof(SetBuddy))]
[Union(44, typeof(BuddyChanged))]
[Union(45, typeof(NpcAction))]
[Union(46, typeof(NpcResult))]
[Union(47, typeof(BeaconChanged))]
public abstract record Message;

/// <summary>Chat channels: near you (same map and channel), the whole server, one player, or the server itself.</summary>
public enum ChatKind { Local = 0, Global = 1, Whisper = 2, System = 3 }

// ---- Client → server ----------------------------------------------------------------------------------------------

/// <summary>
/// The first frame: the protocol version, the session token from logging in over HTTPS, and which of the account's
/// characters to play. Answered by <see cref="Welcome"/> or <see cref="Refused"/>.
/// </summary>
[MessagePackObject]
public sealed record Hello([property: Key(0)] int Version, [property: Key(1)] string Token, [property: Key(2)] long Character) : Message;

/// <summary>A step intent: one tile in direction (Dx, Dy), walking or running. <see cref="Seq"/> numbers the client's steps.</summary>
[MessagePackObject]
public sealed record Step([property: Key(0)] int Seq, [property: Key(1)] int Dx, [property: Key(2)] int Dy, [property: Key(3)] bool Run) : Message;

/// <summary>Taking the warp at tile (X, Y) of the current map (a door or stairs ahead, or the mat or map edge underfoot).</summary>
[MessagePackObject]
public sealed record Warp([property: Key(0)] int Seq, [property: Key(1)] int X, [property: Key(2)] int Y) : Message;

/// <summary>Turning to face a direction without moving.</summary>
[MessagePackObject]
public sealed record Turn([property: Key(0)] int Facing) : Message;

/// <summary>Moving to another channel of the same world (refused while busy).</summary>
[MessagePackObject]
public sealed record SwitchChannel([property: Key(0)] int Channel) : Message;

[MessagePackObject]
public sealed record Ping([property: Key(0)] long Time) : Message;

/// <summary>A chat line to send: local or global, or a whisper to the character named <see cref="To"/>.</summary>
[MessagePackObject]
public sealed record Say([property: Key(0)] ChatKind Kind, [property: Key(1)] string Text, [property: Key(2)] string? To = null) : Message;

/// <summary>Entering Admin Mode: the account password and a code from the authenticator app (BUILD_SPEC §11.1).</summary>
[MessagePackObject]
public sealed record Elevate([property: Key(0)] string Password, [property: Key(1)] string Code) : Message;

/// <summary>An admin console command ("tp Cheren", "announce …"); the server checks the caller's permissions.</summary>
[MessagePackObject]
public sealed record AdminCommand([property: Key(0)] string Text) : Message;

/// <summary>Puts party member <see cref="Index"/> first.</summary>
[MessagePackObject]
public sealed record SetLead([property: Key(0)] int Index) : Message;

// ---- Server → client ----------------------------------------------------------------------------------------------

/// <summary>Joined: this player's id, name, channel and where the server puts them; the channel count to switch between.</summary>
[MessagePackObject]
public sealed record Welcome(
    [property: Key(0)] int PlayerId,
    [property: Key(1)] string Name,
    [property: Key(2)] int Channel,
    [property: Key(3)] int Channels,
    [property: Key(4)] NetLocation Location,
    [property: Key(5)] string ServerName) : Message;

/// <summary>Why the server won't let this connection in (it closes after sending this).</summary>
[MessagePackObject]
public sealed record Refused([property: Key(0)] string Reason) : Message;

/// <summary>
/// The server's answer to a <see cref="Step"/>: accepted, or rejected with the tile the player is really on (the client
/// snaps back to it).
/// </summary>
[MessagePackObject]
public sealed record StepResult([property: Key(0)] int Seq, [property: Key(1)] bool Accepted, [property: Key(2)] NetLocation Location) : Message;

/// <summary>A <see cref="Warp"/> was taken: the player is now at <see cref="Location"/> (another zone, maybe another map).</summary>
[MessagePackObject]
public sealed record Warped([property: Key(0)] int Seq, [property: Key(1)] NetLocation Location) : Message;

/// <summary>Players that came into view (joined, walked or warped near).</summary>
[MessagePackObject]
public sealed record PlayersAppeared([property: Key(0)] IReadOnlyList<NetPlayer> Players) : Message;

/// <summary>Moves of players in view since the last update (sent at 10 Hz).</summary>
[MessagePackObject]
public sealed record PlayersMoved([property: Key(0)] IReadOnlyList<NetMove> Moves) : Message;

/// <summary>Players that went out of view or left.</summary>
[MessagePackObject]
public sealed record PlayersLeft([property: Key(0)] IReadOnlyList<int> Ids) : Message;

[MessagePackObject]
public sealed record Pong([property: Key(0)] long Time) : Message;

/// <summary>A chat line to show. <see cref="Staff"/> marks a line from someone in Admin Mode.</summary>
[MessagePackObject]
public sealed record ChatLine(
    [property: Key(0)] ChatKind Kind,
    [property: Key(1)] string From,
    [property: Key(2)] string Text,
    [property: Key(3)] string? To = null,
    [property: Key(4)] bool Staff = false) : Message;

/// <summary>
/// The answer to an <see cref="AdminCommand"/> or <see cref="Elevate"/>: lines of text, and whether it worked.
/// <see cref="Topic"/> names what it's about when a window wants it ("inspect:Cheren").
/// </summary>
[MessagePackObject]
public sealed record AdminReply([property: Key(0)] bool Ok, [property: Key(1)] IReadOnlyList<string> Lines, [property: Key(2)] string Topic = "") : Message;

/// <summary>One online player as the admin panel lists them.</summary>
[MessagePackObject]
public sealed record AdminPlayerRow(
    [property: Key(0)] int Id,
    [property: Key(1)] string Name,
    [property: Key(2)] string Account,
    [property: Key(3)] string Role,
    [property: Key(4)] int Channel,
    [property: Key(5)] NetLocation Location,
    [property: Key(6)] bool Elevated,
    [property: Key(7)] bool Invisible,
    [property: Key(8)] string? MutedUntil,
    [property: Key(9)] bool Online = true);

/// <summary>Everyone online, for the admin panel (the answer to the "players" command).</summary>
[MessagePackObject]
public sealed record AdminPlayers([property: Key(0)] IReadOnlyList<AdminPlayerRow> Players) : Message;

/// <summary>Spectating: the client's camera follows player <see cref="TargetId"/> (0: back to your own character).</summary>
[MessagePackObject]
public sealed record SpectateStatus([property: Key(0)] int TargetId, [property: Key(1)] string Name) : Message;

/// <summary>
/// The player's staff standing: their role, whether Admin Mode is on (and its toggles), and what they're allowed to
/// do. Sent on joining (to staff) and whenever it changes; the client only uses it to show or hide admin UI.
/// </summary>
[MessagePackObject]
public sealed record AdminStatus(
    [property: Key(0)] string Role,
    [property: Key(1)] bool Elevated,
    [property: Key(2)] bool Invisible,
    [property: Key(3)] bool Noclip,
    [property: Key(4)] IReadOnlyList<string> Permissions) : Message;

/// <summary>
/// The character's game state as the server holds it (the save JSON of <c>Core.Player.SaveGame</c>), for the menus to
/// show. Sent on joining and after anything changes it.
/// </summary>
[MessagePackObject]
public sealed record StateSnapshot([property: Key(0)] string Json) : Message;

/// <summary>A message for the player: a channel switch, a server announcement, why they were disconnected.</summary>
[MessagePackObject]
public sealed record Notice([property: Key(0)] string Text, [property: Key(1)] int Channel = 0) : Message;

// ---- The admin window (BUILD_SPEC §11.3) --------------------------------------------------------------------------

/// <summary>
/// A structured admin action from the admin window: <see cref="Verb"/> on <see cref="Target"/> (a character or role
/// name) with named <see cref="Args"/>. <see cref="Reason"/> is required for punishments and economy changes. The
/// server checks the caller's permissions, applies it, audits it and answers with an <see cref="AdminReply"/> (and a
/// fresh <see cref="AdminPlayerDetail"/> / <see cref="AdminRoles"/> when those changed).
/// </summary>
[MessagePackObject]
public sealed record AdminAction(
    [property: Key(0)] string Verb,
    [property: Key(1)] string Target,
    [property: Key(2)] IReadOnlyDictionary<string, string> Args,
    [property: Key(3)] string Reason = "") : Message;

/// <summary>Everything the player editor shows about one character (online or not).</summary>
[MessagePackObject]
public sealed record AdminPlayerDetail(
    [property: Key(0)] string Character,
    [property: Key(1)] string Account,
    [property: Key(2)] string Role,
    [property: Key(3)] bool Online,
    [property: Key(4)] NetLocation Location,
    [property: Key(5)] string? MutedUntil,
    [property: Key(6)] string? BannedUntil,
    [property: Key(7)] string? BanReason,
    [property: Key(8)] string? StateJson,
    [property: Key(9)] string LastSeen) : Message;

/// <summary>A role: its name, rank (higher outranks lower), whether it's built in, and what it may do.</summary>
[MessagePackObject]
public sealed record RoleRow(
    [property: Key(0)] string Name,
    [property: Key(1)] int Rank,
    [property: Key(2)] bool Builtin,
    [property: Key(3)] IReadOnlyList<string> Permissions,
    [property: Key(4)] int Accounts,
    [property: Key(5)] string Beacon = "");

/// <summary>A permission the server knows, with what it lets a role do.</summary>
[MessagePackObject]
public sealed record PermissionRow([property: Key(0)] string Key, [property: Key(1)] string Description);

/// <summary>Every role and every permission, for the Roles tab.</summary>
[MessagePackObject]
public sealed record AdminRoles([property: Key(0)] IReadOnlyList<RoleRow> Roles, [property: Key(1)] IReadOnlyList<PermissionRow> Catalog) : Message;

/// <summary>One audit log entry; <see cref="Change"/> is the change it can be reverted with (0 when it can't).</summary>
[MessagePackObject]
public sealed record AuditRow(
    [property: Key(0)] long Id,
    [property: Key(1)] string Time,
    [property: Key(2)] string Actor,
    [property: Key(3)] string Role,
    [property: Key(4)] string Action,
    [property: Key(5)] string Target,
    [property: Key(6)] string Details,
    [property: Key(7)] long Change);

[MessagePackObject]
public sealed record AdminAudit([property: Key(0)] IReadOnlyList<AuditRow> Entries) : Message;

// ---- Battles (BUILD_SPEC §4.3: the server runs them; the client replays them) ---------------------------------------

/// <summary>
/// A wild Pokémon appeared: the client builds the same battle from the game the server just sent (the
/// <see cref="StateSnapshot"/> before this) and <see cref="Wild"/>, with <c>new Random(Seed)</c> for the start, and plays
/// it. Every later action gets its own seed in <see cref="BattleTurn"/>.
/// </summary>
[MessagePackObject]
public sealed record BattleStart(
    [property: Key(0)] int Battle,
    [property: Key(1)] string Wild,
    [property: Key(2)] int Seed,
    [property: Key(3)] int Kind = 0,
    [property: Key(4)] int Trainer = -1) : Message;

/// <summary>One Pokémon's choice for a turn: fight (move, target side, target slot), switch (party index), item (item, party index) or run.</summary>
[MessagePackObject]
public sealed record NetChoice([property: Key(0)] string Kind, [property: Key(1)] int A = 0, [property: Key(2)] int B = 0, [property: Key(3)] int C = 0);

/// <summary>
/// The player acts in a battle. <see cref="Kind"/>: <c>turn</c> (<see cref="Choices"/>, one per slot, null for a slot
/// with nothing to choose), <c>locked</c> (a charging or rampaging move goes on), <c>send-out</c> / <c>rotate</c>
/// (party index <see cref="A"/>), <c>learn</c> (party <see cref="A"/> learns move <see cref="B"/> forgetting slot
/// <see cref="C"/>, -1 to skip), <c>nickname</c> (<see cref="Text"/> for the Pokémon just caught), <c>end</c> (the
/// client has shown the end). The first four are answered with a <see cref="BattleTurn"/>.
/// </summary>
[MessagePackObject]
public sealed record BattleInput(
    [property: Key(0)] int Battle,
    [property: Key(1)] int Seq,
    [property: Key(2)] string Kind,
    [property: Key(3)] IReadOnlyList<NetChoice?>? Choices = null,
    [property: Key(4)] int A = 0,
    [property: Key(5)] int B = 0,
    [property: Key(6)] int C = 0,
    [property: Key(7)] string Text = "") : Message;

/// <summary>
/// The server played the action: replay it after <c>Reseed(Seed)</c>; <see cref="Digest"/> is how the battle stood
/// after it (see <c>SingleBattle.Digest</c>). Refused actions say why and change nothing.
/// </summary>
[MessagePackObject]
public sealed record BattleTurn(
    [property: Key(0)] int Battle,
    [property: Key(1)] int Seq,
    [property: Key(2)] bool Accepted,
    [property: Key(3)] int Seed,
    [property: Key(4)] string Digest = "",
    [property: Key(5)] string Reason = "") : Message;

/// <summary>The battle is over and recorded (a new <see cref="StateSnapshot"/> comes with it): lines to show, how it ended.</summary>
[MessagePackObject]
public sealed record BattleOver([property: Key(0)] int Battle, [property: Key(1)] int Outcome, [property: Key(2)] IReadOnlyList<string> Lines) : Message;

// ---- The player menu: market, trades, friends, mail, direct messages (BUILD_SPEC §7.6–7.7) -------------------------

/// <summary>
/// Something done from the player menu. <see cref="Verb"/> names it (<c>market-search</c>, <c>market-sell</c>,
/// <c>market-buy</c>, <c>market-cancel</c>, <c>market-mine</c>, <c>trade-offer</c>, <c>trade-accept</c>,
/// <c>trade-decline</c>, <c>trades</c>, <c>friend-add</c>, <c>friend-accept</c>, <c>friend-remove</c>, <c>block</c>,
/// <c>unblock</c>, <c>friends</c>, <c>mail-send</c>, <c>mails</c>, <c>mail-read</c>, <c>mail-claim</c>,
/// <c>mail-delete</c>, <c>dm-send</c>, <c>dm-threads</c>, <c>dm-open</c>); <see cref="Args"/> carries its fields.
/// </summary>
[MessagePackObject]
public sealed record SocialAction([property: Key(0)] string Verb, [property: Key(1)] IReadOnlyDictionary<string, string> Args) : Message;

/// <summary>The answer to a <see cref="SocialAction"/> (or news from the server): which part of the menu, whether it worked, a line to show.</summary>
[MessagePackObject]
public sealed record SocialReply([property: Key(0)] string Topic, [property: Key(1)] bool Ok, [property: Key(2)] string Text) : Message;

/// <summary>A Global Market listing: a stack of items, or one Pokémon (as <c>PokemonSave</c> JSON).</summary>
[MessagePackObject]
public sealed record ListingRow(
    [property: Key(0)] long Id,
    [property: Key(1)] string Seller,
    [property: Key(2)] int Item,
    [property: Key(3)] int Count,
    [property: Key(4)] string? Pokemon,
    [property: Key(5)] int Price,
    [property: Key(6)] DateTime Listed,
    [property: Key(7)] DateTime Expires);

/// <summary>Market listings: a search's results, or (<see cref="Mine"/>) the player's own.</summary>
[MessagePackObject]
public sealed record MarketPage([property: Key(0)] IReadOnlyList<ListingRow> Listings, [property: Key(1)] bool Mine) : Message;

/// <summary>
/// A trade offer between two players: what <see cref="From"/> gives (held by the server meanwhile) and asks for.
/// <see cref="AskSpecies"/>: -1 no Pokémon, 0 any Pokémon, else that species — the receiver picks which of theirs.
/// </summary>
[MessagePackObject]
public sealed record TradeRow(
    [property: Key(0)] long Id,
    [property: Key(1)] string From,
    [property: Key(2)] string To,
    [property: Key(3)] int GiveMoney,
    [property: Key(4)] int GiveItem,
    [property: Key(5)] int GiveCount,
    [property: Key(6)] string? GivePokemon,
    [property: Key(7)] int AskMoney,
    [property: Key(8)] int AskItem,
    [property: Key(9)] int AskCount,
    [property: Key(10)] int AskSpecies,
    [property: Key(11)] string Note,
    [property: Key(12)] DateTime Expires);

[MessagePackObject]
public sealed record TradeList([property: Key(0)] IReadOnlyList<TradeRow> Incoming, [property: Key(1)] IReadOnlyList<TradeRow> Outgoing) : Message;

/// <summary>A friend or request. <see cref="State"/>: online, offline, sent (waiting on them) or received (waiting on you).</summary>
[MessagePackObject]
public sealed record FriendRow(
    [property: Key(0)] string Name,
    [property: Key(1)] string State,
    [property: Key(2)] int Zone,
    [property: Key(3)] int Channel,
    [property: Key(4)] DateTime LastSeen);

[MessagePackObject]
public sealed record FriendsList([property: Key(0)] IReadOnlyList<FriendRow> Friends, [property: Key(1)] IReadOnlyList<string> Blocked) : Message;

/// <summary>A letter, with what's attached (money, an item stack, a Pokémon as JSON) and whether it's been collected.</summary>
[MessagePackObject]
public sealed record LetterRow(
    [property: Key(0)] long Id,
    [property: Key(1)] string From,
    [property: Key(2)] string Subject,
    [property: Key(3)] string Body,
    [property: Key(4)] DateTime Sent,
    [property: Key(5)] bool Read,
    [property: Key(6)] int Money,
    [property: Key(7)] int Item,
    [property: Key(8)] int Count,
    [property: Key(9)] string? Pokemon,
    [property: Key(10)] bool Claimed);

[MessagePackObject]
public sealed record MailList([property: Key(0)] IReadOnlyList<LetterRow> Letters) : Message;

[MessagePackObject]
public sealed record DmRow([property: Key(0)] string From, [property: Key(1)] string Text, [property: Key(2)] DateTime Sent);

/// <summary>One conversation in the Messages list: who with, the latest line, and how many are unread.</summary>
[MessagePackObject]
public sealed record DmThreadRow([property: Key(0)] string With, [property: Key(1)] string Last, [property: Key(2)] DateTime Time, [property: Key(3)] int Unread);

[MessagePackObject]
public sealed record DmThreads([property: Key(0)] IReadOnlyList<DmThreadRow> Threads) : Message;

/// <summary>A conversation's messages, oldest first (opening it marks them read).</summary>
[MessagePackObject]
public sealed record DmConversation([property: Key(0)] string With, [property: Key(1)] IReadOnlyList<DmRow> Messages) : Message;

/// <summary>A direct message just sent to (or by) this player, in the conversation with <see cref="With"/>.</summary>
[MessagePackObject]
public sealed record DmReceived([property: Key(0)] string With, [property: Key(1)] DmRow Message) : Message;

/// <summary>What's waiting: unread letters and messages, friend requests and trade offers (for the menu's tab titles).</summary>
[MessagePackObject]
public sealed record SocialBadges(
    [property: Key(0)] int Mail,
    [property: Key(1)] int Messages,
    [property: Key(2)] int Requests,
    [property: Key(3)] int Trades) : Message;

/// <summary>Makes party member <see cref="Index"/> the Active Buddy that follows the player (-1 puts it away).</summary>
[MessagePackObject]
public sealed record SetBuddy([property: Key(0)] int Index) : Message;

// ---- NPCs (BUILD_SPEC §9: what they do is decided by the server) -----------------------------------------------------

/// <summary>
/// The player talked to NPC <see cref="Npc"/> of <see cref="Zone"/> (or, <see cref="Spotted"/>, a trainer saw them). The
/// client shows the NPC's own lines; the server decides what it does: a trainer battle (<see cref="BattleStart"/>), a
/// gift, a quest, or a refusal, answered with <see cref="NpcResult"/>.
/// </summary>
[MessagePackObject]
public sealed record NpcAction([property: Key(0)] int Zone, [property: Key(1)] int Npc, [property: Key(2)] bool Spotted = false) : Message;

/// <summary>What talking to an NPC came to (<see cref="Kind"/>: <c>Core.Player.NpcDealKind</c>) and a line to show.</summary>
[MessagePackObject]
public sealed record NpcResult([property: Key(0)] int Zone, [property: Key(1)] int Npc, [property: Key(2)] int Kind,
    [property: Key(3)] string Text) : Message;

/// <summary>A player in view now shows <see cref="Beacon"/> over their head (their role's; "" for none).</summary>
[MessagePackObject]
public sealed record BeaconChanged([property: Key(0)] int Player, [property: Key(1)] string Beacon) : Message;

/// <summary>A player in view has a new Active Buddy (<see cref="Species"/> 0: none following).</summary>
[MessagePackObject]
public sealed record BuddyChanged([property: Key(0)] int Player, [property: Key(1)] int Species, [property: Key(2)] bool Shiny,
    [property: Key(3)] bool Female = false) : Message;
