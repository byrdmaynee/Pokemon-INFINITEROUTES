using System.Text.RegularExpressions;
using InfiniteRoutes.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace InfiniteRoutes.Server.Admin;

/// <summary>
/// The server's roles, kept in the database and in memory. A new server gets the six built-in roles with their default
/// permissions; Owners can then change any role's permissions (except Owner's, which is everything), make custom roles
/// at a free rank between Player (0) and Owner (100), move them, and delete custom roles nobody has. Changes apply at
/// once to everyone online (see <see cref="Changed"/>).
/// </summary>
public sealed partial class RoleService(IDbContextFactory<IrDb> dbs)
{
    Dictionary<string, RoleInfo> _roles = [];
    readonly object _gate = new();

    /// <summary>Raised after any change, so the world can tell online players their new permissions.</summary>
    public event Action? Changed;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{1,23}$")]
    private static partial Regex RoleName();

    /// <summary>Loads the roles, adding the built-in ones the database doesn't have yet.</summary>
    public void Load()
    {
        using var db = dbs.CreateDbContext();
        var rows = db.Roles.AsNoTracking().ToList();
        foreach (var role in Enum.GetValues<Role>())
            if (!rows.Any(r => r.NameKey == role.ToString().ToLowerInvariant()))
            {
                var row = new RoleDef
                {
                    Name = role.ToString(), NameKey = role.ToString().ToLowerInvariant(), Rank = Permissions.BuiltinRank(role),
                    Permissions = string.Join(',', Permissions.Defaults(role)), Builtin = true,
                    Beacon = Core.Player.Beacons.Default(role.ToString()),
                };
                db.Roles.Add(row);
                rows.Add(row);
            }
        db.SaveChanges();
        lock (_gate)
            _roles = rows.ToDictionary(r => r.NameKey, ToInfo);
    }

    static RoleInfo ToInfo(RoleDef r) =>
        new(r.Name, r.Rank, r.Builtin, r.Permissions.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(Permissions.Known).ToHashSet(),
            Core.Player.Beacons.Known(r.Beacon) && !(Core.Player.Beacons.OwnerOnly(r.Beacon) && r.Rank < RoleInfo.OwnerRank) ? r.Beacon : "");

    /// <summary>The role called <paramref name="name"/>; Player for an unknown or missing one.</summary>
    public RoleInfo Get(string? name)
    {
        lock (_gate)
            return _roles.TryGetValue((name ?? "").ToLowerInvariant(), out var r) ? r
                : _roles.TryGetValue("player", out var player) ? player : new RoleInfo("Player", 0, true, new HashSet<string>());
    }

    public RoleInfo? Find(string? name)
    {
        lock (_gate)
            return _roles.GetValueOrDefault((name ?? "").ToLowerInvariant());
    }

    public IReadOnlyList<RoleInfo> All()
    {
        lock (_gate)
            return [.. _roles.Values.OrderBy(r => r.Rank)];
    }

    /// <summary>How many accounts have each role (by lower-case name).</summary>
    public async Task<Dictionary<string, int>> Counts()
    {
        await using var db = await dbs.CreateDbContextAsync();
        var roles = await db.Accounts.AsNoTracking().Select(a => a.Role).ToListAsync();
        return roles.GroupBy(r => r.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>Makes a custom role at <paramref name="rank"/> with no permissions (or <paramref name="copyFrom"/>'s); null when it worked.</summary>
    public async Task<string?> Create(string name, int rank, string? copyFrom = null)
    {
        if (!RoleName().IsMatch(name ?? ""))
            return "Role names are 2–24 letters or digits, starting with a letter.";
        if (rank is < 1 or >= RoleInfo.OwnerRank)
            return $"A custom role's rank must be from 1 to {RoleInfo.OwnerRank - 1}.";
        if (Find(name) is not null)
            return $"There's already a role called {name}.";
        if (All().FirstOrDefault(r => r.Rank == rank) is { } taken)
            return $"{taken.Name} already has rank {rank}; pick a free one.";
        var permissions = copyFrom is null ? [] : Find(copyFrom)?.All ?? [];
        await Write(db => db.Roles.Add(new RoleDef
        {
            Name = name!, NameKey = name!.ToLowerInvariant(), Rank = rank, Permissions = string.Join(',', permissions), Builtin = false,
        }));
        return null;
    }

    public async Task<string?> Delete(string name)
    {
        if (Find(name) is not { } role)
            return $"There's no role {name}.";
        if (role.Builtin)
            return $"{role.Name} is built in and can't be deleted (its permissions can be changed).";
        if ((await Counts()).GetValueOrDefault(role.Name.ToLowerInvariant()) is var n and > 0)
            return $"{n} account(s) have {role.Name}; give them another role first.";
        await Write(db => db.Roles.Where(r => r.NameKey == role.Name.ToLowerInvariant()).ExecuteDelete());
        return null;
    }

    /// <summary>Gives or takes one permission from a role; null when it worked.</summary>
    public async Task<string?> SetPermission(string name, string permission, bool on)
    {
        if (Find(name) is not { } role)
            return $"There's no role {name}.";
        if (role.IsOwner)
            return "Owners always have every permission.";
        if (role.Rank == 0 && on)
            return "Player is everyone's role and can't have staff permissions; make a role for that.";
        if (!Permissions.Known(permission))
            return $"There's no permission {permission}.";
        var set = new HashSet<string>(role.Granted);
        if (on)
            set.Add(permission);
        else
            set.Remove(permission);
        await Write(db => db.Roles.Where(r => r.NameKey == role.Name.ToLowerInvariant())
            .ExecuteUpdate(u => u.SetProperty(r => r.Permissions, string.Join(',', set.Order()))));
        return null;
    }

    /// <summary>Picks the beacon shown over everyone in a role ("" for none); the crown is the Owners'. Null when it worked.</summary>
    public async Task<string?> SetBeacon(string name, string beacon)
    {
        if (Find(name) is not { } role)
            return $"There's no role {name}.";
        if (!Core.Player.Beacons.Known(beacon))
            return $"There's no beacon {beacon}.";
        if (Core.Player.Beacons.OwnerOnly(beacon) && !role.IsOwner)
            return "The crown is for Owners only.";
        await Write(db => db.Roles.Where(r => r.NameKey == role.Name.ToLowerInvariant()).ExecuteUpdate(u => u.SetProperty(r => r.Beacon, beacon)));
        return null;
    }

    /// <summary>Moves a role (not Player or Owner) to a free rank between them; null when it worked.</summary>
    public async Task<string?> SetRank(string name, int rank)
    {
        if (Find(name) is not { } role)
            return $"There's no role {name}.";
        if (role.Rank == 0 || role.IsOwner)
            return $"{role.Name}'s rank is fixed.";
        if (rank is < 1 or >= RoleInfo.OwnerRank)
            return $"Ranks are from 1 to {RoleInfo.OwnerRank - 1}.";
        if (All().FirstOrDefault(r => r.Rank == rank && r != role) is { } taken)
            return $"{taken.Name} already has rank {rank}.";
        await Write(db => db.Roles.Where(r => r.NameKey == role.Name.ToLowerInvariant()).ExecuteUpdate(u => u.SetProperty(r => r.Rank, rank)));
        return null;
    }

    async Task Write(Action<IrDb> change)
    {
        await using (var db = await dbs.CreateDbContextAsync())
        {
            change(db);
            await db.SaveChangesAsync();
        }
        Load();
        Changed?.Invoke();
    }
}
