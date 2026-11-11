using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;
using InfiniteRoutes.Server.Accounts;
using InfiniteRoutes.Server.Admin;
using InfiniteRoutes.Server.Data;
using InfiniteRoutes.Server.Game;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InfiniteRoutes.Tests;

public class AdminChatTests
{
    static async Task<(TestClient Client, Welcome Welcome)> Join(TestServer server, string name)
    {
        var (token, character) = await server.NewPlayer(name);
        var client = await TestClient.Connect(server.GamePort);
        await client.Send(new Hello(Protocol.Version, token, character));
        return (client, await client.Expect<Welcome>());
    }

    [Fact]
    public async Task Chat_reaches_the_right_players_and_is_rate_limited()
    {
        await using var server = await TestServer.Start(channels: 2, extra: "[chat]\nlines = 3\nwindow_seconds = 30");
        var (a, _) = await Join(server, "Alder");
        var (b, _) = await Join(server, "Bianca");
        await using var _a = a;
        await using var _b = b;
        await a.Expect<PlayersAppeared>();

        await a.Send(new Say(ChatKind.Local, "  hi\u0007 there  "));
        Assert.Equal(new ChatLine(ChatKind.Local, "AlderC", "hi there"), await b.Expect<ChatLine>());
        await a.Expect<ChatLine>();   // their own line comes back too

        await b.Send(new Say(ChatKind.Whisper, "psst", "alderc"));
        Assert.Equal(new ChatLine(ChatKind.Whisper, "BiancaC", "psst", "AlderC"), await a.Expect<ChatLine>());
        Assert.Equal("AlderC", (await b.Expect<ChatLine>(l => l.Kind == ChatKind.Whisper)).To);
        await b.Send(new Say(ChatKind.Whisper, "hello?", "Nobody"));
        Assert.Contains("isn't online", (await b.Expect<ChatLine>(l => l.Kind == ChatKind.System)).Text);

        // On another channel local chat no longer reaches them, global still does.
        await b.Send(new SwitchChannel(2));
        await b.Expect<Notice>();
        await a.Expect<PlayersLeft>();
        await a.Send(new Say(ChatKind.Local, "anyone?"));
        await a.Send(new Say(ChatKind.Global, "hello world"));
        Assert.Equal("hello world", (await b.Expect<ChatLine>(l => l.Kind == ChatKind.Global)).Text);
        await a.Send(new Say(ChatKind.Global, "and again"));
        Assert.Contains("too fast", (await a.Expect<ChatLine>(l => l.Kind == ChatKind.System)).Text);   // a fourth line in the window
    }

