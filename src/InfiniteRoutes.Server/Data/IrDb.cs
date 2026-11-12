using InfiniteRoutes.Server.Config;
using Microsoft.EntityFrameworkCore;

namespace InfiniteRoutes.Server.Data;

public sealed class Account
{
    public long Id { get; set; }
    /// <summary>As typed at registration.</summary>
    public string Name { get; set; } = "";
    /// <summary>Lower-case, for case-insensitive uniqueness.</summary>
    public string NameKey { get; set; } = "";
    public byte[] PasswordHash { get; set; } = [];
    public byte[] PasswordSalt { get; set; } = [];
    public int PasswordIterations { get; set; }
    public DateTime Created { get; set; }
    /// <summary>The staff role (BUILD_SPEC §11.1): Player, Helper, Moderator, GameMaster, Admin or Owner.</summary>
    public string Role { get; set; } = "Player";
    /// <summary>The authenticator secret (RFC 6238), once set up; <see cref="TotpEnabled"/> once a code confirmed it.</summary>
    public byte[]? TotpSecret { get; set; }
    public bool TotpEnabled { get; set; }
    /// <summary>The last 30-second step a code was accepted for (a code can't be used twice).</summary>
    public long TotpLastStep { get; set; }
    /// <summary>Chat is blocked until this time (UTC), for <see cref="MuteReason"/>; null when not muted.</summary>
    public DateTime? MutedUntil { get; set; }
    public string? MuteReason { get; set; }
    /// <summary>The account can't log in until this time (UTC; <see cref="DateTime.MaxValue"/> for good); null when not banned.</summary>
    public DateTime? BannedUntil { get; set; }
    public string? BanReason { get; set; }
    public List<Character> Characters { get; set; } = [];
}

/// <summary>A login: the SHA-256 of its token (the token itself is only ever with the client) and when it expires.</summary>
public sealed class Session
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public Account? Account { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public DateTime Created { get; set; }
    public DateTime Expires { get; set; }
}

/// <summary>A player character: its name and look, and where it is in the world.</summary>
public sealed class Character
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public Account? Account { get; set; }
    public string Name { get; set; } = "";
    public string NameKey { get; set; } = "";
    /// <summary>0 the hero, 1 the heroine.</summary>
    public int Appearance { get; set; }
    public int Zone { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Facing { get; set; }
    public DateTime Created { get; set; }
    public DateTime LastSeen { get; set; }
    /// <summary>The character's game (party, Bag, progress…) as <c>Core.Player.SaveGame</c> JSON; null before it has one.</summary>
    public string? StateJson { get; set; }
}

/// <summary>
/// One admin action (BUILD_SPEC §11.4): who, when, in which role, what, to whom, and the details. Append-only: the
/// server has no code that changes or deletes these rows.
/// </summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime Time { get; set; }
    public long AccountId { get; set; }
    public string Actor { get; set; } = "";
    public string Role { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Details { get; set; } = "";
    /// <summary>The <see cref="StateChange"/> this action made (0: none), which the log can revert.</summary>
    public long Change { get; set; }
}

/// <summary>A role (BUILD_SPEC §11.1): its rank and its comma-separated permissions. Built-in roles can't be deleted.</summary>
public sealed class RoleDef
{
    public string Name { get; set; } = "";
    public string NameKey { get; set; } = "";
    public int Rank { get; set; }
    public string Permissions { get; set; } = "";
    public bool Builtin { get; set; }
    /// <summary>The emblem shown over the heads of everyone in the role (<see cref="Core.Player.Beacons"/>); "" for none.</summary>
    public string Beacon { get; set; } = "";
}

