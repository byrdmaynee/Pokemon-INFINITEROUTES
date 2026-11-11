namespace InfiniteRoutes.Server.Admin;

/// <summary>Staff roles, lowest to highest (BUILD_SPEC §11.1).</summary>
public enum Role { Player, Helper, Moderator, GameMaster, Admin, Owner }

/// <summary>
/// What each role may do. A role has its own permissions and everything below it. Nobody can grant a role at or above
/// their own, and only an Owner can make Admins or Owners — see <see cref="CanGrant"/>.
/// </summary>
public static class Permissions
{
    public const string Who = "player.list";
    public const string Inspect = "player.view";
    public const string Kick = "player.kick";
    public const string Announce = "server.announce";
    public const string Teleport = "world.teleport";
    public const string Summon = "player.summon";
    public const string Invisible = "admin.invisible";
    public const string Noclip = "admin.noclip";
    public const string Roles = "roles.grant";
    public const string AuditRead = "audit.view";
    public const string Mute = "chat.mute";
    public const string Spectate = "admin.spectate";

    static readonly Dictionary<Role, string[]> Own = new()
    {
        [Role.Player] = [],
        [Role.Helper] = [Who, Mute],
        [Role.Moderator] = [Inspect, Kick, AuditRead],
        [Role.GameMaster] = [Announce, Teleport, Summon, Invisible, Noclip, Spectate],
        [Role.Admin] = [Roles],
        [Role.Owner] = [],
    };

    public static IReadOnlyList<string> Of(Role role) =>
        Enum.GetValues<Role>().Where(r => r <= role).SelectMany(r => Own[r]).Distinct().ToList();

    public static bool Has(Role role, string permission) => Of(role).Contains(permission);

    public static Role Parse(string? text) => Enum.TryParse<Role>(text, ignoreCase: true, out var r) ? r : Role.Player;

    /// <summary>Whether <paramref name="granter"/> may give <paramref name="role"/> to someone who has <paramref name="current"/>.</summary>
    public static bool CanGrant(Role granter, Role current, Role role) =>
        Has(granter, Roles) && (granter == Role.Owner || (role < granter && current < granter));
}
