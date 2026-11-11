using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using InfiniteRoutes.Core.Config;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Extractor.Extraction;
using InfiniteRoutes.Server;
using InfiniteRoutes.Server.Accounts;
using InfiniteRoutes.Server.Config;
using InfiniteRoutes.Server.World;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InfiniteRoutes.Tests;

/// <summary>
/// A tiny world: zone 1 is a 20×20 shared map "field" with a wall down x = 5 (but for a gap at y = 2) and a door at
/// (10, 10) into zone 2, an 8×8 room whose mat at (3, 7) leads back out in front of the door.
/// </summary>
sealed class FakeWorld : IWorldData
{
    public StartPoint Start { get; } = new(1, 2, 2, 0);

    public string? MapOf(int zone) => zone switch { 1 => "field", 2 => "room", _ => null };
    public bool IsShared(string map) => map == "field";
    public int? ZoneAt(string map, int x, int y) => map == "field" ? 1 : null;

    public bool Blocked(int zone, int x, int y) => zone switch
    {
        1 => x < 0 || y < 0 || x >= 20 || y >= 20 || (x == 5 && y != 2) || (x, y) == (10, 10),
        2 => x < 0 || y < 0 || x >= 8 || y >= 8,
        _ => true,
    };

    static readonly WarpInfo Door = new(10, 10, 1, 1, 2, 0, null, WarpKind.Door);
    static readonly WarpInfo Mat = new(3, 7, 1, 1, 1, 0, Facing.Down, WarpKind.Mat);

    public WarpInfo? WarpAt(int zone, int x, int y) => (zone, x, y) switch
    {
        (1, 10, 10) => Door,
        (2, 3, 7) => Mat,
        _ => null,
    };

    public (int Zone, int X, int Y, int Facing)? Destination(WarpInfo warp) =>
        warp == Door ? (2, 3, 7, 1) : (1, 10, 11, 0);
}

/// <summary>The server running in-process on free ports, with its own temporary folder and SQLite database.</summary>
sealed class TestServer : IAsyncDisposable
{
    public WebApplication App { get; }
    public HttpClient Http { get; }
    public int GamePort { get; private set; }
    readonly string _dir;

    TestServer(WebApplication app, string dir, HttpClient http)
    {
        App = app;
        _dir = dir;
        Http = http;
    }

