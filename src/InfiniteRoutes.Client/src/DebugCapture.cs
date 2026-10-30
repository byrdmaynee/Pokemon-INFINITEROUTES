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

            main.Push(() => new World.OverworldScreen());
            await Wait(1.0);
            Save("09-overworld");
            if (main.CurrentScreen is World.OverworldScreen world)
            {
                world.Zoom = 2.6f;
                world.ShowCollision(true);
                await Wait(0.6);
                Save("09b-overworld-collision");
                world.ShowCollision(false);
                world.Zoom = 1;
                async Task Walk(List<Vector2I> path, bool run = true)
                {
                    foreach (var step in path)
                    {
                        world.TryStep(step, run);
                        await Wait(0.05);
                        while (world.Busy)
                            await Wait(0.05);
                    }
                }
                async Task Settle()
                {
                    await Wait(0.3);
                    while (world.Busy)
                        await Wait(0.05);
                    await Wait(0.3);
                }
                // Into the player's house: walk up to the first door warp from below and step in.
                var start = world.PlayerTile;
                var door = world.Warps.Where(w => w.Value.Kind == Extractor.Extraction.WarpKind.Door).OrderBy(w => (w.Key - start).LengthSquared()).First();
                await Walk(FindPath(world, start, t => t == door.Key + Vector2I.Down));
                world.TryStep(Vector2I.Up);
                await Settle();
                Save("10-house");
                GD.Print($"[capture] door {door.Key} → zone {world.CurrentZone} at {world.PlayerTile}");

                // Upstairs and back down.
                var stairs = world.Warps.FirstOrDefault(w => w.Value.Kind == Extractor.Extraction.WarpKind.Stairs);
                if (stairs.Value is not null)
                {
                    var next = new[] { Vector2I.Up, Vector2I.Down, Vector2I.Left, Vector2I.Right };
                    var path = FindPath(world, world.PlayerTile, t => next.Any(d => t + d == stairs.Key));
                    await Walk(path, run: false);
                    var into = next.First(d => world.PlayerTile + d == stairs.Key);
                    world.TryStep(into);
                    await Settle();
                    Save("11-house-upstairs");
                    GD.Print($"[capture] stairs → zone {world.CurrentZone} at {world.PlayerTile}");
                    var back = world.Warps.First(w => w.Value.Kind == Extractor.Extraction.WarpKind.Stairs);
                    path = FindPath(world, world.PlayerTile, t => next.Any(d => t + d == back.Key));
                    await Walk(path, run: false);
                    world.TryStep(next.First(d => world.PlayerTile + d == back.Key));
                    await Settle();
                }

                // Out through the exit mat.
                var mat = world.Warps.First(w => w.Value.Kind == Extractor.Extraction.WarpKind.Mat);
                await Walk(FindPath(world, world.PlayerTile, t => t == mat.Key, allowWarps: true));
                world.TryStep(Vector2I.Down);
                await Settle();
                Save("12-back-outside");
                GD.Print($"[capture] mat → zone {world.CurrentZone} at {world.PlayerTile}");

                // Then north to Route 1.
                start = world.PlayerTile;
                var route = FindPath(world, start, t => world.LocationAt(t) == "Route 1" && t.Y < start.Y - 40);
                GD.Print($"[capture] path to Route 1: {route.Count} steps");
                await Walk(route[..(route.Count / 2)]);
                Save("13-overworld-halfway");
                world.Zoom = 1.8f;
                world.ShowCollision(true);
                await Wait(0.6);
                Save("13b-halfway-collision");
                world.ShowCollision(false);
                world.Zoom = 1;
                await Wait(0.3);
                await Walk(route[(route.Count / 2)..]);
                await Wait(0.5);
                Save("14-overworld-route1");
                GD.Print($"[capture] walked from {start} to {world.PlayerTile} ({world.LocationAt(world.PlayerTile)})");
            }
            main.Back();
        }

        GD.Print("[capture] done");
        tree.Quit();
    }

    /// <summary>Breadth-first search over walkable tiles; returns the step directions.</summary>
    /// <param name="allowWarps">Whether the path may cross warp tiles (otherwise only end on one).</param>
    static List<Vector2I> FindPath(World.OverworldScreen world, Vector2I start, Func<Vector2I, bool> isGoal, bool allowWarps = false)
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
                if (!cameFrom.ContainsKey(t + d) && world.IsWalkable(t + d) && (allowWarps || isGoal(t + d) || !world.IsWarp(t + d)))
                {
                    cameFrom[t + d] = t;
                    queue.Enqueue(t + d);
                }
        }
        return [];
    }
}
