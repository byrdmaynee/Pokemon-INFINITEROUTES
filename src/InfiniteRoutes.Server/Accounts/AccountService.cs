using System.Security.Cryptography;
using System.Text.RegularExpressions;
using InfiniteRoutes.Server.Admin;
using InfiniteRoutes.Server.Config;
using InfiniteRoutes.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace InfiniteRoutes.Server.Accounts;

/// <summary>A new character's look and where it starts.</summary>
public sealed record StartPoint(int Zone, int X, int Y, int Facing);

/// <summary>What gets written for a character: where it is, and its game state when that changed (null: unchanged).</summary>
public sealed record PlayerSave(long Character, int Zone, int X, int Y, int Facing, string? StateJson);

/// <summary>
/// Accounts, logins and characters. Passwords are stored as PBKDF2-SHA256 (<see cref="Iterations"/> rounds, a random
/// salt each); a login gives a random 32-byte token whose SHA-256 is kept, so a database leak gives no usable tokens.
/// </summary>
public sealed partial class AccountService(IDbContextFactory<IrDb> dbs, ServerConfig config, StartPoint start, Game.GameRules? rules = null)
{
    public const int Iterations = 210_000;
    public const int MinPassword = 8;

    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$")]
    private static partial Regex AccountName();

    [GeneratedRegex("^[A-Za-z0-9]{2,12}$")]
    private static partial Regex CharacterName();

    public sealed record LoginResult(string Token, DateTime Expires, string Account);

