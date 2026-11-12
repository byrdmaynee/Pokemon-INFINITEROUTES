using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Server.Accounts;
using InfiniteRoutes.Server.Config;
using InfiniteRoutes.Server.Data;
using InfiniteRoutes.Server.World;
using Microsoft.EntityFrameworkCore;

namespace InfiniteRoutes.Server.Net;

/// <summary>
/// The game port (BUILD_SPEC §4.1): TCP with TLS, then frames (<see cref="Frames"/>). A connection must send
/// <see cref="Hello"/> within <see cref="HelloSeconds"/> with a live session token and one of its account's
/// characters; then its messages go to the world until it closes. Each connection has a reader and a writer task and a
/// bounded outgoing queue; a client that floods messages or can't keep up is disconnected.
/// </summary>
public sealed class GameListener(ServerConfig config, X509Certificate2 certificate, AccountService accounts,
    IDbContextFactory<IrDb> dbs, WorldService world, Admin.AdminService admin, Social.SocialService social, ILogger<GameListener> log,
    Game.GameRules? rules = null) : BackgroundService
{
    const int HelloSeconds = 10;
    const int MaxMessagesPerSecond = 60;
    const int OutgoingQueue = 512;

    TcpListener? _listener;
    readonly TaskCompletionSource<int> _port = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The port actually listened on (when the config asks for port 0, tests read it here).</summary>
    public Task<int> Port => _port.Task;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        _listener = new TcpListener(IPAddress.Parse(config.BindAddress), config.GamePort);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _port.TrySetResult(port);
        log.LogInformation("Game port {Port} open (TLS fingerprint {Fingerprint})", port, Certificates.Fingerprint(certificate));
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stop);
                _ = Task.Run(() => Serve(client, stop), stop);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _listener.Stop();
        }
    }

    async Task Serve(TcpClient client, CancellationToken stop)
    {
        client.NoDelay = true;
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
        using var _ = client;
        await using var tls = new SslStream(client.GetStream(), false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        Connection? connection = null;
        WorldPlayer? player = null;
        try
        {
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(HelloSeconds));
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, handshake.Token);
                if (await Frames.ReadAsync(tls, handshake.Token) is not Hello hello)
                    return;
                var (account, refusal) = await Admit(hello);
                if (refusal is not null)
                {
                    await Frames.WriteAsync(tls, new Refused(refusal), cts.Token);
                    return;
                }
                var character = (await LoadCharacter(hello))!;
                connection = new Connection(tls, cts);
                var (joined, reason) = world.Join(character, account!, rules?.Load(character.StateJson), connection);
                if (joined is null)
                {
                    await Frames.WriteAsync(tls, new Refused(reason!), cts.Token);
                    return;
                }
                player = joined;
                connection.Send(new Welcome(player.Id, player.Name, player.Channel, config.Channels, player.Location, config.Name));
                world.SendState(player);
                if (player.Beacon.Length > 0)
                    connection.Send(new BeaconChanged(player.Id, player.Beacon));
                if (world.RoleOf(player).IsStaff)
                    connection.Send(world.StatusOf(player));
                log.LogInformation("{Name} joined channel {Channel} from {Endpoint}", player.Name, player.Channel, endpoint);
            }
            await social.Joined(player);

            var writer = connection.WriteLoop();
            var window = DateTime.UtcNow;
            var count = 0;
            while (!cts.IsCancellationRequested)
            {
                var message = await Frames.ReadAsync(tls, cts.Token);
                if (message is null)
                    break;
                if ((DateTime.UtcNow - window).TotalSeconds >= 1)
                    (window, count) = (DateTime.UtcNow, 0);
                if (++count > MaxMessagesPerSecond)
                {
                    connection.Close("Too many messages.");
                    break;
                }
                if (message is Elevate or AdminCommand or AdminAction)
                    await admin.Handle(player, message);
                else if (message is SocialAction action)
                    await social.Handle(player, action);
                else
                    world.Handle(player, message);
            }
            await cts.CancelAsync();
            await writer;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
        {
            log.LogDebug("Connection {Endpoint} ended: {Message}", endpoint, e.Message);
        }
        finally
        {
            if (player is not null)
            {
                world.Leave(player);
                log.LogInformation("{Name} left", player.Name);
                try
                {
                    await social.Left(player);
                }
                catch (Exception e) when (e is Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException or ObjectDisposedException)
                {
                    log.LogDebug("Telling {Name}'s friends they left failed: {Message}", player.Name, e.Message);
                }
            }
        }
    }

    async Task<(Account? Account, string? Refusal)> Admit(Hello hello)
    {
        if (hello.Version != Protocol.Version)
            return (null, $"This server speaks protocol {Protocol.Version}; your game speaks {hello.Version}. Update the game.");
        if (await accounts.FromToken(hello.Token) is not { } account)
            return (null, "Your login has expired. Log in again.");
        if (AccountService.BanMessage(account) is { } banned)
            return (null, banned);
        var character = await LoadCharacter(hello);
        return character is null || character.AccountId != account.Id ? (null, "That character isn't on your account.") : (account, null);
    }

    async Task<Character?> LoadCharacter(Hello hello)
    {
        await using var db = await dbs.CreateDbContextAsync();
        return await db.Characters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == hello.Character);
    }

    /// <summary>One client: a queue of outgoing messages written by its own task, so the world never waits on a socket.</summary>
    sealed class Connection(SslStream stream, CancellationTokenSource cts) : IClientLink
    {
        readonly Channel<Message> _out = System.Threading.Channels.Channel.CreateBounded<Message>(new BoundedChannelOptions(OutgoingQueue)
        {
            FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true,
        });
        string? _closing;

        public void Send(Message message)
        {
            if (!_out.Writer.TryWrite(message))
                Close("Your connection can't keep up.");
        }

        public void Close(string reason)
        {
            if (_closing is not null)
                return;
            _closing = reason;
            _out.Writer.TryComplete();
        }

        public async Task WriteLoop()
        {
            try
            {
                await foreach (var message in _out.Reader.ReadAllAsync(cts.Token))
                    await stream.WriteAsync(Frames.Encode(message), cts.Token);
                if (_closing is { } reason)
                    await Frames.WriteAsync(stream, new Notice(reason), CancellationToken.None);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                await cts.CancelAsync();
            }
        }
    }
}

/// <summary>Runs the world's 10 Hz update and writes positions on shutdown.</summary>
public sealed class WorldTicker(WorldService world, ILogger<WorldTicker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                try
                {
                    await world.Tick();
                }
                catch (Exception e)
                {
                    log.LogError(e, "World update failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        world.Broadcast("The server is shutting down.");
        await base.StopAsync(cancellationToken);
        await world.SaveAll();
    }
}
