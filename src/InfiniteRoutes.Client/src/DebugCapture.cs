using Godot;
using InfiniteRoutes.Client.Ui;

namespace InfiniteRoutes.Client;

/// <summary>
/// Scripted smoke test: <c>godot --path . -- --capture=&lt;dir&gt;</c> walks through the main screens,
/// saves a PNG of each into &lt;dir&gt;, then quits. Used to check UI without clicking through it.
/// </summary>
public static class DebugCapture
{
    public static void StartIfRequested(Main main)
    {
        var arg = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--capture=", StringComparison.Ordinal));
        if (arg is null)
            return;
        Run(main, arg["--capture=".Length..]);
    }

    static async void Run(Main main, string dir)
    {
        Directory.CreateDirectory(dir);
        var tree = main.GetTree();

        async Task Wait(double seconds) => await main.ToSignal(tree.CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        void Save(string name)
        {
            var path = System.IO.Path.Combine(dir, name + ".png");
            main.GetViewport().GetTexture().GetImage().SavePng(path);
            GD.Print($"[capture] {path}");
        }

        await Wait(0.3);
        Save("01-loading");
        for (var waited = 0.0; main.CurrentScreen is LoadingScreen && waited < 60; waited += 0.25)
            await Wait(0.25);
        await Wait(0.3);
        Save("02-after-loading");

        if (ClientServices.Library.WizardCompleted)
        {
            main.Toast("Toast test: this feature arrives later.");
            await Wait(0.3);
            Save("03-main-menu");
            foreach (var (name, screen) in new (string, Func<Control>)[]
                     {
                         ("04-login", () => new LoginScreen()),
                         ("05-settings", () => new SettingsScreen()),
                         ("06-servers", () => new ServersScreen()),
                         ("07-credits", () => new CreditsScreen()),
                         ("08-pokedex", () => new PokedexScreen()),
                     })
            {
                main.Push(screen);
                await Wait(0.5);
                Save(name);
                main.Back();
            }

            // Walk north from Nuvema Town towards Route 1 and capture along the way.
            main.Push(() => new World.OverworldScreen());
            await Wait(1.0);
            Save("09-overworld");
            if (main.CurrentScreen is World.OverworldScreen world)
            {
                var start = world.PlayerTile;
                var path = FindPath(world, start, t => world.LocationAt(t) == "Route 1" && t.Y < start.Y - 40);
                GD.Print($"[capture] path to Route 1: {path.Count} steps");
                var steps = 0;
                foreach (var step in path)
                {
                    if (world.TryStep(step, run: true))
                        steps++;
                    await Wait(0.16);
                    if (steps == path.Count / 2)
                        Save("10-overworld-halfway");
                }
                await Wait(0.5);
                Save("11-overworld-route1");
                GD.Print($"[capture] walked {steps} steps from {start} to {world.PlayerTile} ({world.LocationAt(world.PlayerTile)})");
            }
            main.Back();
        }

        GD.Print("[capture] done");
        tree.Quit();
    }

    /// <summary>Breadth-first search over walkable tiles; returns the step directions.</summary>
    static List<Vector2I> FindPath(World.OverworldScreen world, Vector2I start, Func<Vector2I, bool> isGoal)
    {
        var dirs = new[] { Vector2I.Up, Vector2I.Left, Vector2I.Right, Vector2I.Down };
        var cameFrom = new Dictionary<Vector2I, Vector2I> { [start] = start };
        var queue = new Queue<Vector2I>([start]);
        while (queue.Count > 0 && cameFrom.Count < 20000)
        {
            var t = queue.Dequeue();
            if (isGoal(t))
            {
                var path = new List<Vector2I>();
                for (var c = t; c != start; c = cameFrom[c])
                    path.Add(c - cameFrom[c]);
                path.Reverse();
                return path;
            }
            foreach (var d in dirs)
                if (!cameFrom.ContainsKey(t + d) && world.IsWalkable(t + d))
                {
                    cameFrom[t + d] = t;
                    queue.Enqueue(t + d);
                }
        }
        return [];
    }
}
