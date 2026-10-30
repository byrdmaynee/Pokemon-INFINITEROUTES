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
        await Wait(2.0);
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
                     })
            {
                main.Push(screen);
                await Wait(0.5);
                Save(name);
                main.Back();
            }
        }

        GD.Print("[capture] done");
        tree.Quit();
    }
}