    [Fact]
    public void Totp_matches_the_rfc_test_vector_and_refuses_reuse()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal("287082", Totp.Code(secret, 1));   // RFC 6238 at T = 59 s: 94287082 (last 6 digits)
        var now = DateTimeOffset.FromUnixTimeSeconds(59).UtcDateTime;
        Assert.Equal(1, Totp.Verify(secret, "287 082", now, lastUsed: 0));
        Assert.Null(Totp.Verify(secret, "287082", now, lastUsed: 1));   // already used
        Assert.Null(Totp.Verify(secret, "000000", now, lastUsed: 0));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32(secret));
    }

    [Fact]
    public void Roles_include_lower_ones_and_cannot_grant_upward()
    {
        static RoleInfo R(Role r) => new(r.ToString(), Permissions.BuiltinRank(r), true, Permissions.Defaults(r).ToHashSet());
        Assert.True(R(Role.Moderator).Has(Permissions.Who));
        Assert.False(R(Role.Moderator).Has(Permissions.Teleport));
        Assert.True(R(Role.Owner).Has(Permissions.Teleport));   // Owners have everything, stored or not
        Assert.True(Permissions.CanGrant(R(Role.Admin), R(Role.Player), R(Role.GameMaster)));
        Assert.False(Permissions.CanGrant(R(Role.Admin), R(Role.Player), R(Role.Admin)));
        Assert.False(Permissions.CanGrant(R(Role.Admin), R(Role.Owner), R(Role.Player)));
        Assert.False(Permissions.CanGrant(R(Role.GameMaster), R(Role.Player), R(Role.Helper)));
        Assert.True(Permissions.CanGrant(R(Role.Owner), R(Role.Admin), R(Role.Owner)));
        Assert.Equal(Permissions.Catalog.Count, R(Role.Owner).All.Count);
    }

    [Fact]
    public async Task Admin_mode_needs_a_role_the_password_and_a_code_and_its_commands_are_checked_and_audited()
    {
        await using var server = await TestServer.Start(channels: 1);
        var accounts = server.App.Services.GetRequiredService<AccountService>();
        var (admin, adminWelcome) = await Join(server, "Owen");
        var (player, playerWelcome) = await Join(server, "Pat");
        await using var _x = admin;
        await using var _y = player;
        await admin.Expect<PlayersAppeared>();

        // A player can't elevate or run commands.
        await player.Send(new AdminCommand("who"));
        Assert.Contains("no staff role", (await player.Expect<AdminReply>()).Lines[0]);

        // Make Owen a Game Master (as `ir-server admin grant` does); without two-factor, Admin Mode is refused.
        await accounts.SetRole("Owen", Role.GameMaster);
        await admin.DisposeAsync();
        var token = await Login(server, "Owen");
        admin = await TestClient.Connect(server.GamePort);
        var characters = await Get(server, token, "/api/characters");
        await admin.Send(new Hello(Protocol.Version, token, characters[0].GetProperty("id").GetInt64()));
        await admin.Expect<Welcome>();
        Assert.Equal(("GameMaster", false), (await admin.Expect<AdminStatus>()) is var st ? (st.Role, st.Elevated) : default);
        await admin.Send(new Elevate("password123", "000000"));
        Assert.Contains("two-factor", (await admin.Expect<AdminReply>()).Lines[0]);

        // Set up two-factor through the API.
        var setup = await Post(server, token, "/api/2fa/setup", new { });
        var account = (await accounts.ByName("Owen"))!;
        var code = Totp.Code(account.TotpSecret!, Totp.StepAt(DateTime.UtcNow));
        Assert.Equal(account.TotpSecret, Base32Decode(setup.GetProperty("secret").GetString()!));
        await Post(server, token, "/api/2fa/confirm", new { code });

        await admin.Send(new Elevate("wrong-password", code));
        Assert.False((await admin.Expect<AdminReply>()).Ok);
        await admin.Send(new Elevate("password123", code));   // already used to confirm
        Assert.False((await admin.Expect<AdminReply>()).Ok);
        // The next code (one step ahead is accepted for clock drift).
        await admin.Send(new Elevate("password123", Totp.Code(account.TotpSecret!, Totp.StepAt(DateTime.UtcNow) + 1)));
        Assert.True((await admin.Expect<AdminReply>()).Ok);
        Assert.True((await admin.Expect<AdminStatus>(s => s.Elevated)).Elevated);

        await admin.Send(new AdminCommand("who"));
        Assert.Contains((await admin.Expect<AdminReply>()).Lines, l => l.StartsWith("PatC"));
        await admin.Send(new AdminCommand("grant Pat Moderator"));   // a Game Master can't grant roles
        Assert.Contains("can't use grant", (await admin.Expect<AdminReply>()).Lines[0]);

        // Invisible: Pat stops seeing Owen.
        await player.Expect<PlayersAppeared>();
        await admin.Send(new AdminCommand("invisible on"));
        await admin.Expect<AdminReply>();
        await player.Expect<PlayersLeft>();
        await admin.Send(new Say(ChatKind.Global, "staff here"));
        Assert.True((await player.Expect<ChatLine>()).Staff);

        // Summon brings Pat to Owen; kick needs a reason, then disconnects Pat.
        await admin.Send(new AdminCommand("summon Pat"));
        Assert.Equal(0, (await player.Expect<Warped>()).Seq);
        await admin.Send(new AdminCommand("kick Pat"));
        Assert.Contains("reason", (await admin.Expect<AdminReply>(r => !r.Ok)).Lines[0]);
        await admin.Send(new AdminCommand("kick Pat spamming"));
        Assert.Contains("spamming", (await player.Expect<Notice>(n => n.Text.Contains("disconnected"))).Text);

        // Everything went in the audit log.
        var log = await accounts.RecentAudit(50);
        Assert.Contains(log, e => e is { Action: "elevate-failed", Actor: "Owen" });
        Assert.Contains(log, e => e is { Action: "elevate", Actor: "Owen", Role: "GameMaster" });
        Assert.Contains(log, e => e is { Action: "denied", Target: "grant" });
        Assert.Contains(log, e => e is { Action: "kick", Target: "PatC", Details: "spamming" });
        Assert.Contains(log, e => e.Action == "2fa-on");
        await admin.DisposeAsync();
    }

    [Fact]
    public async Task Characters_get_a_new_game_on_the_server_and_map_changes_save_at_once()
    {
        await using var server = await TestServer.Start(channels: 1, rules: Rules());
        var (token, character) = await server.NewPlayer("Hilbert");
        await using var client = await TestClient.Connect(server.GamePort);
        await client.Send(new Hello(Protocol.Version, token, character));
        await client.Expect<Welcome>();
        var save = SaveGame.FromJson((await client.Expect<StateSnapshot>()).Json)!;
        Assert.Equal(StarterKit.Species, save.Party.Single().Species);
        Assert.Contains(save.Bag, i => i.Item == ItemEffect.PokeBall && i.Count == 10);

        // Walk to the door and in: the map change is written without waiting for the batch timer or leaving.
        var seq = 0;
        foreach (var (dx, dy, n) in new[] { (1, 0, 9), (0, 1, 9), (-1, 0, 1) })
            for (var i = 0; i < n; i++)
            {
                await client.Send(new Step(++seq, dx, dy, false));
                Assert.True((await client.Expect<StepResult>(r => r.Seq == seq)).Accepted);
                await Task.Delay(190);
            }
        await client.Send(new Warp(99, 10, 10));
        await client.Expect<Warped>();
        for (var i = 0; i < 30 && (await Get(server, token, "/api/characters"))[0].GetProperty("zone").GetInt32() != 2; i++)
            await Task.Delay(100);
        Assert.Equal(2, (await Get(server, token, "/api/characters"))[0].GetProperty("zone").GetInt32());
    }

    [Fact]
    public void An_old_database_is_upgraded_in_place()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("ir-schema").FullName, "old.db");
        using (var old = new SqliteConnection($"Data Source={file}"))
        {
            old.Open();
            var cmd = old.CreateCommand();
            // The first slice's tables, as that server made them.
            cmd.CommandText = """
                CREATE TABLE "Accounts" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Accounts" PRIMARY KEY AUTOINCREMENT, "Name" TEXT NOT NULL, "NameKey" TEXT NOT NULL, "PasswordHash" BLOB NOT NULL, "PasswordSalt" BLOB NOT NULL, "PasswordIterations" INTEGER NOT NULL, "Created" TEXT NOT NULL);
                CREATE TABLE "Characters" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Characters" PRIMARY KEY AUTOINCREMENT, "AccountId" INTEGER NOT NULL, "Name" TEXT NOT NULL, "NameKey" TEXT NOT NULL, "Appearance" INTEGER NOT NULL, "Zone" INTEGER NOT NULL, "X" INTEGER NOT NULL, "Y" INTEGER NOT NULL, "Facing" INTEGER NOT NULL, "Created" TEXT NOT NULL, "LastSeen" TEXT NOT NULL);
                CREATE TABLE "Sessions" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Sessions" PRIMARY KEY AUTOINCREMENT, "AccountId" INTEGER NOT NULL, "TokenHash" BLOB NOT NULL, "Created" TEXT NOT NULL, "Expires" TEXT NOT NULL);
                INSERT INTO "Accounts" VALUES (1, 'Ash', 'ash', x'00', x'00', 1, '2026-11-10 00:00:00');
                """;
            cmd.ExecuteNonQuery();
        }
        var options = new DbContextOptionsBuilder<IrDb>().UseSqlite($"Data Source={file}").Options;
        using (var db = new IrDb(options))
        {
            Assert.Equal(1, Schema.Ensure(db));
            var ash = db.Accounts.Single();
            Assert.Equal(("Ash", "Player", false), (ash.Name, ash.Role, ash.TotpEnabled));
            db.Audit.Add(new AuditEntry { Actor = "test", Action = "check", Time = DateTime.UtcNow });
            db.SaveChanges();
        }
        using (var db = new IrDb(options))
            Assert.Equal(Schema.Current, Schema.Ensure(db));   // already current: nothing to do
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Mutes_stick_across_logins_spectators_follow_their_target_and_the_panel_lists_players()
    {
        await using var server = await TestServer.Start(channels: 1, extra: "[admin]\nrequire_2fa = false");
        var accounts = server.App.Services.GetRequiredService<AccountService>();
        var (token, character) = await server.NewPlayer("Gwen");
        await accounts.SetRole("Gwen", Role.GameMaster);
        await using var staff = await TestClient.Connect(server.GamePort);
        await staff.Send(new Hello(Protocol.Version, token, character));
        await staff.Expect<Welcome>();
        await staff.Send(new Elevate("password123", ""));
        Assert.True((await staff.Expect<AdminReply>()).Ok);

        var (tokenP, characterP) = await server.NewPlayer("Pat");
        var player = await TestClient.Connect(server.GamePort);
        await player.Send(new Hello(Protocol.Version, tokenP, characterP));
        var welcome = await player.Expect<Welcome>();

        // The panel's list.
        await staff.Send(new AdminCommand("players"));
        var rows = (await staff.Expect<AdminPlayers>()).Players;
        Assert.Contains(rows, r => r is { Name: "PatC", Account: "Pat", Role: "Player", MutedUntil: null });
        Assert.Contains(rows, r => r is { Name: "GwenC", Elevated: true });

        // Mute needs minutes and a reason; it holds after logging out and in.
        await staff.Send(new AdminCommand("mute Pat 10"));
        Assert.False((await staff.Expect<AdminReply>()).Ok);
        await staff.Send(new AdminCommand("mute Pat 10 shouting"));
        Assert.True((await staff.Expect<AdminReply>(r => r.Ok)).Ok);
        Assert.Contains("muted you", (await player.Expect<ChatLine>(l => l.Kind == ChatKind.System)).Text);
        await player.DisposeAsync();
        player = await TestClient.Connect(server.GamePort);
        await player.Send(new Hello(Protocol.Version, tokenP, characterP));
        welcome = await player.Expect<Welcome>();
        await player.Send(new Say(ChatKind.Global, "let me talk"));
        Assert.Contains("muted until", (await player.Expect<ChatLine>(l => l.Kind == ChatKind.System)).Text);
        await staff.Send(new AdminCommand("unmute Pat"));
        await player.Expect<ChatLine>(l => l.Text.Contains("chat again"));
        await player.Send(new Say(ChatKind.Global, "thanks"));
        Assert.Equal("thanks", (await staff.Expect<ChatLine>(l => l.Kind == ChatKind.Global)).Text);

        // Inspect answers with a topic the inspector window can pick up.
        await staff.Send(new AdminCommand("inspect Pat"));
        Assert.Equal("inspect:PatC", (await staff.Expect<AdminReply>(r => r.Topic.Length > 0)).Topic);

        // Spectate: the camera follows Pat, Gwen goes invisible and can't walk, and follows Pat through the door.
        await staff.Send(new AdminCommand("spectate Pat"));
        Assert.Equal(new SpectateStatus(welcome.PlayerId, "PatC"), await staff.Expect<SpectateStatus>(s => s.TargetId != 0));
        Assert.True((await staff.Expect<AdminStatus>(s => s.Invisible)).Invisible);
        await staff.Send(new Step(1, 1, 0, false));
        Assert.False((await staff.Expect<StepResult>()).Accepted);
        var seq = 0;
        foreach (var (dx, dy, n) in new[] { (1, 0, 9), (0, 1, 9), (-1, 0, 1) })
            for (var i = 0; i < n; i++)
            {
                await player.Send(new Step(++seq, dx, dy, false));
                await player.Expect<StepResult>(r => r.Seq == seq);
                await Task.Delay(190);
            }
        await player.Send(new Warp(99, 10, 10));
        await player.Expect<Warped>();
        Assert.Equal(2, (await staff.Expect<Warped>(w => w.Seq == 0 && w.Location.Zone == 2)).Location.Zone);
        await staff.Send(new AdminCommand("spectate off"));
        Assert.Equal(0, (await staff.Expect<SpectateStatus>(s => s.TargetId == 0)).TargetId);

        var log = await accounts.RecentAudit(50);
        Assert.Contains(log, e => e is { Action: "mute", Target: "PatC", Details: "10 min: shouting" });
        Assert.Contains(log, e => e is { Action: "spectate", Target: "PatC" });
        await player.DisposeAsync();
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    internal static GameRules Rules()
    {
        var types = new[] { TypeChart.Grass };
        var snivy = new SpeciesInfo(StarterKit.Species, "Snivy", new(45, 45, 55, 63, 45, 55), ["Grass"], 45, new(0, 0, 0, 0, 0, 0), 31, 20, 70, 3, [1],
            ["Overgrow"], null, 28, 6, 81, 1, types);
        var tackle = new MoveInfo(33, "Tackle", TypeChart.Normal, MoveKind.Damage, MoveCategory.Physical, 50, 100, 35, 0, 1, 1, Ailment.None, 0, 0, 0, 0, 0, 0, 0, [], 0);
        var learnsets = Enumerable.Range(0, StarterKit.Species + 1).Select(i => (IReadOnlyList<LevelMove>)(i == StarterKit.Species ? [new(1, 33)] : [])).ToList();
        return new GameRules(new BattleData([snivy], [tackle], learnsets), [], "white");
    }

    static async Task<string> Login(TestServer server, string name)
    {
        var login = await (await server.Http.PostAsJsonAsync("/api/login", new { name, password = "password123" })).Content.ReadFromJsonAsync<JsonElement>();
        return login.GetProperty("token").GetString()!;
    }

    static async Task<JsonElement> Get(TestServer server, string token, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path) { Headers = { Authorization = new("Bearer", token) } };
        return await (await server.Http.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
    }

    static async Task<JsonElement> Post(TestServer server, string token, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body), Headers = { Authorization = new("Bearer", token) } };
        var response = await server.Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    static byte[] Base32Decode(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. bytes];
    }
}
