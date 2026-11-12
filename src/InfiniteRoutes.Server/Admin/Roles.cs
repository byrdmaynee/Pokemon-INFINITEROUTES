namespace InfiniteRoutes.Server.Admin;

/// <summary>The built-in roles, lowest to highest (BUILD_SPEC §11.1); their ranks are <see cref="Permissions.BuiltinRank"/>.</summary>
public enum Role { Player, Helper, Moderator, GameMaster, Admin, Owner }

/// <summary>
/// A role as the server uses it: a name, a rank (a higher rank outranks a lower one; Player is 0, Owner 100) and the
/// permissions it has. Owners have every permission whatever is stored.
/// </summary>
public sealed record RoleInfo(string Name, int Rank, bool Builtin, IReadOnlySet<string> Granted, string Beacon = "")
{
    public const int OwnerRank = 100;

    public bool IsOwner => Rank >= OwnerRank;
    public bool IsStaff => Rank > 0;
    public bool Has(string permission) => IsOwner || Granted.Contains(permission);

    /// <summary>What this role may do, as the client is told.</summary>
    public IReadOnlyList<string> All => IsOwner ? Permissions.Catalog.Select(p => p.Key).ToList() : [.. Granted.Order()];

    public override string ToString() => Name;
}

/// <summary>
/// The permissions (BUILD_SPEC §11.1) and the built-in roles' defaults. Roles are stored in the database and Owners can
/// change them (<see cref="RoleService"/>); these are only where a new server starts. Nobody can grant a role at or above
/// their own rank — see <see cref="CanGrant"/> — and only Owners edit roles.
/// </summary>
public static class Permissions
{
    public const string Who = "player.list";
    public const string Inspect = "player.view";
    public const string Kick = "player.kick";
    public const string Ban = "player.ban";
    public const string Mute = "chat.mute";
    public const string Announce = "server.announce";
    public const string Teleport = "world.teleport";
    public const string Summon = "player.summon";
    public const string Invisible = "admin.invisible";
    public const string Noclip = "admin.noclip";
    public const string Spectate = "admin.spectate";
    public const string Inventory = "player.inventory.write";
    public const string PokemonEdit = "pokemon.edit";
    public const string PokemonIllegal = "pokemon.illegal";
    public const string Roles = "roles.grant";
    public const string AuditRead = "audit.view";
    public const string Revert = "audit.revert";

    /// <summary>Every permission and what it allows (the Roles tab shows these).</summary>
    public static readonly IReadOnlyList<(string Key, string Description)> Catalog =
    [
        (Who, "See who's online"),
        (Mute, "Mute and unmute players' chat"),
        (Inspect, "Look up any player (online or not): account, character, game"),
        (Kick, "Disconnect players"),
        (Ban, "Ban and unban accounts"),
        (AuditRead, "Read the audit log"),
        (Announce, "Send announcements to everyone"),
        (Teleport, "Teleport to players and places"),
        (Summon, "Bring players to you"),
        (Invisible, "Be invisible to players"),
        (Noclip, "Walk through walls"),
        (Spectate, "Watch a player"),
        (Inventory, "Give and take money and items"),
        (PokemonEdit, "Give, edit, heal and release Pokémon"),
        (PokemonIllegal, "Save Pokémon that couldn't be obtained normally (with a reason)"),
        (Roles, "Give accounts a role below your own"),
        (Revert, "Undo player changes from the audit log"),
    ];

    public static bool Known(string permission) => Catalog.Any(p => p.Key == permission);

    /// <summary>The ranks of the built-in roles (custom roles take any free rank from 1 to 99 in between).</summary>
    public static int BuiltinRank(Role role) => role switch
    {
        Role.Player => 0,
        Role.Helper => 10,
        Role.Moderator => 20,
        Role.GameMaster => 30,
        Role.Admin => 40,
        _ => RoleInfo.OwnerRank,
    };

    /// <summary>What each built-in role starts with on a new server (each has what the ones below it have).</summary>
    public static IReadOnlyList<string> Defaults(Role role)
    {
        string[][] own =
        [
            [],
            [Who, Mute],
            [Inspect, Kick, Ban, AuditRead],
            [Announce, Teleport, Summon, Invisible, Noclip, Spectate, Inventory, PokemonEdit],
            [Roles, PokemonIllegal, Revert],
            [],
        ];
        return Enumerable.Range(0, (int)role + 1).SelectMany(r => own[r]).Distinct().ToList();
    }

    /// <summary>Whether <paramref name="granter"/> may give <paramref name="role"/> to someone who has <paramref name="current"/>.</summary>
    public static bool CanGrant(RoleInfo granter, RoleInfo current, RoleInfo role) =>
        granter.Has(Roles) && (granter.IsOwner || (role.Rank < granter.Rank && current.Rank < granter.Rank));
}