    public static async Task<TestServer> Start(int idleMinutes = 25, int channels = 2, int saveSeconds = 45, string extra = "", InfiniteRoutes.Server.Game.GameRules? rules = null)
    {
        var dir = Directory.CreateTempSubdirectory("ir-server").FullName;
        var config = ServerConfig.FromTable(Pkir.Parse($"""
            [server]
            bind_address = "127.0.0.1"
            game_port = 0
            api_port = 0
            name = "Test"
            [world]
            channels = {channels}
            idle_minutes = {idleMinutes}
            save_interval_seconds = {saveSeconds}
            {extra}
            """), dir);
        var app = ServerHost.Build(config, new FakeWorld(), l => l.SetMinimumLevel(LogLevel.Warning), rules);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true })
        {
            BaseAddress = new Uri(address.Replace("0.0.0.0", "127.0.0.1")),
        };
        var server = new TestServer(app, dir, http);
        server.GamePort = await app.Services.GetRequiredService<InfiniteRoutes.Server.Net.GameListener>().Port;
        return server;
    }

    /// <summary>Registers, logs in and makes a character; returns the token and character id.</summary>
    public async Task<(string Token, long Character)> NewPlayer(string name)
    {
        (await Http.PostAsJsonAsync("/api/register", new { name, password = "password123" })).EnsureSuccessStatusCode();
        var login = await (await Http.PostAsJsonAsync("/api/login", new { name, password = "password123" })).Content.ReadFromJsonAsync<JsonElement>();
        var token = login.GetProperty("token").GetString()!;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/characters") { Content = JsonContent.Create(new { name = name + "C", appearance = 0 }) };
        request.Headers.Authorization = new("Bearer", token);
        var character = await (await Http.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
        return (token, character.GetProperty("id").GetInt64());
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        Http.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A test client on the game port: sends messages and collects what comes back.</summary>
sealed class TestClient : IAsyncDisposable
{
    readonly TcpClient _tcp = new();
    SslStream _tls = null!;
    readonly List<Message> _inbox = [];
    readonly SemaphoreSlim _arrived = new(0);
    Task? _reader;

    public static async Task<TestClient> Connect(int port)
    {
        var client = new TestClient();
        await client._tcp.ConnectAsync("127.0.0.1", port);
        client._tls = new SslStream(client._tcp.GetStream(), false, (_, _, _, _) => true);
        await client._tls.AuthenticateAsClientAsync("localhost");
        client._reader = Task.Run(client.Read);
        return client;
    }

    async Task Read()
    {
        try
        {
            while (await Frames.ReadAsync(_tls) is { } m)
            {
                lock (_inbox)
                    _inbox.Add(m);
                _arrived.Release();
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException)
        {
        }
        _arrived.Release();
    }

    public Task Send(Message m) => Frames.WriteAsync(_tls, m);

    /// <summary>Waits for the first message matching <paramref name="match"/> (and removes it), or fails after a few seconds.</summary>
    public async Task<T> Expect<T>(Func<T, bool>? match = null) where T : Message
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (_inbox)
            {
                var found = _inbox.OfType<T>().FirstOrDefault(m => match?.Invoke(m) ?? true);
                if (found is not null)
                {
                    _inbox.Remove(found);
                    return found;
                }
            }
            await _arrived.WaitAsync(TimeSpan.FromMilliseconds(100));
        }
        throw new TimeoutException($"No {typeof(T).Name} arrived.");
    }

    public bool Closed => _reader?.IsCompleted ?? false;

    public async ValueTask DisposeAsync()
    {
        _tcp.Dispose();
        if (_reader is not null)
            await _reader;
    }
}

public class ServerTests
{
    [Fact]
    public void Config_template_loads_and_mistakes_are_listed()
    {
        var config = ServerConfig.FromTable(Pkir.Parse(ServerConfig.Template), "/srv");
        Assert.Equal((7450, 7451, "sqlite", 2, 25), (config.GamePort, config.ApiPort, config.DatabaseProvider, config.Channels, config.IdleMinutes));
        Assert.Equal(Path.GetFullPath("/srv/data/ir.db"), config.Resolve(config.Sqlite));

        var error = Assert.Throws<ServerConfigException>(() => ServerConfig.FromTable(Pkir.Parse("""
            [server]
            game_port = 70000
            name = 5
            [database]
            provider = "mongo"
            postgres = "${ENV:IR_TEST_SURELY_UNSET}"
            """), "."));
        Assert.Equal(4, error.Errors.Count);
        Assert.Contains(error.Errors, e => e.Contains("game_port"));
        Assert.Contains(error.Errors, e => e.Contains("IR_TEST_SURELY_UNSET"));

        Environment.SetEnvironmentVariable("IR_TEST_DB", "Host=db");
        var pg = ServerConfig.FromTable(Pkir.Parse("[database]\nprovider = \"postgres\"\npostgres = \"${ENV:IR_TEST_DB};Database=ir\""), ".");
        Assert.Equal("Host=db;Database=ir", pg.Postgres);
    }

    [Fact]
    public async Task Accounts_register_log_in_and_make_characters()
    {
        await using var server = await TestServer.Start();
        var http = server.Http;
        Assert.Equal(400, (int)(await http.PostAsJsonAsync("/api/register", new { name = "a", password = "password123" })).StatusCode);
        Assert.Equal(400, (int)(await http.PostAsJsonAsync("/api/register", new { name = "Ash", password = "short" })).StatusCode);
        (await http.PostAsJsonAsync("/api/register", new { name = "Ash", password = "password123" })).EnsureSuccessStatusCode();
        Assert.Equal(400, (int)(await http.PostAsJsonAsync("/api/register", new { name = "ASH", password = "password123" })).StatusCode);
        Assert.Equal(401, (int)(await http.PostAsJsonAsync("/api/login", new { name = "Ash", password = "wrongpassword" })).StatusCode);
        Assert.Equal(401, (int)(await http.GetAsync("/api/characters")).StatusCode);

        var login = await (await http.PostAsJsonAsync("/api/login", new { name = "ash", password = "password123" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ash", login.GetProperty("account").GetString());
        var token = login.GetProperty("token").GetString()!;
        async Task<HttpResponseMessage> Create(string name)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/characters") { Content = JsonContent.Create(new { name, appearance = 1 }) };
            request.Headers.Authorization = new("Bearer", token);
            return await http.SendAsync(request);
        }
        var made = await (await Create("Hilda")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Hilda", 1, 1, 2, 2), (made.GetProperty("name").GetString(), made.GetProperty("appearance").GetInt32(),
            made.GetProperty("zone").GetInt32(), made.GetProperty("x").GetInt32(), made.GetProperty("y").GetInt32()));
        Assert.Equal(400, (int)(await Create("Second")).StatusCode);   // one slot

        var info = await http.GetFromJsonAsync<JsonElement>("/api/info");
        Assert.Equal(server.GamePort, info.GetProperty("gamePort").GetInt32());
    }

    [Fact]
    public async Task Players_see_each_other_walk_and_the_server_checks_every_step()
    {
        await using var server = await TestServer.Start(channels: 1);
        var (tokenA, charA) = await server.NewPlayer("Alice");
        var (tokenB, charB) = await server.NewPlayer("Bianca");

        await using var a = await TestClient.Connect(server.GamePort);
        await a.Send(new Hello(Protocol.Version, tokenA, charA));
        var welcomeA = await a.Expect<Welcome>();
        Assert.Equal(new NetLocation(1, 2, 2, 0), welcomeA.Location);

        await using var b = await TestClient.Connect(server.GamePort);
        await b.Send(new Hello(Protocol.Version, tokenB, charB));
        var welcomeB = await b.Expect<Welcome>();
        Assert.Contains((await a.Expect<PlayersAppeared>()).Players, p => p.Id == welcomeB.PlayerId && p.Name == "BiancaC");
        Assert.Contains((await b.Expect<PlayersAppeared>()).Players, p => p.Id == welcomeA.PlayerId);

        // B walks right along y = 2 (through the gap in the wall); A sees it.
        await b.Send(new Step(1, 1, 0, false));
        Assert.Equal(new StepResult(1, true, new NetLocation(1, 3, 2, 3)), await b.Expect<StepResult>());
        var moved = await a.Expect<PlayersMoved>();
        Assert.Contains(moved.Moves, m => m.Id == welcomeB.PlayerId && (m.X, m.Y) == (3, 2));

        // Into the wall: refused, put back where it is.
        await Task.Delay(300);
        await b.Send(new Step(2, 1, 0, false));
        await Task.Delay(300);
        await b.Send(new Step(3, 1, 0, false));   // onto the gap at (5, 2)
        await Task.Delay(300);
        await b.Send(new Step(5, 0, 1, false));   // (5, 3) is wall
        Assert.True((await b.Expect<StepResult>(r => r.Seq == 5)) is { Accepted: false, Location: { X: 5, Y: 2 } });

        // Steps faster than running pace are refused once the budget runs out.
        var results = new List<StepResult>();
        for (var i = 0; i < 12; i++)
            await b.Send(new Step(10 + i, -1 + 2 * (i % 2), 0, true));   // back and forth through the gap
        for (var i = 0; i < 12; i++)
            results.Add(await b.Expect<StepResult>(r => r.Seq == 10 + i));
        Assert.Contains(results, r => !r.Accepted);
        Assert.Contains(results, r => r.Accepted);
    }

    [Fact]
    public async Task Warps_need_the_right_tile_and_move_players_out_of_sight()
    {
        await using var server = await TestServer.Start(channels: 1);
        var (tokenA, charA) = await server.NewPlayer("Alice");
        var (tokenB, charB) = await server.NewPlayer("Bianca");
        await using var a = await TestClient.Connect(server.GamePort);
        await a.Send(new Hello(Protocol.Version, tokenA, charA));
        await a.Expect<Welcome>();
        await using var b = await TestClient.Connect(server.GamePort);
        await b.Send(new Hello(Protocol.Version, tokenB, charB));
        var welcomeB = await b.Expect<Welcome>();
        await a.Expect<PlayersAppeared>();

        // Too far from the door: refused.
        await b.Send(new Warp(1, 10, 10));
        Assert.False((await b.Expect<StepResult>(r => r.Seq == 1)).Accepted);

        // Walk to (10, 11), in front of the door (which is solid): right along y = 2, down x = 11, then left.
        var seq = 100;
        async Task Walk(int dx, int dy, int steps)
        {
            for (var i = 0; i < steps; i++)
            {
                await b.Send(new Step(++seq, dx, dy, false));
                Assert.True((await b.Expect<StepResult>(r => r.Seq == seq)).Accepted, $"step {seq}");
                await Task.Delay(190);
            }
        }
        await Walk(1, 0, 9);
        await Walk(0, 1, 9);
        await Walk(-1, 0, 1);
        await b.Send(new Warp(500, 10, 10));
        Assert.Equal(new NetLocation(2, 3, 7, 1), (await b.Expect<Warped>()).Location);
        Assert.Contains(welcomeB.PlayerId, (await a.Expect<PlayersLeft>()).Ids);

        // The mat is taken standing on it.
        await b.Send(new Warp(501, 3, 7));
        Assert.Equal(new NetLocation(1, 10, 11, 0), (await b.Expect<Warped>()).Location);
        Assert.Contains((await a.Expect<PlayersAppeared>()).Players, p => p.Id == welcomeB.PlayerId);
    }

    [Fact]
    public async Task Bad_tokens_are_refused_and_a_second_login_replaces_the_first_and_positions_are_kept()
    {
        await using var server = await TestServer.Start(channels: 1);
        var (token, character) = await server.NewPlayer("Cheren");

        await using (var bad = await TestClient.Connect(server.GamePort))
        {
            await bad.Send(new Hello(Protocol.Version, "nonsense", character));
            Assert.Contains("expired", (await bad.Expect<Refused>()).Reason);
        }
        await using (var old = await TestClient.Connect(server.GamePort))
        {
            await old.Send(new Hello(Protocol.Version - 1, token, character));
            Assert.Contains("protocol", (await old.Expect<Refused>()).Reason);
        }

        var first = await TestClient.Connect(server.GamePort);
        await first.Send(new Hello(Protocol.Version, token, character));
        await first.Expect<Welcome>();
        await first.Send(new Step(1, 1, 0, false));
        await first.Expect<StepResult>();

        await using var second = await TestClient.Connect(server.GamePort);
        await second.Send(new Hello(Protocol.Version, token, character));
        Assert.Equal(new NetLocation(1, 3, 2, 3), (await second.Expect<Welcome>()).Location);
        Assert.Contains("somewhere else", (await first.Expect<Notice>()).Text);
        await first.DisposeAsync();

        await second.Send(new Step(2, 1, 0, false));
        await second.Expect<StepResult>();
        await second.DisposeAsync();
        // Leaving writes the position: the character list shows it.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/characters");
        request.Headers.Authorization = new("Bearer", token);
        for (var i = 0; i < 30; i++)
        {
            var list = await (await server.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/characters") { Headers = { Authorization = new("Bearer", token) } }))
                .Content.ReadFromJsonAsync<JsonElement>();
            if (list[0].GetProperty("x").GetInt32() == 4)
                return;
            await Task.Delay(100);
        }
        Assert.Fail("The position wasn't saved after leaving.");
    }

    [Fact]
    public async Task Players_fill_the_first_channel_so_they_meet()
    {
        await using var server = await TestServer.Start(channels: 3);
        var (tokenA, charA) = await server.NewPlayer("Alder");
        var (tokenB, charB) = await server.NewPlayer("Bianca");
        await using var a = await TestClient.Connect(server.GamePort);
        await a.Send(new Hello(Protocol.Version, tokenA, charA));
        await using var b = await TestClient.Connect(server.GamePort);
        await b.Send(new Hello(Protocol.Version, tokenB, charB));
        var (welcomeA, welcomeB) = (await a.Expect<Welcome>(), await b.Expect<Welcome>());
        Assert.Equal((1, 1, 3), (welcomeA.Channel, welcomeB.Channel, welcomeB.Channels));

        // Switching channel takes B out of A's sight.
        await a.Expect<PlayersAppeared>();
        await b.Send(new SwitchChannel(2));
        Assert.Equal(2, (await b.Expect<Notice>()).Channel);
        await a.Expect<PlayersLeft>();
    }

    [Fact]
    public void Server_list_round_trips_and_reads_addresses()
    {
        Assert.Equal(("example.org", 7451), ServerEntry.ParseAddress("example.org"));
        Assert.Equal(("192.168.1.50", 9000), ServerEntry.ParseAddress(" 192.168.1.50:9000 "));
        Assert.Equal(("::1", 7451), ServerEntry.ParseAddress("[::1]"));
        Assert.Equal(("::1", 8000), ServerEntry.ParseAddress("[::1]:8000"));
        Assert.Null(ServerEntry.ParseAddress("host:notaport"));
        Assert.Null(ServerEntry.ParseAddress("two words"));

        var path = Path.Combine(Directory.CreateTempSubdirectory("ir-servers").FullName, "servers.pkir");
        Assert.Equal([ServerList.LocalTest], ServerList.Load(path));
        ServerList.Save(path, [new ServerEntry("Home", "192.168.1.50", 7451, "AB:CD"), new ServerEntry("v6 it's", "::1", 8000)]);
        Assert.Equal([new ServerEntry("Home", "192.168.1.50", 7451, "AB:CD"), new ServerEntry("v6 it's", "::1", 8000)], ServerList.Load(path));
    }

    [Fact]
    public void Frames_round_trip_and_refuse_oversized_ones()
    {
        var message = new PlayersMoved([new NetMove(3, 10, -4, 2, true)]);
        var stream = new MemoryStream(Frames.Encode(message));
        Assert.Equal(message.Moves[0], ((PlayersMoved)Frames.ReadAsync(stream).Result!).Moves[0]);
        Assert.Null(Frames.ReadAsync(stream).Result);

        var huge = new MemoryStream(BitConverter.GetBytes(Protocol.MaxFrame + 1));
        Assert.Throws<AggregateException>(() => Frames.ReadAsync(huge).Result);
    }
}
