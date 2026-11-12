using InfiniteRoutes.Core.Config;
using InfiniteRoutes.Server.Accounts;
using Microsoft.Extensions.DependencyInjection;

namespace InfiniteRoutes.Tests;

public class RememberLoginTests
{
    static string TempFile() => Path.Combine(Path.GetTempPath(), $"ir-logins-{Guid.NewGuid():N}.json");

    static readonly ServerEntry Home = new("Home", "127.0.0.1", 7451, "AA:BB");

    [Fact]
    public void A_remembered_login_is_kept_per_server_and_only_while_it_lives()
    {
        var path = TempFile();
        try
        {
            var logins = new RememberedLogins(path);
            var expires = DateTime.UtcNow.AddDays(30);
            logins.Remember(Home, "Hilda", "token-1", expires);
            logins.Remember(Home with { Host = "10.0.0.2" }, "Cheren", "token-2", expires);

            // A fresh reader (the next game start) finds it.
            var found = new RememberedLogins(path).Find(Home);
            Assert.NotNull(found);
            Assert.Equal(("Hilda", "token-1"), (found.Account, found.Token));
            Assert.Equal("Cheren", new RememberedLogins(path).Find(Home with { Host = "10.0.0.2" })!.Account);

            // Not for a server whose certificate changed, nor once it has expired.
            Assert.Null(logins.Find(Home with { Fingerprint = "CC:DD" }));
            Assert.Null(logins.Find(Home, now: expires.AddMinutes(1)));

            // Remembering again replaces it; forgetting removes only that server's.
            logins.Remember(Home, "Hilda", "token-3", expires);
            Assert.Equal("token-3", logins.Find(Home)!.Token);
            logins.Forget(Home);
            Assert.Null(logins.Find(Home));
            Assert.NotNull(logins.Find(Home with { Host = "10.0.0.2" }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_token_is_not_stored_as_written_on_Windows_and_an_unreadable_file_is_ignored()
    {
        var path = TempFile();
        try
        {
            new RememberedLogins(path).Remember(Home, "Hilda", "secret-token-value", DateTime.UtcNow.AddDays(1));
            var text = File.ReadAllText(path);
            if (OperatingSystem.IsWindows())
                Assert.DoesNotContain("secret-token-value", text);
            Assert.Equal("secret-token-value", new RememberedLogins(path).Find(Home)!.Token);

            // A token another user sealed (unprotect fails) is skipped; junk is treated as nothing remembered.
            var other = new RememberedLogins(path, new RememberedLogins.Protector(t => "x:" + t, _ => null));
            Assert.Null(other.Find(Home));
            File.WriteAllText(path, "{ not json");
            Assert.Null(new RememberedLogins(path).Find(Home));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_remember_me_login_gets_a_session_of_days_not_hours()
    {
        await using var server = await TestServer.Start(channels: 1);
        var accounts = server.App.Services.GetRequiredService<AccountService>();
        Assert.Null(await accounts.Register("Remy", "password123"));

        var (usual, _) = await accounts.LoginOrWhy("Remy", "password123");
        var (kept, _) = await accounts.LoginOrWhy("Remy", "password123", remember: true);

        Assert.InRange(usual!.Expires, DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddDays(2));
        Assert.True(kept!.Expires > DateTime.UtcNow.AddDays(20), $"{kept.Expires}");
        Assert.Equal("Remy", (await accounts.FromToken(kept.Token))?.Name);
    }
}