    /// <summary>Creates an account; returns an error message, or null when it worked.</summary>
    public async Task<string?> Register(string name, string password)
    {
        if (!AccountName().IsMatch(name ?? ""))
            return "Account names are 3–16 letters, digits or underscores.";
        if ((password ?? "").Length < MinPassword)
            return $"Passwords need at least {MinPassword} characters.";
        await using var db = await dbs.CreateDbContextAsync();
        var key = name!.ToLowerInvariant();
        if (await db.Accounts.AnyAsync(a => a.NameKey == key))
            return "That account name is taken.";
        var salt = RandomNumberGenerator.GetBytes(16);
        db.Accounts.Add(new Account
        {
            Name = name, NameKey = key, PasswordSalt = salt, PasswordIterations = Iterations,
            PasswordHash = Hash(password!, salt, Iterations), Created = DateTime.UtcNow,
        });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return "That account name is taken.";
        }
        return null;
    }

    /// <summary>Checks the password and starts a session; null when the name or password is wrong.</summary>
    public async Task<LoginResult?> Login(string name, string password)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var key = (name ?? "").ToLowerInvariant();
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.NameKey == key);
        if (account is null)
        {
            // Spend the same time as a real check, so timing doesn't reveal which names exist.
            Hash(password ?? "", new byte[16], Iterations);
            return null;
        }
        if (!CryptographicOperations.FixedTimeEquals(Hash(password ?? "", account.PasswordSalt, account.PasswordIterations), account.PasswordHash))
            return null;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expires = DateTime.UtcNow.AddHours(config.SessionHours);
        db.Sessions.Add(new Session { AccountId = account.Id, TokenHash = TokenHash(token), Created = DateTime.UtcNow, Expires = expires });
        await db.SaveChangesAsync();
        return new LoginResult(token, expires, account.Name);
    }

    /// <summary>The account a token belongs to, if it's a live session.</summary>
    public async Task<Account?> FromToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        await using var db = await dbs.CreateDbContextAsync();
        var hash = TokenHash(token);
        var session = await db.Sessions.Include(s => s.Account).FirstOrDefaultAsync(s => s.TokenHash == hash);
        return session is not null && session.Expires > DateTime.UtcNow ? session.Account : null;
    }

    public async Task Logout(string token)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var hash = TokenHash(token);
        await db.Sessions.Where(s => s.TokenHash == hash).ExecuteDeleteAsync();
    }

    public async Task<List<Character>> Characters(long account)
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Characters.Where(c => c.AccountId == account).OrderBy(c => c.Id).ToListAsync();
    }

    /// <summary>Creates a character at the start point; returns it, or an error message.</summary>
    public async Task<(Character? Character, string? Error)> CreateCharacter(long account, string name, int appearance)
    {
        if (!CharacterName().IsMatch(name ?? ""))
            return (null, "Character names are 2–12 letters or digits.");
        if (appearance is not (0 or 1))
            return (null, "Pick the hero or the heroine.");
        await using var db = await dbs.CreateDbContextAsync();
        if (await db.Characters.CountAsync(c => c.AccountId == account) >= config.CharacterSlots)
            return (null, $"This account can have {config.CharacterSlots} character(s).");
        var key = name!.ToLowerInvariant();
        if (await db.Characters.AnyAsync(c => c.NameKey == key))
            return (null, "That character name is taken.");
        var now = DateTime.UtcNow;
        var character = new Character
        {
            AccountId = account, Name = name, NameKey = key, Appearance = appearance,
            Zone = start.Zone, X = start.X, Y = start.Y, Facing = start.Facing, Created = now, LastSeen = now,
            StateJson = rules?.NewGameJson(),
        };
        db.Characters.Add(character);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return (null, "That character name is taken.");
        }
        return (character, null);
    }

    /// <summary>
    /// Writes characters' positions and changed game states in one transaction (the world calls this on map changes,
    /// logouts, important events and every save interval — the save plan in docs/ONLINE_TEST.md).
    /// </summary>
    public async Task SavePlayers(IEnumerable<PlayerSave> saves)
    {
        var list = saves.ToList();
        if (list.Count == 0)
            return;
        await using var db = await dbs.CreateDbContextAsync();
        var ids = list.Select(p => p.Character).ToList();
        var rows = await db.Characters.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id);
        var now = DateTime.UtcNow;
        foreach (var p in list)
            if (rows.TryGetValue(p.Character, out var c))
            {
                (c.Zone, c.X, c.Y, c.Facing, c.LastSeen) = (p.Zone, p.X, p.Y, p.Facing, now);
                if (p.StateJson is not null)
                    c.StateJson = p.StateJson;
            }
        await db.SaveChangesAsync();
    }

    // ---- Staff: roles, two-factor codes, the audit log ------------------------------------------------------------

    public async Task<Account?> ByName(string name)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var key = (name ?? "").ToLowerInvariant();
        return await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.NameKey == key);
    }

    public async Task<Account?> ById(long id)
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
    }

    public async Task<bool> CheckPassword(long account, string password)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var a = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == account);
        return a is not null && CryptographicOperations.FixedTimeEquals(Hash(password ?? "", a.PasswordSalt, a.PasswordIterations), a.PasswordHash);
    }

    /// <summary>Gives <paramref name="account"/> a new authenticator secret (not active until <see cref="ConfirmTotp"/>).</summary>
    public async Task<(string Secret, string Uri)?> BeginTotp(long account)
    {
        await using var db = await dbs.CreateDbContextAsync();
        if (await db.Accounts.FirstOrDefaultAsync(a => a.Id == account) is not { } a)
            return null;
        var secret = Totp.NewSecret();
        (a.TotpSecret, a.TotpEnabled, a.TotpLastStep) = (secret, false, 0);
        await db.SaveChangesAsync();
        return (Totp.Base32(secret), Totp.Uri(secret, a.Name, config.Name));
    }

    /// <summary>Turns two-factor on once a code from the new secret checks out.</summary>
    public Task<bool> ConfirmTotp(long account, string code) => CheckTotp(account, code, confirming: true);

    /// <summary>Checks a code from the account's authenticator (each code works once).</summary>
    public Task<bool> VerifyTotp(long account, string code) => CheckTotp(account, code, confirming: false);

    async Task<bool> CheckTotp(long account, string code, bool confirming)
    {
        await using var db = await dbs.CreateDbContextAsync();
        if (await db.Accounts.FirstOrDefaultAsync(a => a.Id == account) is not { TotpSecret: { } secret } a || a.TotpEnabled == confirming)
            return false;
        if (Totp.Verify(secret, code, DateTime.UtcNow, a.TotpLastStep) is not { } step)
            return false;
        a.TotpLastStep = step;
        a.TotpEnabled = true;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Sets an account's role (the command line's <c>admin grant</c>, and admins in game); null when it worked.</summary>
    public async Task<string?> SetRole(string accountName, Role role)
    {
        await using var db = await dbs.CreateDbContextAsync();
        var key = (accountName ?? "").ToLowerInvariant();
        if (await db.Accounts.FirstOrDefaultAsync(a => a.NameKey == key) is not { } a)
            return $"No account named {accountName}.";
        a.Role = role.ToString();
        await db.SaveChangesAsync();
        return null;
    }

    public async Task<List<(string Name, Role Role, bool TwoFactor)>> Staff()
    {
        await using var db = await dbs.CreateDbContextAsync();
        var rows = await db.Accounts.AsNoTracking().Where(a => a.Role != "Player").ToListAsync();
        return rows.Select(a => (a.Name, Permissions.Parse(a.Role), a.TotpEnabled)).OrderByDescending(x => x.Item2).ToList();
    }

    /// <summary>[admin] bootstrap_owner: makes that account Owner if it exists and there's no Owner yet.</summary>
    public async Task<string?> Bootstrap()
    {
        if (string.IsNullOrWhiteSpace(config.BootstrapOwner))
            return null;
        await using var db = await dbs.CreateDbContextAsync();
        if (await db.Accounts.AnyAsync(a => a.Role == "Owner"))
            return null;
        var key = config.BootstrapOwner.ToLowerInvariant();
        if (await db.Accounts.FirstOrDefaultAsync(a => a.NameKey == key) is not { } a)
            return $"[admin] bootstrap_owner: no account {config.BootstrapOwner} yet (register it, then restart).";
        a.Role = nameof(Role.Owner);
        await db.SaveChangesAsync();
        await Audit(a.Id, a.Name, "Server", "bootstrap", a.Name, "made Owner from [admin] bootstrap_owner");
        return $"{a.Name} is now the Owner.";
    }

    /// <summary>Mutes an account's chat until <paramref name="until"/> (null lifts it).</summary>
    public async Task SetMute(long account, DateTime? until, string? reason)
    {
        await using var db = await dbs.CreateDbContextAsync();
        if (await db.Accounts.FirstOrDefaultAsync(a => a.Id == account) is not { } a)
            return;
        (a.MutedUntil, a.MuteReason) = (until, until is null ? null : reason);
        await db.SaveChangesAsync();
    }

    /// <summary>Appends to the audit log (BUILD_SPEC §11.4).</summary>
    public async Task Audit(long account, string actor, string role, string action, string target, string details)
    {
        await using var db = await dbs.CreateDbContextAsync();
        db.Audit.Add(new AuditEntry { Time = DateTime.UtcNow, AccountId = account, Actor = actor, Role = role, Action = action, Target = target, Details = details });
        await db.SaveChangesAsync();
    }

    public async Task<List<AuditEntry>> RecentAudit(int count)
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Audit.AsNoTracking().OrderByDescending(e => e.Id).Take(count).ToListAsync();
    }

    static byte[] Hash(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    static byte[] TokenHash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
}
