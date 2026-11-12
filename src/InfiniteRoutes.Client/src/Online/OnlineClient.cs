using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using InfiniteRoutes.Core.Config;
using InfiniteRoutes.Core.Net;

namespace InfiniteRoutes.Client.Online;

public sealed record ServerInfo(string Name, int Version, int GamePort, int Online);
public sealed record CharacterSummary(long Id, string Name, int Appearance, int Zone, int X, int Y);

/// <summary>
/// Talks to one server's accounts API over HTTPS. The server's certificate is pinned: on the first connection the
/// player is shown its fingerprint (<see cref="PresentedFingerprint"/>) and trusts it, after which any other
/// certificate is refused — the self-signed-friendly way of BUILD_SPEC §4.5.
/// </summary>
public sealed class OnlineClient : IDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly HttpClient _http;

    public ServerEntry Server { get; private set; }
    /// <summary>The fingerprint the server showed on the last request (to ask the player about, or to compare).</summary>
    public string? PresentedFingerprint { get; private set; }
    public string? Token { get; private set; }
    public string? Account { get; private set; }
    public ServerInfo? Info { get; private set; }

    public OnlineClient(ServerEntry server)
    {
        Server = server;
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, cert, _, _) => Accept(cert) },
            ConnectTimeout = TimeSpan.FromSeconds(8),
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri($"https://{(server.Host.Contains(':') ? $"[{server.Host}]" : server.Host)}:{server.ApiPort}/"), Timeout = TimeSpan.FromSeconds(15) };
    }

    public static string Fingerprint(X509Certificate cert) =>
        string.Join(":", SHA256.HashData(cert.GetRawCertData()).Select(b => b.ToString("X2")));

    /// <summary>Accepts the certificate the player trusted; before they've trusted one, accepts it only to show it to them.</summary>
    bool Accept(X509Certificate? cert)
    {
        if (cert is null)
            return false;
        PresentedFingerprint = Fingerprint(cert);
        return Server.Fingerprint is null || Server.Fingerprint == PresentedFingerprint;
    }

    /// <summary>Uses <paramref name="fingerprint"/> from now on (the player trusted it).</summary>
    public void Trust(string fingerprint) => Server = Server with { Fingerprint = fingerprint };

    /// <summary>
    /// Reaches the server. Returns null when it answered; otherwise why not: unreachable, or a certificate other than
    /// the trusted one.
    /// </summary>
    public async Task<string?> Probe()
    {
        try
        {
            Info = await _http.GetFromJsonAsync<ServerInfo>("api/info", Json);
            if (Info is null)
                return "The server didn't answer.";
            return Info.Version == Protocol.Version ? null
                : $"The server speaks protocol {Info.Version}; this game speaks {Protocol.Version}.";
        }
        catch (HttpRequestException e) when (Server.Fingerprint is not null && PresentedFingerprint is not null && PresentedFingerprint != Server.Fingerprint)
        {
            _ = e;
            return "This server's certificate has CHANGED since you trusted it. It may not be the same server. Not connecting.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return $"Can't reach {Server.Address}: {e.Message}";
        }
    }

    async Task<(JsonElement Body, bool Ok)> Send(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        if (Token is not null)
            request.Headers.Authorization = new("Bearer", Token);
        var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return (json, response.IsSuccessStatusCode);
    }

    static string Error(JsonElement body, string fallback) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var e) ? e.GetString() ?? fallback : fallback;

    /// <summary>Null when it worked, else the server's reason.</summary>
    public async Task<string?> Register(string name, string password)
    {
        try
        {
            var (body, ok) = await Send(HttpMethod.Post, "api/register", new { name, password });
            return ok ? null : Error(body, "Registration failed.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return e.Message;
        }
    }

    /// <summary>When the session from <see cref="Login"/> ends.</summary>
    public DateTime Expires { get; private set; }

    /// <param name="remember">Ask for a long "remember me" session.</param>
    public async Task<string?> Login(string name, string password, bool remember = false)
    {
        try
        {
            var (body, ok) = await Send(HttpMethod.Post, "api/login", new { name, password, remember });
            if (!ok)
                return Error(body, "Login failed.");
            Token = body.GetProperty("token").GetString();
            Account = body.GetProperty("account").GetString();
            Expires = body.TryGetProperty("expires", out var e) && e.TryGetDateTime(out var at) ? at.ToUniversalTime() : DateTime.UtcNow.AddHours(1);
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return e.Message;
        }
    }

    public async Task<(List<CharacterSummary>? List, string? Error)> Characters()
    {
        try
        {
            var (body, ok) = await Send(HttpMethod.Get, "api/characters");
            return ok ? (body.Deserialize<List<CharacterSummary>>(Json), null) : (null, Error(body, "Couldn't load your characters."));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, e.Message);
        }
    }

    public async Task<(CharacterSummary? Character, string? Error)> CreateCharacter(string name, int appearance)
    {
        try
        {
            var (body, ok) = await Send(HttpMethod.Post, "api/characters", new { name, appearance });
            return ok ? (body.Deserialize<CharacterSummary>(Json), null) : (null, Error(body, "Couldn't create the character."));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, e.Message);
        }
    }

    /// <summary>The account's staff role and two-factor state.</summary>
    /// <summary>
    /// Picks up a remembered session: null when the server still takes the token, else why not (expired or ended:
    /// the token is dropped).
    /// </summary>
    public async Task<string?> Resume(string token)
    {
        Token = token;
        var (info, error) = await AccountInfo();
        if (info is null)
        {
            Token = null;
            return error;
        }
        Account = info.Name;
        return null;
    }

    /// <summary>Ends the session on the server (best effort) and forgets the token.</summary>
    public async Task Logout()
    {
        try
        {
            if (Token is not null)
                await Send(HttpMethod.Post, "api/logout");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
        }
        Token = null;
    }

    public async Task<(AccountInfo? Info, string? Error)> AccountInfo()
    {
        try
        {
            var (body, ok) = await Send(HttpMethod.Get, "api/account");
            return ok ? (body.Deserialize<AccountInfo>(Json), null) : (null, Error(body, "Couldn't load the account."));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, e.Message);
        }
    }

    /// <summary>Starts two-factor setup: the secret to type into an authenticator app, and its otpauth:// link.</summary>
    public async Task<(string? Secret, string? Uri, string? Error)> SetupTwoFactor()
    {
        try
        {
            var (body, ok) = await Send(HttpMethod.Post, "api/2fa/setup", new { });
            return ok ? (body.GetProperty("secret").GetString(), body.GetProperty("uri").GetString(), null) : (null, null, Error(body, "Couldn't start two-factor setup."));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return (null, null, e.Message);
        }
    }

    /// <summary>Turns two-factor on with the first code from the app; null when it worked.</summary>
    public async Task<string?> ConfirmTwoFactor(string code)
    {
        try
        {
            var (body, ok) = await Send(HttpMethod.Post, "api/2fa/confirm", new { code });
            return ok ? null : Error(body, "That code didn't work.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return e.Message;
        }
    }

    public void Dispose() => _http.Dispose();
}

