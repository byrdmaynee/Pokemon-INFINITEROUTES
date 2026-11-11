using Godot;
using InfiniteRoutes.Core;
using InfiniteRoutes.Core.Config;

namespace InfiniteRoutes.Client.Online;

/// <summary>The player's online state: their server list, the server they're logged in to, and the game connection.</summary>
public static class OnlineSession
{
    public static List<ServerEntry> Servers { get; private set; } = [];
    public static OnlineClient? Client { get; private set; }
    public static GameConnection? Game { get; private set; }
    /// <summary>The character being played (its look picks the sprite).</summary>
    public static CharacterSummary? Character { get; private set; }

    public static void LoadServers()
    {
        try
        {
            Servers = ServerList.Load(AppPaths.ServersFile);
        }
        catch (PkirException e)
        {
            GD.PushWarning($"servers.pkir: {e.Message}");
            Servers = [ServerList.LocalTest];
        }
        catch (IOException e)
        {
            // Another game window is writing it right now: use what's known (or the default) this time.
            GD.PushWarning($"servers.pkir is busy: {e.Message}");
            if (Servers.Count == 0)
                Servers = [ServerList.LocalTest];
        }
    }

    public static void SaveServers()
    {
        for (var attempt = 0; ; attempt++)
            try
            {
                ServerList.Save(AppPaths.ServersFile, Servers);
                return;
            }
            catch (IOException e) when (attempt < 5)
            {
                // Another game window replacing it at the same moment: try again shortly.
                GD.PushWarning($"servers.pkir is busy, retrying: {e.Message}");
                Thread.Sleep(50);
            }
    }

    /// <summary>Replaces <paramref name="old"/> in the list (a newly trusted fingerprint) and saves.</summary>
    public static void Update(ServerEntry old, ServerEntry updated)
    {
        var i = Servers.IndexOf(old);
        if (i >= 0)
            Servers[i] = updated;
        else
            Servers.Add(updated);
        SaveServers();
    }

    public static void LoggedIn(OnlineClient client)
    {
        Leave();
        Client?.Dispose();
        Client = client;
    }

    public static void Joined(GameConnection game, CharacterSummary character)
    {
        Leave();
        Game = game;
        Character = character;
    }

    /// <summary>Closes the game connection (back to the character list).</summary>
    public static void Leave()
    {
        Game?.Dispose();
        Game = null;
    }
}
