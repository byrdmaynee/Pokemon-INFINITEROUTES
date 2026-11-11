using MessagePack;

namespace InfiniteRoutes.Core.Net;

/// <summary>
/// The game protocol (BUILD_SPEC §4.1, §4.3): after TLS, each side sends length-prefixed MessagePack frames (see
/// <see cref="Frames"/>), each one <see cref="Message"/> tagged with its union key. Bump <see cref="Version"/> when a
/// message changes shape; the server refuses clients on another version.
/// </summary>
public static class Protocol
{
    public const int Version = 1;

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
    [property: Key(3)] NetLocation Location);

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
    [property: Key(4)] int Accounts);

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