/// <summary>
/// A staff edit to a character's game (BUILD_SPEC §11.4: every write is reversible): the state before and after. Revert
/// puts "before" back, if the state is still what the edit left ("after"); otherwise it says what changed since.
/// </summary>
public sealed class StateChange
{
    public long Id { get; set; }
    public DateTime Time { get; set; }
    public long CharacterId { get; set; }
    public string Character { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public bool Reverted { get; set; }
}

/// <summary>
/// The server's database. The provider is a setting: SQLite (a file, nothing to install) or PostgreSQL. The schema is
/// created on first start and upgraded in place by <see cref="Schema"/>.
/// </summary>
public sealed class IrDb(DbContextOptions<IrDb> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<RoleDef> Roles => Set<RoleDef>();
    public DbSet<StateChange> Changes => Set<StateChange>();
    public DbSet<Listing> Listings => Set<Listing>();
    public DbSet<TradeOffer> Trades => Set<TradeOffer>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<Block> Blocks => Set<Block>();
    public DbSet<Letter> Letters => Set<Letter>();
    public DbSet<DirectMessage> DirectMessages => Set<DirectMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>(e =>
        {
            e.HasIndex(a => a.NameKey).IsUnique();
            e.Property(a => a.Name).HasMaxLength(32);
            e.Property(a => a.NameKey).HasMaxLength(32);
        });
        b.Entity<AuditEntry>(e => e.ToTable("AuditEntries"));
        b.Entity<RoleDef>(e =>
        {
            e.ToTable("Roles");
            e.HasKey(r => r.Name);
            e.HasIndex(r => r.NameKey).IsUnique();
        });
        b.Entity<StateChange>(e => e.ToTable("StateChanges"));
        b.Entity<Listing>(e =>
        {
            e.HasIndex(l => l.Expires);
            e.HasIndex(l => l.Seller);
        });
        b.Entity<TradeOffer>(e =>
        {
            e.ToTable("Trades");
            e.HasIndex(t => t.To);
            e.HasIndex(t => t.From);
        });
        b.Entity<Friendship>(e => e.HasIndex(f => new { f.From, f.To }).IsUnique());
        b.Entity<Block>(e => e.HasIndex(x => new { x.CharacterId, x.BlockedId }).IsUnique());
        b.Entity<Letter>(e => e.HasIndex(l => l.To));
        b.Entity<DirectMessage>(e => e.HasIndex(m => new { m.To, m.From }));
        b.Entity<Session>(e =>
        {
            e.HasIndex(s => s.TokenHash).IsUnique();
            e.HasOne(s => s.Account).WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Character>(e =>
        {
            e.HasIndex(c => c.NameKey).IsUnique();
            e.Property(c => c.Name).HasMaxLength(32);
            e.Property(c => c.NameKey).HasMaxLength(32);
            e.HasOne(c => c.Account).WithMany(a => a.Characters).HasForeignKey(c => c.AccountId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>Options for the provider <paramref name="config"/> names.</summary>
    public static void Configure(DbContextOptionsBuilder options, ServerConfig config)
    {
        if (config.DatabaseProvider == "postgres")
            options.UseNpgsql(config.Postgres);
        else
        {
            var file = config.Resolve(config.Sqlite);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            options.UseSqlite($"Data Source={file}");
        }
    }
}

/// <summary>A Global Market listing (BUILD_SPEC §7.7). The goods are held here until sold, taken down or expired.</summary>
public sealed class Listing
{
    public long Id { get; set; }
    public long Seller { get; set; }
    public string SellerName { get; set; } = "";
    public int Item { get; set; }
    public int Count { get; set; }
    /// <summary>The Pokémon for sale as <c>PokemonSave</c> JSON (null for items).</summary>
    public string? Pokemon { get; set; }
    /// <summary>What it's searched by: the item or species name (and nickname).</summary>
    public string Name { get; set; } = "";
    public int Price { get; set; }
    public DateTime Listed { get; set; }
    public DateTime Expires { get; set; }
}

/// <summary>A trade offer from one player to another; the giver's side is held here until it's settled.</summary>
public sealed class TradeOffer
{
    public long Id { get; set; }
    public long From { get; set; }
    public string FromName { get; set; } = "";
    public long To { get; set; }
    public string ToName { get; set; } = "";
    public int GiveMoney { get; set; }
    public int GiveItem { get; set; }
    public int GiveCount { get; set; }
    public string? GivePokemon { get; set; }
    public int AskMoney { get; set; }
    public int AskItem { get; set; }
    public int AskCount { get; set; }
    /// <summary>-1 no Pokémon, 0 any, else that species.</summary>
    public int AskSpecies { get; set; } = -1;
    public string Note { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime Expires { get; set; }
}

/// <summary>A friendship between two characters: a request from <see cref="From"/> until <see cref="Accepted"/>.</summary>
public sealed class Friendship
{
    public long Id { get; set; }
    public long From { get; set; }
    public long To { get; set; }
    public bool Accepted { get; set; }
    public DateTime Created { get; set; }
}

/// <summary>A character someone has blocked: no messages, mail, whispers, friend requests or trade offers from them.</summary>
public sealed class Block
{
    public long Id { get; set; }
    public long CharacterId { get; set; }
    public long BlockedId { get; set; }
    public string BlockedName { get; set; } = "";
}

/// <summary>In-game mail: from a player or the game (market, trades), with at most one of each attachment.</summary>
public sealed class Letter
{
    public long Id { get; set; }
    public long To { get; set; }
    /// <summary>The sending character, or 0 for the game itself.</summary>
    public long FromId { get; set; }
    public string From { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime Sent { get; set; }
    public bool Read { get; set; }
    public int Money { get; set; }
    public int Item { get; set; }
    public int Count { get; set; }
    public string? Pokemon { get; set; }
    public bool Claimed { get; set; }
}

/// <summary>A direct message between two characters, kept so it can be read later.</summary>
public sealed class DirectMessage
{
    public long Id { get; set; }
    public long From { get; set; }
    public long To { get; set; }
    public string Text { get; set; } = "";
    public DateTime Sent { get; set; }
    public bool Read { get; set; }
}