public sealed record AccountInfo(string Name, string Role, bool TwoFactor, bool Require2fa);

/// <summary>
/// The game connection: TLS to the game port (same pinned certificate), <see cref="Hello"/>, then messages both ways.
/// Incoming messages queue in <see cref="Inbox"/> for the game to handle on its own thread.
/// </summary>
public sealed class GameConnection : IDisposable
{
    readonly TcpClient _tcp;
    readonly SslStream _tls;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly CancellationTokenSource _cts = new();

    public ConcurrentQueue<Message> Inbox { get; } = new();
    public Welcome Welcome { get; }
    public bool Closed { get; private set; }
    /// <summary>Why the server closed the connection, when it said.</summary>
    public string? CloseReason { get; private set; }

    GameConnection(TcpClient tcp, SslStream tls, Welcome welcome)
    {
        _tcp = tcp;
        _tls = tls;
        Welcome = welcome;
        _ = Task.Run(Read);
    }

    /// <summary>Connects and joins as <paramref name="character"/>; returns the connection or why it failed.</summary>
    public static async Task<(GameConnection? Connection, string? Error)> Join(OnlineClient client, long character)
    {
        if (client.Info is not { } info || client.Token is not { } token)
            return (null, "Log in first.");
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(client.Server.Host, info.GamePort, timeout.Token);
            var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
                cert is not null && OnlineClient.Fingerprint(cert) == client.Server.Fingerprint);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = client.Server.Host }, timeout.Token);
            await Frames.WriteAsync(tls, new Hello(Protocol.Version, token, character), timeout.Token);
            switch (await Frames.ReadAsync(tls, timeout.Token))
            {
                case Welcome welcome:
                    return (new GameConnection(tcp, tls, welcome), null);
                case Refused refused:
                    tcp.Dispose();
                    return (null, refused.Reason);
                default:
                    tcp.Dispose();
                    return (null, "The server closed the connection.");
            }
        }
        catch (Exception e) when (e is SocketException or IOException or InvalidDataException or OperationCanceledException
            or System.Security.Authentication.AuthenticationException)
        {
            tcp.Dispose();
            return (null, $"Couldn't join the game: {e.Message}");
        }
    }

    async Task Read()
    {
        try
        {
            while (!_cts.IsCancellationRequested && await Frames.ReadAsync(_tls, _cts.Token) is { } message)
            {
                if (message is Notice { Channel: 0 } notice)
                    CloseReason = notice.Text;
                Inbox.Enqueue(message);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
        }
        Closed = true;
    }

    /// <summary>Sends a message (in the background; a failure closes the connection).</summary>
    public void Send(Message message)
    {
        if (Closed)
            return;
        _ = Task.Run(async () =>
        {
            await _writeLock.WaitAsync();
            try
            {
                await Frames.WriteAsync(_tls, message, _cts.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                Closed = true;
            }
            finally
            {
                _writeLock.Release();
            }
        });
    }

    public void Dispose()
    {
        Closed = true;
        _cts.Cancel();
        _tls.Dispose();
        _tcp.Dispose();
    }
}
