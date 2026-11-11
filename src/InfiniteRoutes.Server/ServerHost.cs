using System.Net;
using System.Threading.RateLimiting;
using InfiniteRoutes.Server.Accounts;
using InfiniteRoutes.Server.Config;
using InfiniteRoutes.Server.Data;
using InfiniteRoutes.Server.Net;
using InfiniteRoutes.Server.World;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace InfiniteRoutes.Server;

public sealed record Credentials(string Name, string Password);
public sealed record NewCharacter(string Name, int Appearance);
public sealed record CharacterView(long Id, string Name, int Appearance, int Zone, int X, int Y);
public sealed record TotpCode(string Code);

/// <summary>
/// Builds the server: the HTTPS accounts API on <c>api_port</c> and the TLS game port on <c>game_port</c>, sharing one
/// certificate, the database and the world.
/// </summary>
/// <remarks>
/// API (JSON): <c>GET /api/info</c>; <c>POST /api/register</c> and <c>/api/login</c> with {name, password} (login
/// returns {token, expires, account}); with <c>Authorization: Bearer</c> token: <c>POST /api/logout</c>,
/// <c>GET /api/characters</c>, <c>POST /api/characters</c> with {name, appearance}. Logins and registrations are
/// limited to 10 a minute per address.
/// </remarks>
public static class ServerHost
{
    public static WebApplication Build(ServerConfig config, IWorldData world, Action<ILoggingBuilder>? logging = null, Game.GameRules? rules = null)
    {
        var certificate = Certificates.Load(config);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        if (logging is not null)
            logging(builder.Logging);
        builder.WebHost.ConfigureKestrel(k =>
            k.Listen(IPAddress.Parse(config.BindAddress), config.ApiPort, listen => listen.UseHttps(certificate)));
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(certificate);
        builder.Services.AddSingleton(world);
        builder.Services.AddSingleton(world.Start);
        builder.Services.AddDbContextFactory<IrDb>(o => IrDb.Configure(o, config));
        if (rules is not null)
            builder.Services.AddSingleton(rules);
        builder.Services.AddSingleton(s => new AccountService(s.GetRequiredService<IDbContextFactory<IrDb>>(), config, world.Start, rules));
        builder.Services.AddSingleton<Admin.RoleService>();
        builder.Services.AddSingleton(s => new WorldService(world, config,
            saves => s.GetRequiredService<AccountService>().SavePlayers(saves), rules is null ? null : rules.Json,
            s.GetRequiredService<Admin.RoleService>()));
        builder.Services.AddSingleton<Admin.AdminService>();
        builder.Services.AddSingleton<GameListener>();
        builder.Services.AddHostedService(s => s.GetRequiredService<GameListener>());
        builder.Services.AddHostedService<WorldTicker>();
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "?",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
        });

        var app = builder.Build();
        using (var db = app.Services.GetRequiredService<IDbContextFactory<IrDb>>().CreateDbContext())
        {
            var was = Schema.Ensure(db);
            if (was is > 0 and < Schema.Current)
                app.Logger.LogInformation("Database upgraded from schema {Old} to {New}", was, Schema.Current);
        }
        var roleService = app.Services.GetRequiredService<Admin.RoleService>();
        roleService.Load();
        var worldService = app.Services.GetRequiredService<WorldService>();
        roleService.Changed += worldService.RefreshStaff;
        if (app.Services.GetRequiredService<AccountService>().Bootstrap().GetAwaiter().GetResult() is { } bootstrap)
            app.Logger.LogWarning("{Bootstrap}", bootstrap);
        app.UseRateLimiter();
        MapApi(app, config);
        return app;
    }

    static void MapApi(WebApplication app, ServerConfig config)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/info", (WorldService world, GameListener game) => Results.Ok(new
        {
            name = config.Name, version = Core.Net.Protocol.Version, gamePort = game.Port.IsCompletedSuccessfully ? game.Port.Result : config.GamePort,
            online = world.Online, channels = world.ChannelCounts(),
        }));
        api.MapPost("/register", async (Credentials c, AccountService accounts) =>
            await accounts.Register(c.Name, c.Password) is { } error ? Results.BadRequest(new { error }) : Results.Ok(new { ok = true }))
            .RequireRateLimiting("auth");
        api.MapPost("/login", async (Credentials c, AccountService accounts) =>
            await accounts.LoginOrWhy(c.Name, c.Password) is var (login, why) && login is not null
                ? Results.Ok(new { token = login.Token, expires = login.Expires, account = login.Account })
                : Results.Json(new { error = why ?? "Wrong account name or password." }, statusCode: why is null ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden))
            .RequireRateLimiting("auth");
        api.MapPost("/logout", async (HttpRequest request, AccountService accounts) =>
        {
            if (Bearer(request) is { } token)
                await accounts.Logout(token);
            return Results.Ok(new { ok = true });
        });
        api.MapGet("/account", async (HttpRequest request, AccountService accounts) =>
            await accounts.FromToken(Bearer(request)) is not { } account
                ? Unauthorized()
                : Results.Ok(new { name = account.Name, role = account.Role, twoFactor = account.TotpEnabled, require2fa = config.Require2fa }));
        api.MapPost("/2fa/setup", async (HttpRequest request, AccountService accounts) =>
        {
            if (await accounts.FromToken(Bearer(request)) is not { } account)
                return Unauthorized();
            if (account.TotpEnabled)
                return Results.BadRequest(new { error = "Two-factor is already on for this account." });
            var setup = await accounts.BeginTotp(account.Id);
            return Results.Ok(new { secret = setup!.Value.Secret, uri = setup.Value.Uri });
        }).RequireRateLimiting("auth");
        api.MapPost("/2fa/confirm", async (TotpCode body, HttpRequest request, AccountService accounts) =>
        {
            if (await accounts.FromToken(Bearer(request)) is not { } account)
                return Unauthorized();
            if (!await accounts.ConfirmTotp(account.Id, body.Code))
                return Results.BadRequest(new { error = "That code doesn't match. Check the app's clock and try the newest code." });
            await accounts.Audit(account.Id, account.Name, account.Role, "2fa-on", account.Name, "");
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("auth");
        api.MapGet("/characters", async (HttpRequest request, AccountService accounts) =>
            await accounts.FromToken(Bearer(request)) is not { } account
                ? Unauthorized()
                : Results.Ok((await accounts.Characters(account.Id)).Select(View)));
        api.MapPost("/characters", async (NewCharacter n, HttpRequest request, AccountService accounts) =>
        {
            if (await accounts.FromToken(Bearer(request)) is not { } account)
                return Unauthorized();
            var (character, error) = await accounts.CreateCharacter(account.Id, n.Name, n.Appearance);
            return character is null ? Results.BadRequest(new { error }) : Results.Ok(View(character));
        });
    }

    static CharacterView View(Character c) => new(c.Id, c.Name, c.Appearance, c.Zone, c.X, c.Y);

    static IResult Unauthorized() => Results.Json(new { error = "Log in first." }, statusCode: StatusCodes.Status401Unauthorized);

    static string? Bearer(HttpRequest request) =>
        request.Headers.Authorization.ToString() is { } h && h.StartsWith("Bearer ", StringComparison.Ordinal) ? h[7..].Trim() : null;
}
