using Godot;
using InfiniteRoutes.Client.Ui;

namespace InfiniteRoutes.Client;

/// <summary>
/// Scripted smoke test: <c>godot --path . -- --capture=&lt;dir&gt;</c> walks through the main screens,
/// saves a PNG of each into &lt;dir&gt;, then quits. Used to check UI without clicking through it.
/// The offline game saves into &lt;dir&gt;/save (never the user's own save) and starts fresh;
/// <c>--capture-continue=&lt;dir&gt;</c> then reopens the world from that save to check it loads.
/// </summary>
public static class DebugCapture
{
    public static void StartIfRequested(Main main)
    {
        var args = OS.GetCmdlineUserArgs();
        if (args.FirstOrDefault(a => a.StartsWith("--capture-continue=", StringComparison.Ordinal)) is { } cont)
        {
            var dir = cont["--capture-continue=".Length..];
            World.OfflineSession.SaveDirOverride = System.IO.Path.Combine(dir, "save");
            Continue(main, dir);
        }
        else if (args.FirstOrDefault(a => a.StartsWith("--capture-remember=", StringComparison.Ordinal)) is { } remember)
        {
            string Arg(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.Ordinal)) is { } v ? v[(name.Length + 3)..] : fallback;
            RememberLogin(main, remember["--capture-remember=".Length..], Arg("account", "Tester"), Arg("phase", "1"));
        }
        else if (args.FirstOrDefault(a => a.StartsWith("--capture-online=", StringComparison.Ordinal)) is { } online)
        {
            string Arg(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.Ordinal)) is { } v ? v[(name.Length + 3)..] : fallback;
            OnlinePlay(main, online["--capture-online=".Length..], Arg("account", "Tester"), Arg("walk", "R4"), double.Parse(Arg("stay", "12"), System.Globalization.CultureInfo.InvariantCulture),
                Arg("say", "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        }
        else if (args.FirstOrDefault(a => a.StartsWith("--capture-doubles=", StringComparison.Ordinal)) is { } doubles)
        {
            var dir = doubles["--capture-doubles=".Length..];
            World.OfflineSession.SaveDirOverride = System.IO.Path.Combine(dir, "save");
            var type = args.FirstOrDefault(a => a.StartsWith("--battle-type=", StringComparison.Ordinal)) is { } t ? int.Parse(t["--battle-type=".Length..]) : 1;
            MultiBattle(main, dir, type);
        }
        else if (args.FirstOrDefault(a => a.StartsWith("--capture-moves=", StringComparison.Ordinal)) is { } moves)
        {
            var dir = moves["--capture-moves=".Length..];
            World.OfflineSession.SaveDirOverride = System.IO.Path.Combine(dir, "save");
            var list = args.FirstOrDefault(a => a.StartsWith("--moves=", StringComparison.Ordinal)) is { } m
                ? m["--moves=".Length..].Split(',').Select(int.Parse).ToArray() : [1, 33, 52, 53, 55, 57, 85, 94];
            MoveAnimations(main, dir, list);
        }
        else if (args.FirstOrDefault(a => a.StartsWith("--capture-battle=", StringComparison.Ordinal)) is { } battle)
        {
            var dir = battle["--capture-battle=".Length..];
            World.OfflineSession.SaveDirOverride = System.IO.Path.Combine(dir, "save");
            Scenery(main, dir);
        }
        else if (args.FirstOrDefault(a => a.StartsWith("--capture=", StringComparison.Ordinal)) is { } arg)
        {
            var dir = arg["--capture=".Length..];
            var save = System.IO.Path.Combine(dir, "save");
            if (Directory.Exists(save))
                Directory.Delete(save, recursive: true);
            World.OfflineSession.SaveDirOverride = save;
            Run(main, dir);
        }
    }

    /// <summary>
    /// "Remember me" against the local test server. Phase 1 logs in with it ticked. Phase 2 (a new game start) expects
    /// "Continue as …", continues without a password, then logs out and checks the login is forgotten.
    /// </summary>
    static async void RememberLogin(Main main, string dir, string account, string phase)
    {
        var tree = main.GetTree();
        async Task Wait(double seconds) => await main.ToSignal(tree.CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        void Shot(string name) => main.GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, name + ".png"));
        System.IO.Directory.CreateDirectory(dir);
        for (var waited = 0.0; main.CurrentScreen is LoadingScreen && waited < 60; waited += 0.25)
            await Wait(0.25);
        await Wait(0.3);
        try
        {
            Online.OnlineSession.LoadServers();
            var port = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--api-port=", StringComparison.Ordinal)) is { } p
                ? int.Parse(p["--api-port=".Length..]) : Core.Config.ServerEntry.DefaultApiPort;
            var server = Online.OnlineSession.Servers.FirstOrDefault(s => s.Host == "127.0.0.1" && s.ApiPort == port) ?? new Core.Config.ServerEntry("Local test server", "127.0.0.1", port);
            if (!Online.OnlineSession.Servers.Contains(server))
                Online.OnlineSession.Update(server, server);
            main.Push(() => new LoginScreen(server));
            await Wait(0.8);
            var login = (LoginScreen)main.CurrentScreen!;
            GD.Print($"[capture] phase {phase}: login screen offers {login.RememberedAccount ?? "nothing"}");
            Shot($"{phase}-login");
            if (phase == "1")
                login.SubmitRemembering(account, "password123");
            else
                login.PressContinue();
            for (var i = 0; i < 40 && main.CurrentScreen is LoginScreen; i++)
            {
                await Wait(0.25);
                if (login.TrustPending is { } trust)
                    trust();
            }
            if (main.CurrentScreen is not CharacterScreen characters)
            {
                GD.Print($"[capture] phase {phase}: still on the login screen: {(main.CurrentScreen as LoginScreen)?.Status}");
                return;
            }
            await Wait(1);
            GD.Print($"[capture] phase {phase}: at the characters as {Online.OnlineSession.Client?.Account}");
            Shot($"{phase}-characters");
            if (phase == "2")
            {
                characters.PressLogOut();
                for (var i = 0; i < 20 && main.CurrentScreen is not LoginScreen; i++)
                    await Wait(0.25);
                await Wait(0.8);
                GD.Print($"[capture] after logging out: login screen offers {(main.CurrentScreen as LoginScreen)?.RememberedAccount ?? "nothing"}");
                Shot("2-logged-out");
            }
        }
        catch (Exception e)
        {
            GD.Print($"[capture] failed: {e}");
        }
        finally
        {
            GD.Print("[capture] done");
            tree.Quit();
        }
    }

    /// <summary>
    /// Plays online against the local test server: registers (or logs in) <paramref name="account"/>, trusts the
    /// certificate, makes or picks a character, joins, walks <paramref name="walk"/> (letters U/D/L/R each followed by a
    /// step count, e.g. "R4D2"), then stays <paramref name="stay"/> seconds, with screenshots and who it saw.
    /// </summary>
    static async void OnlinePlay(Main main, string dir, string account, string walk, double stay, string[] say)
    {
        var tree = main.GetTree();
        async Task Wait(double seconds) => await main.ToSignal(tree.CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        void Shot(string name) => main.GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, name + ".png"));
        System.IO.Directory.CreateDirectory(dir);
        for (var waited = 0.0; main.CurrentScreen is LoadingScreen && waited < 60; waited += 0.25)
            await Wait(0.25);
        await Wait(0.3);
        World.OverworldScreen? world = null;
        try
        {
            Online.OnlineSession.LoadServers();
            var port = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--api-port=", StringComparison.Ordinal)) is { } p
                ? int.Parse(p["--api-port=".Length..]) : Core.Config.ServerEntry.DefaultApiPort;
            var server = Online.OnlineSession.Servers.FirstOrDefault(s => s.Host == "127.0.0.1" && s.ApiPort == port) ?? new Core.Config.ServerEntry("Local test server", "127.0.0.1", port);
            if (!Online.OnlineSession.Servers.Contains(server))
                Online.OnlineSession.Update(server, server);   // the login screen only offers listed servers
            GD.Print($"[capture] server {server.Host}:{server.ApiPort}");
            main.Push(() => new LoginScreen(server));
            await Wait(0.5);
            var login = (LoginScreen)main.CurrentScreen!;
            login.Submit(account, "password123", register: true);
            for (var i = 0; i < 40 && main.CurrentScreen is LoginScreen; i++)
            {
                await Wait(0.25);
                if (login.TrustPending is { } trust)
                {
                    Shot("01-trust");
                    GD.Print("[capture] trusting the server certificate");
                    trust();
                }
            }
            if (main.CurrentScreen is LoginScreen again)
            {
                GD.Print($"[capture] register: {again.Status}");
                again.Submit(account, "password123", register: false);
                for (var i = 0; i < 40 && main.CurrentScreen is LoginScreen; i++)
                    await Wait(0.25);
            }
            if (main.CurrentScreen is not CharacterScreen characters)
            {
                GD.Print($"[capture] login failed: {(main.CurrentScreen as LoginScreen)?.Status}");
                return;
            }
            await Wait(1.5);
            if (characters.Characters.Count == 0)
            {
                characters.GetTree();
                var name = new string(account.Where(char.IsLetterOrDigit).Take(10).ToArray());
                var (made, error) = await Online.OnlineSession.Client!.CreateCharacter(name, account.Length % 2);
                GD.Print($"[capture] created {made?.Name} {error}");
                main.Back();
                main.Push(() => new CharacterScreen());
                await Wait(1.5);
                characters = (CharacterScreen)main.CurrentScreen!;
            }
            Shot("02-characters");
            characters.Play(characters.Characters[0]);
            for (var i = 0; i < 40 && main.CurrentScreen is not World.OverworldScreen; i++)
                await Wait(0.25);
            world = main.CurrentScreen as World.OverworldScreen;
            if (world is null)
            {
                GD.Print($"[capture] join failed: {characters.Status}");
                return;
            }
            await Wait(2);
            GD.Print($"[capture] joined at {world.PlayerTile}; sees {string.Join(", ", world.OtherPlayers)}");
            Shot("03-joined");
            for (var i = 0; i + 1 < walk.Length; i += 2)
            {
                var dirv = walk[i] switch { 'U' => Vector2I.Up, 'D' => Vector2I.Down, 'L' => Vector2I.Left, _ => Vector2I.Right };
                for (var n = 0; n < walk[i + 1] - '0'; n++)
                {
                    for (var t = 0; t < 20 && !world.TryStep(dirv); t++)
                        await Wait(0.05);
                    await Wait(0.32);
                }
            }
            await Wait(1);
            GD.Print($"[capture] walked to {world.PlayerTile}; sees {string.Join(", ", world.OtherPlayers)}");
            Shot("04-walked");
            // Chat lines and commands ("/admin!" enters Admin Mode with the test password and no code).
            foreach (var line in say)
            {
                if (line == "/admin!")
                    Online.OnlineSession.Game?.Send(new Core.Net.Elevate("password123", ""));
                else if (line == "panel")
                {
                    world.ToggleAdminPanel();
                    await Wait(1);
                    GD.Print($"[capture] admin panel open {world.AdminPanelOpen}; rows {string.Join(", ", world.AdminPanelRows.Select(r => r.Name))}");
                }
                else if (line.StartsWith("rclick:", StringComparison.Ordinal))
                {
                    world.OpenPlayerMenu(line["rclick:".Length..], main.GetViewport().GetVisibleRect().Size / 2);
                    await Wait(0.4);
                    GD.Print($"[capture] player menu: {string.Join(" | ", world.PlayerMenuItems)}");
                }
                else if (line.StartsWith("a:", StringComparison.Ordinal))
                {
                    // An admin action: a:verb|target|key=value,key=value|reason.
                    var parts = line[2..].Split('|');
                    var fields = parts.Length > 2 && parts[2].Length > 0
                        ? parts[2].Split(',').Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv.Length > 1 ? kv[1] : "")
                        : new Dictionary<string, string>();
                    Online.OnlineSession.Game?.Send(new Core.Net.AdminAction(parts[0], parts.Length > 1 ? parts[1] : "", fields, parts.Length > 3 ? parts[3] : ""));
                    await Wait(1);
                    GD.Print($"[capture] {line}: sent");
                }
                else if (line.StartsWith("buddy:", StringComparison.Ordinal))
                {
                    Online.OnlineSession.Game?.Send(new Core.Net.SetBuddy(int.Parse(line["buddy:".Length..])));
                    await Wait(1.5);
                    GD.Print($"[capture] {line}: buddy {world.BuddySpecies}");
                }
                else if (line.StartsWith("m:", StringComparison.Ordinal))
                {
                    // The player menu: m:open, m:tab=N, m:market=N, m:press=Text, m:text=I,V, m:spin=I,V, m:dump.
                    var (op, value) = line[2..].IndexOf('=') is var eq and >= 0 ? (line[2..(2 + eq)], line[(3 + eq)..]) : (line[2..], "");
                    if (op == "open" && world.PlayerMenuOpen is null)
                        world.OpenOnlineMenuForTest();
                    if (op == "close" && world.PlayerMenuOpen is not null)
                        world.OpenOnlineMenuForTest();
                    await Wait(0.5);
                    var menu = world.PlayerMenuOpen;
                    var result = op == "close" ? $"closed; buddy {world.BuddySpecies}" : menu is null ? "the player menu is closed" : op switch
                    {
                        "open" => "open",
                        "tab" => Run(() => menu.OpenTab(int.Parse(value))),
                        "market" => Run(() => menu.TestMarket(int.Parse(value))),
                        "press" => menu.TestPress(value),
                        "text" => menu.TestText(int.Parse(value.Split(',')[0]), value[(value.IndexOf(',') + 1)..]),
                        "spin" => menu.TestSpin(int.Parse(value.Split(',')[0]), double.Parse(value.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture)),
                        "dump" => $"tab {menu.CurrentTab}; shop disabled {menu.ShopDisabled}; status \"{menu.StatusText}\"\n    {menu.TestLabels()}",
                        _ => "unknown",
                    };
                    await Wait(1.0);
                    GD.Print($"[capture] {line}: {result}");
                    static string Run(Action a)
                    {
                        a();
                        return "ok";
                    }
                }
                else if (line.StartsWith("w:", StringComparison.Ordinal))
                {
                    // The admin window: w:open=Name, w:tab=N, w:reason=..., w:press=Text, w:spin=I,V, w:text=I,V, w:dump.
                    var (op, value) = line[2..].IndexOf('=') is var eq and >= 0 ? (line[2..(2 + eq)], line[(3 + eq)..]) : (line[2..], "");
                    var window = world.AdminWindowOpen;
                    string result;
                    if (op == "open")
                    {
                        world.OpenPlayerForTest(value);
                        result = "opened";
                    }
                    else if (window is null)
                        result = "the admin window is closed";
                    else
                        result = op switch
                        {
                            "tab" => Run(() => window.TestTab(int.Parse(value))),
                            "reason" => Run(() => window.TestReason(value)),
                            "press" => window.TestPress(value),
                            "spin" => window.TestSpin(int.Parse(value.Split(',')[0]), double.Parse(value.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture)),
                            "text" => window.TestText(int.Parse(value.Split(',')[0]), value[(value.IndexOf(',') + 1)..]),
                            "dump" => $"tab {window.CurrentTab}; status \"{window.StatusText}\"; editor {window.EditorOpen is { } e && GodotObject.IsInstanceValid(e)}\n    {window.TestLabels()}",
                            _ => "unknown",
                        };
                    await Wait(0.9);
                    GD.Print($"[capture] {line}: {result}");
                    static string Run(Action a)
                    {
                        a();
                        return "ok";
                    }
                }
                else if (line.StartsWith("battle:", StringComparison.Ordinal))
                {
                    // Plays the battle on screen to the end: battle:fight (first move) or battle:ball (Poké Balls, then the first move).
                    var ball = line.EndsWith("ball", StringComparison.Ordinal);
                    for (var t = 0; t < 40 && world.BattleOpen is null; t++)
                        await Wait(0.25);
                    var shotTaken = false;
                    for (var t = 0; t < 1200 && world.BattleOpen is { } screen && GodotObject.IsInstanceValid(screen); t++)
                    {
                        await Wait(0.1);
                        if (!GodotObject.IsInstanceValid(screen))
                            break;
                        if (screen.Naming)
                            screen.SubmitNickname("Capt");
                        if (!screen.Choosing)
                            continue;
                        if (!shotTaken)
                        {
                            Shot("battle");
                            shotTaken = true;
                        }
                        var b = screen.Battle;
                        if (b.NeedsSwitch)
                            screen.Replace(Enumerable.Range(0, b.Party.Count).First(i => b.CanSendOut(i) && !b.IsOut(i)));
                        else if (ball && b.Bag.Count(Core.Battle.ItemEffect.PokeBall) > 0)
                            screen.Choose(new Core.Battle.BattleChoice.Item(Core.Battle.ItemEffect.PokeBall));
                        else
                            screen.Choose(new Core.Battle.BattleChoice.Fight(0));
                    }
                    await Wait(2);
                    GD.Print($"[capture] battle over ({world.LastBattleOutcome}) at {world.PlayerTile}; mismatches {world.BattleMismatches}; party {string.Join(", ", world.OnlineState?.Party.Select(m => $"{m.Species} Lv{m.Level} HP{m.Hp}") ?? [])}; balls {world.OnlineState?.Bag.Where(i => i.Item == 4).Sum(i => i.Count)}; caught {world.OnlineState?.Progress.Caught}, wins {world.OnlineState?.Progress.WildWins}");
                }
                else if (line.StartsWith("shot:", StringComparison.Ordinal))
                    Shot(line["shot:".Length..]);
                else if (line.StartsWith("step:", StringComparison.Ordinal) && line.Length >= 7)
                {
                    var dirv = line[5] switch { 'U' => Vector2I.Up, 'D' => Vector2I.Down, 'L' => Vector2I.Left, _ => Vector2I.Right };
                    for (var n = 0; n < line[6] - '0'; n++)
                    {
                        for (var t = 0; t < 20 && !world.TryStep(dirv); t++)
                            await Wait(0.05);
                        await Wait(0.4);
                    }
                }
                else if (line.StartsWith("wait:", StringComparison.Ordinal))
                    await Wait(double.Parse(line["wait:".Length..], System.Globalization.CultureInfo.InvariantCulture));
                else if (line == "editor")
                {
                    // F10: the route editor (or the chat line saying why not).
                    world.ToggleRouteEditor();
                    await Wait(2.5);
                    GD.Print($"[capture] route editor open {world.RouteEditorOpen}");
                }
                else if (line.StartsWith("ed:", StringComparison.Ordinal) && world.RouteEditor is { } editor)
                {
                    var parts = line.Split(':');
                    switch (parts[1])
                    {
                        case "open": editor.Open(int.Parse(parts[2])); break;
                        case "gen": editor.Generate(int.Parse(parts[2])); break;
                        case "pond":
                            for (var y = 6; y < 12; y += 2)
                                editor.PaintForTest(20, y, Extractor.Extraction.TileKind.Ground, 4);
                            editor.PaintForTest(22, 8, Extractor.Extraction.TileKind.Water, 2);
                            break;
                        case "extras":
                            // A person by the entrance, a boulder on the path, a fence-ish stamp and an exit to Nuvema Town.
                            var file = editor.FileForTest;
                            var ends = Extractor.Extraction.UnchartedRoutes.Ends(file);
                            var (ex, ey) = (ends[0].X + 3, ends[0].Y);
                            editor.AddForTest(npc: new Extractor.Extraction.RouteNpc(ex, ey, 12, Facing: 2, Text: "Welcome to my route!\n\nWatch out for the boulder."));
                            editor.AddForTest(obstacle: new Extractor.Extraction.RouteObstacle(ex + 4, ey + 1, "strength"));
                            editor.AddForTest(stamp: new Extractor.Extraction.RouteStamp(ex + 2, ey - 6, 6, 4, 40, 40));
                            editor.AddForTest(wild: new Extractor.Extraction.RouteWild(509, 4, 6, 10));
                            if (ends.Count > 1)
                                editor.AddForTest(link: new Extractor.Extraction.RouteLink(ends[1].X, ends[1].Y, 389, 0));
                            editor.LookAtForTest(ex + 2, ey, 1.3f);
                            var f2 = editor.FileForTest;
                            GD.Print($"[capture] editor parts: npcs {f2.Npcs?.Count}, obstacles {f2.Obstacles?.Count}, stamps {f2.Stamps?.Count}, links {f2.Links?.Count}, wild {f2.Wild?.Count}");
                            break;
                        case "source": editor.ShowSourceForTest(); break;
                        case "tree":
                        {
                            // A small tree right in front of where the player arrives.
                            var start = Extractor.Extraction.UnchartedRoutes.Ends(editor.FileForTest)[0];
                            var (px, py) = (start.X, start.Y + (start.Along - 1) / 2);
                            editor.AddForTest(obstacle: new Extractor.Extraction.RouteObstacle(px + 1, py, "cut"));
                            break;
                        }
                        case "people":
                        {
                            // A trainer two tiles in from the entrance (facing it), a gift-giver beside them, and a route gate
                            // and a sign from other parts of Unova's maps, as objects on this route's ground.
                            var start = Extractor.Extraction.UnchartedRoutes.Ends(editor.FileForTest)[0];
                            // Where the player arrives: the middle of the entrance.
                            var (px, py) = (start.X, start.Y + (start.Along - 1) / 2);
                            editor.AddForTest(npc: new Extractor.Extraction.RouteNpc(px + 3, py, 20, Facing: 2,
                                Text: "Hold it! You're on my route.\n\nLet's battle!", Sight: 3,
                                Battle: new Extractor.Extraction.RouteTrainer("Ava", 4, [new(504, 3), new(506, 4)], Prize: 1200,
                                    Defeat: "You got me!", After: "Good luck out there.")));
                            editor.AddForTest(npc: new Extractor.Extraction.RouteNpc(px, py + 1, 40, Facing: 1, Text: "Here, take this!", GiftItem: 17, GiftCount: 3));
                            foreach (var (area, want, at) in new[] { (22, "gate", (px + 8, py - 10)), (26, "sign", (px + 5, py - 4)) })
                                if (editor.ObjectsOf(area).FirstOrDefault(o => o.Name.StartsWith(want, StringComparison.Ordinal)) is { } o)
                                    editor.AddForTest(stamp: new Extractor.Extraction.RouteStamp(at.Item1, at.Item2, o.W, o.H, o.X, o.Y, area, ObjectsOnly: true));
                            editor.SelectForTest(editor.FileForTest.Npcs!.Count - 2);
                            editor.LookAtForTest(px + 4, py - 3, 1.4f);
                            var f3 = editor.FileForTest;
                            GD.Print($"[capture] editor people: {string.Join("; ", f3.Npcs!.Select(n => n.Battle is { } t ? $"trainer {t.Name} ×{t.Party.Count} ₽{t.Prize}" : n.GiftItem > 0 ? $"gift {n.GiftItem}×{n.GiftCount}" : "talk"))}; stamps {string.Join(", ", f3.Stamps?.Select(st => $"{st.W}x{st.H} from {st.Area}{(st.ObjectsOnly ? " objects" : "")}") ?? [])}");
                            break;
                        }
                        case "save": GD.Print($"[capture] editor save sent {editor.Save()}"); break;
                        case "walk": editor.TestWalk(); break;
                    }
                    await Wait(1.5);
                    if (world.RouteEditor is { } still)
                        GD.Print($"[capture] editor: route {still.RouteNumber}, area {still.AreaSize}, status: {still.StatusText.Replace('\n', ' ')}");
                }
                else if (line.StartsWith("bag:", StringComparison.Ordinal))
                {
                    // bag:item:party — opens X, clicks the item in the Bag (and the Pokémon it asks for).
                    var parts = line.Split(':');
                    if (world.PlayerMenuOpen is null)
                        world.OpenOnlineMenuForTest();
                    await Wait(0.8);
                    world.PlayerMenuOpen?.UseItemForTest(int.Parse(parts[1]), parts.Length > 2 ? int.Parse(parts[2]) : -1);
                    await Wait(1.2);
                    GD.Print($"[capture] bag {parts[1]}: menu says \"{world.PlayerMenuOpen?.StatusText}\"");
                }
                else if (line == "close-menu")
                {
                    if (world.PlayerMenuOpen is not null)
                        world.OpenOnlineMenuForTest();
                    await Wait(0.5);
                }
                else if (line == "pages")
                {
                    // Reads and closes whatever dialogue comes up (a trainer who spotted the player), until a battle or quiet.
                    for (var quiet = 0; quiet < 12;)
                    {
                        await Wait(0.4);
                        if (world.DialogText is { Length: > 0 } text)
                        {
                            GD.Print($"[capture] says: {text.Replace('\n', ' ')}");
                            world.CloseDialog();
                            quiet = 0;
                        }
                        else
                            quiet++;
                        if (world.BattleOpen is not null)
                        {
                            GD.Print("[capture] a battle started");
                            break;
                        }
                    }
                }
                else if (line.StartsWith("ask:", StringComparison.Ordinal))
                {
                    GD.Print($"[capture] asked: {world.Question ?? "(nothing)"}");
                    world.AnswerForTest(line == "ask:yes");
                    await Wait(1.5);
                }
                else if (line == "battle?")
                    GD.Print($"[capture] in battle: {main.CurrentScreen is not World.OverworldScreen || world.InBattle}");
                else if (line.StartsWith("zoom:", StringComparison.Ordinal))
                {
                    world.Zoom = float.Parse(line["zoom:".Length..], System.Globalization.CultureInfo.InvariantCulture);
                    await Wait(1.5);
                }
                else if (line.StartsWith("face:", StringComparison.Ordinal))
                    world.FaceForTest(line[5] switch { 'U' => Vector2I.Up, 'D' => Vector2I.Down, 'L' => Vector2I.Left, _ => Vector2I.Right });
                else if (line == "talk")
                {
                    // Talks to whoever the player faces and reads every page shown, closing each (server answers too).
                    GD.Print($"[capture] talk at {world.PlayerTile}: {world.Interact()}");
                    for (var quiet = 0; quiet < 8;)
                    {
                        await Wait(0.4);
                        if (world.DialogText is { Length: > 0 } text)
                        {
                            GD.Print($"[capture] says: {text.Replace('\n', ' ')}");
                            if (quiet == 0)
                                Shot($"talk-{world.PlayerTile.X}-{world.PlayerTile.Y}");
                            world.CloseDialog();
                            quiet = 0;
                        }
                        else
                            quiet++;
                        if (world.BattleOpen is not null)
                        {
                            GD.Print("[capture] a battle started");
                            break;
                        }
                    }
                }
                else if (line == "marks")
                    GD.Print($"[capture] quest givers: {string.Join(", ", world.QuestGivers.Select(n => $"{n.Zone}:{n.Info.Id}@{n.Tile}"))}; hud: {world.QuestHudText.Replace("\n", " | ")}");
                else if (line == "menu")
                {
                    world.OpenOnlineMenuForTest();
                    await Wait(0.6);
                    Shot("05-menu");
                    world.OpenOnlineMenuForTest();
                }
                else
                    world.SubmitChat(line);
                await Wait(0.8);
            }
            for (var t = 0.0; t < stay; t += 1)
            {
                await Wait(1);
                if (Math.Abs(t - stay / 2) < 0.5)
                    Shot("05-later");
            }
            GD.Print($"[capture] spectating {world.SpectatingId}; admin mode {world.AdminMode}; party {string.Join(", ", world.OnlineState?.Party.Select(p => p.Species) ?? [])}");
            foreach (var line in world.ChatLines)
                GD.Print($"[capture] chat: {line}");
            GD.Print($"[capture] at the end at {world.PlayerTile}; sees {string.Join(", ", world.OtherPlayers)}");
            Shot("06-end");
        }
        finally
        {
            GD.Print("[capture] done");
            tree.Quit();
        }
    }

    /// <summary>
    /// Fights the weakest ROM trainer of battle type <paramref name="type"/> (1 double, 2 triple, 3 rotation) with a
    /// party of six, choosing each PokÃ©mon's first move through the menu, with screenshots of the field and of turns.
    /// </summary>
    static async void MultiBattle(Main main, string dir, int type)
    {
        var tree = main.GetTree();
        async Task Wait(double seconds) => await main.ToSignal(tree.CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        void Shot(string name) => main.GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, name + ".png"));
        for (var waited = 0.0; main.CurrentScreen is LoadingScreen && waited < 60; waited += 0.25)
            await Wait(0.25);
        await Wait(0.3);
        main.Push(() => new World.OverworldScreen());
        await Wait(1.5);
        if (main.CurrentScreen is World.OverworldScreen world)
        {
            world.RandomEncounters = false;
            var data = World.OfflineSession.Data;
            var party = World.OfflineSession.Party;
            var rng = new Random(3);
            foreach (var species in new[] { 498, 501, 506, 519, 532 })
                if (party.Count < 6)
                    party.Add(Core.Battle.Pokemon.Create(data, species, 30, rng));
            foreach (var mon in party)
            {
                mon.Hp = mon.MaxHp;
                mon.Status = Core.Data.Ailment.None;
            }
            // The low-level starter goes to the back so the battle lasts a few turns.
            var lead = party[0];
            party.RemoveAt(0);
            party.Add(lead);
            var trainer = World.OfflineSession.Trainers.Where(t => t.BattleType == type && t.Pokemon.Count >= 2)
                .OrderBy(t => t.Pokemon.Max(p => p.Level)).FirstOrDefault();
            GD.Print($"[capture] battle type {type}: {trainer?.Title} with {trainer?.Pokemon.Count} PokÃ©mon");
            if (trainer is not null)
            {
                world.StartTrainerBattle(trainer);
                for (var waited = 0.0; waited < 15 && world.CurrentBattle is null; waited += 0.2)
                    await Wait(0.2);
                for (var turn = 0; turn < 14; turn++)
                {
                    for (var waited = 0.0; waited < 30 && world.CurrentBattle is { Choosing: false, Battle.Over: false }; waited += 0.2)
                        await Wait(0.2);
                    if (world.CurrentBattle is not { } screen || screen.Battle.Over)
                        break;
                    var battle = screen.Battle;
                    if (turn == 0)
                        Shot($"type{type}-field");
                    GD.Print($"[capture] turn {turn}: {battle.Format}, width {battle.Width}; player {string.Join(", ", battle.PlayerField.Select(b => $"{b.Mon.Name} {b.Mon.Hp}/{b.Mon.MaxHp}"))}; foe {string.Join(", ", battle.FoeField.Select(b => $"{b.Mon.Name} {b.Mon.Hp}/{b.Mon.MaxHp}"))}");
                    if (battle.NeedsSwitch)
                    {
                        var next = Enumerable.Range(0, battle.Party.Count).First(i => battle.CanSendOut(i) && !battle.IsOut(i));
                        GD.Print($"[capture] sending out {battle.Party[next].Name} into slot {battle.SlotsToFill.FirstOrDefault()}");
                        screen.Replace(next);
                        await Wait(1.5);
                        continue;
                    }
                    if (battle.Format == Core.Battle.BattleFormat.Rotation && turn == 1 && battle.CanRotate)
                    {
                        var next = Enumerable.Range(0, battle.Party.Count).First(i => battle.RotationTeam.Contains(battle.Party[i]) && !battle.IsOut(i) && !battle.Party[i].Fainted);
                        foreach (var e in battle.Rotate(next).OfType<Core.Battle.MessageEvent>())
                            GD.Print($"[capture] {e.Text}");
                    }
                    for (var slot = 0; slot < battle.Width; slot++)
                    {
                        if (!screen.Choosing)
                            break;
                        screen.Choose(new Core.Battle.BattleChoice.Fight(0, Core.Battle.BattleSide.Foe, slot % battle.Width));
                        await Wait(0.1);
                    }
                    await Wait(2.2);
                    Shot($"type{type}-turn{turn}");
                }
                GD.Print($"[capture] after: {world.CurrentBattle?.Battle.Outcome}");
            }
        }
        GD.Print("[capture] done");
        tree.Quit();
    }

    /// <summary>Starts a wild battle and plays each move's ROM animation (the player's, then the foe's), with screenshots along the way.</summary>
    static async void MoveAnimations(Main main, string dir, int[] moves)
    {
        var tree = main.GetTree();
        async Task Wait(double seconds) => await main.ToSignal(tree.CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        for (var waited = 0.0; main.CurrentScreen is LoadingScreen && waited < 60; waited += 0.25)
            await Wait(0.25);
        await Wait(0.3);
        main.Push(() => new World.OverworldScreen());
        await Wait(1.5);
        if (main.CurrentScreen is World.OverworldScreen world)
        {
            world.RandomEncounters = false;
            world.StartBattle(Core.Battle.Pokemon.Create(World.OfflineSession.Data, 504, 3, new Random(7)));
            await Wait(4.5);
            if (world.CurrentBattle is { } screen)
                foreach (var move in moves)
                    foreach (var side in new[] { Core.Battle.BattleSide.Player, Core.Battle.BattleSide.Foe })
                    {
                        var play = screen.Effects.Play(move, side);
                        for (var shot = 0; shot < 4 && !play.IsCompleted; shot++)
                        {
                            await Wait(0.35);
                            main.GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, $"move{move}-{side}-{shot}.png"));
                        }
                        await play;
                        var (_, peak) = screen.Effects.Played.LastOrDefault();
                        GD.Print($"[capture] move {move} by {side}: peak {peak} particles");
                        await Wait(0.3);
                    }
        }
        GD.Print("[capture] done");
        tree.Quit();
    }

    /// <summary>Starts a wild battle and shows it over every backdrop the ROM has (with its matching platform).</summary>
    static async void Scenery(Main main, string dir)
    {
        var tree = main.GetTree();
        async Task Wait(double seconds) => await main.ToSignal(tree.CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        for (var waited = 0.0; main.CurrentScreen is LoadingScreen && waited < 60; waited += 0.25)
            await Wait(0.25);
        await Wait(0.3);
        main.Push(() => new World.OverworldScreen());
        await Wait(1.5);
        if (main.CurrentScreen is World.OverworldScreen world)
        {
            world.RandomEncounters = false;
            world.StartBattle(Core.Battle.Pokemon.Create(World.OfflineSession.Data, 504, 3, new Random(7)));
            await Wait(4.5);
            var folder = System.IO.Path.GetDirectoryName(ClientServices.UnovaAssets!.BattleSceneryPath("batt_bg01")) ?? "";
            foreach (var path in Directory.GetFiles(folder, "batt_bg*.gltf").Order())
            {
                var bg = System.IO.Path.GetFileNameWithoutExtension(path);
                // batt_bg04b â†’ batt_stage04b, else the plain number.
                var stage = $"batt_stage{bg["batt_bg".Length..]}";
                if (ClientServices.UnovaAssets.BattleSceneryPath(stage) is null)
                    stage = $"batt_stage{bg["batt_bg".Length..][..2]}";
                world.CurrentBattle?.SetScenery(bg, stage);
                await Wait(0.4);
                main.GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, $"{bg}.png"));
                GD.Print($"[capture] scenery {world.CurrentBattle?.Scenery}");
            }
        }
        GD.Print("[capture] done");
        tree.Quit();
    }

    /// <summary>Opens the world from the save a previous capture left and reports what came back.</summary>
    static async void Continue(Main main, string dir)
    {
        var tree = main.GetTree();
        async Task Wait(double seconds) => await main.ToSignal(tree.CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        for (var waited = 0.0; main.CurrentScreen is LoadingScreen && waited < 60; waited += 0.25)
            await Wait(0.25);
        await Wait(0.3);
        main.Push(() => new World.OverworldScreen());
        await Wait(2.5);
        main.GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, "30-continued.png"));
        var s = World.OfflineSession.State;
        GD.Print($"[capture] continued {World.OfflineSession.Continued} from {World.OfflineSession.SavePath}; at {s.Location}; party {string.Join(", ", s.Party.Select(p => $"{p.Name} Lv{p.Level} {p.Hp}/{p.MaxHp}"))}; box {s.Box.Count}; money {s.Progress.Money}; badges {string.Join(",", s.Progress.Badges.Keys)}; beaten {string.Join(",", s.Progress.DefeatedTrainers)}; quests {string.Join(",", s.Quests.Active.Select(q => q.Info.Id))}; done {s.Quests.Completed.Count()}; mail {s.Mail.Messages.Count} ({s.Mail.Unread} unread); listings {s.Market.Listings.Count}; friends {s.Friends.Friends.Count()}; play {World.OfflineSession.PlayTime}");
        if (main.CurrentScreen is World.OverworldScreen world)
        {
            world.ToggleMenu(World.GameMenu.Tab.Card);
            await Wait(0.5);
            main.GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, "31-continued-card.png"));
            world.ToggleMenu();
        }
        GD.Print("[capture] done");
        tree.Quit();
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
                world.RandomEncounters = false;   // scripted walks; the battles below are forced
                world.TrainerBattles = false;
                var season = world.Season;
                world.Zoom = 2.2f;   // far enough out to show Nuvema's houses
                foreach (var s in Enum.GetValues<Extractor.Extraction.Season>())
                {
                    world.Season = s;
                    await Wait(0.6);
                    Save($"09s-{s.ToString().ToLowerInvariant()}");
                }
                world.Season = season;
                await Wait(0.6);
                GD.Print($"[capture] animated materials: {world.AnimatedMaterials} ({world.SwappedMaterials} frame-swapped)");
                Save("09w-water-a");
                await Wait(0.7);
                Save("09w-water-b");
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
                        // A wandering NPC may be in the way for a moment.
                        for (var tries = 0; !world.TryStep(step, run) && tries < 20; tries++)
                            await Wait(0.1);
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
                GD.Print($"[capture] door {door.Key} â†’ zone {world.CurrentZone} at {world.PlayerTile}; {world.Npcs.Count()} NPCs");

                // Talk to the nearest NPC: walk next to it, face it, interact.
                var sides = new[] { Vector2I.Up, Vector2I.Down, Vector2I.Left, Vector2I.Right };
                if (world.Npcs.OrderBy(n => (n.Tile - world.PlayerTile).LengthSquared()).FirstOrDefault() is { } npc)
                {
                    await Walk(FindPath(world, world.PlayerTile, t => sides.Any(d => t + d == npc.Tile)), run: false);
                    if (sides.FirstOrDefault(d => world.PlayerTile + d == npc.Tile) is var face && face != Vector2I.Zero)
                    {
                        world.TryStep(face);   // blocked by the NPC: just turns
                        await Wait(0.1);
                        GD.Print($"[capture] talk to NPC {npc.Info.Id} (sprite {npc.Info.Sprite}): {world.Interact()}");
                        await Wait(0.3);
                        Save("10b-talk");
                        Input.ParseInputEvent(new InputEventAction { Action = "ui_accept", Pressed = true });
                        await Wait(0.2);
                        GD.Print($"[capture] dialogue closed: {!world.Talking}");
                    }
                }

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
                    GD.Print($"[capture] stairs â†’ zone {world.CurrentZone} at {world.PlayerTile}");
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
                GD.Print($"[capture] mat â†’ zone {world.CurrentZone} at {world.PlayerTile}");

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

                // Water: a borrowed Oshawott carries the player onto the nearest water, then the Super Rod is cast.
                {
                    var back = world.PlayerTile;
                    var backZone = world.CurrentZone;
                    var oshawott = Core.Battle.Pokemon.Create(World.OfflineSession.Data, 501, 10, new Random(4));
                    World.OfflineSession.Party.Add(oshawott);
                    Vector2I[] dirs = [Vector2I.Up, Vector2I.Down, Vector2I.Left, Vector2I.Right];
                    (Vector2I Land, Vector2I Dir)? shore = null;
                    for (var r = 1; r < 70 && shore is null; r++)
                    for (var dx = -r; dx <= r && shore is null; dx++)
                    for (var dy = -r; dy <= r && shore is null; dy++)
                    {
                        var land = back + new Vector2I(dx, dy);
                        if (world.IsWalkable(land))
                            foreach (var d in dirs)
                                if (world.IsWater(land + d))
                                {
                                    shore = (land, d);
                                    break;
                                }
                    }
                    if (shore is { } s && world.Teleport(backZone, s.Land))
                    {
                        await Wait(1.0);
                        world.TryStep(s.Dir);   // bumps the water: now facing it
                        await Wait(0.2);
                        world.Interact();
                        await Wait(1.2);
                        Press("ui_accept");
                        await Wait(0.3);
                        Save("14s-surf");
                        GD.Print($"[capture] surf from {s.Land} heading {s.Dir}: surfing {world.Surfing} at {world.PlayerTile}");
                        world.Fish();
                        for (var waited = 0.0; world.CurrentBattle is null && waited < 6; waited += 0.1)
                        {
                            if (world.Talking)
                                Press("ui_accept");
                            await Wait(0.2);
                        }
                        GD.Print($"[capture] fishing: {(world.CurrentBattle is { } fished ? $"hooked a {fished.Battle.Foe.Mon.Name}" : "no bite")}");
                        if (world.CurrentBattle is not null)
                        {
                            await Wait(3.5);
                            Save("14f-fishing-battle");
                            for (var attempt = 0; attempt < 10 && world.CurrentBattle is not null; attempt++)
                            {
                                for (var waited = 0.0; world.CurrentBattle is { Busy: true } && waited < 15; waited += 0.1)
                                    await Wait(0.1);
                                Press("ui_down");
                                Press("ui_right");
                                Press("ui_accept");
                                await Wait(0.5);
                            }
                            for (var waited = 0.0; world.CurrentBattle is not null && waited < 10; waited += 0.1)
                                await Wait(0.1);
                        }
                        Press("ui_accept");
                        await Wait(0.5);
                    }
                    else
                        GD.Print("[capture] no shore found near Route 1");
                    World.OfflineSession.Party.Remove(oshawott);
                    world.Teleport(backZone, back);
                    await Wait(1.0);
                }

                // Into tall grass: the player stands in it at ground height, not on top of the bushes.
                var beside = FindPath(world, world.PlayerTile, t => !world.IsTallGrass(t) && world.IsTallGrass(t + Vector2I.Up));
                await Walk(beside, run: false);
                var outside = world.PlayerHeight;
                await Walk([Vector2I.Up], run: false);
                await Wait(0.3);
                world.Zoom = 2.2f;
                await Wait(0.4);
                Save("14b-tall-grass");
                world.Zoom = 1;
                GD.Print($"[capture] tall grass at {world.PlayerTile}: height {world.PlayerHeight:F2} (beside it {outside:F2})");

                // A wild battle: a Sandile with Intimidate against the starter, fought with the first move until it ends.
                var lead = World.OfflineSession.Lead!;
                var sandile = Core.Battle.Pokemon.Create(World.OfflineSession.Data, 551, 3, new Random(7));
                sandile.SetAbilitySlot(0);
                world.StartBattle(sandile);
                await Wait(1.2);
                Save("15-battle-intro");
                for (var waited = 0.0; world.CurrentBattle is { LastAbility: null } && waited < 8; waited += 0.1)
                    await Wait(0.1);
                await Wait(0.3);
                Save("15a-ability");
                GD.Print($"[capture] ability banner: {world.CurrentBattle?.LastAbility ?? "none"}; {lead.Name}'s ability {lead.AbilityName}");
                void Press(string action)
                {
                    Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
                    Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
                }
                for (var waited = 0.0; world.CurrentBattle is { Busy: true } && waited < 15; waited += 0.1)
                    await Wait(0.1);
                Save("15b-battle-menu");
                Press("ui_accept");   // Fight
                await Wait(0.3);
                Save("15c-battle-moves");
                var battle = world.CurrentBattle?.Battle;
                for (var turn = 0; turn < 20 && world.CurrentBattle is not null; turn++)
                {
                    Press("ui_accept");   // first move (and Fight again on later turns)
                    await Wait(0.3);
                    if (turn == 0)
                    {
                        await Wait(1.6);
                        Save("15d-battle-turn");
                    }
                    for (var waited = 0.0; world.CurrentBattle is { Busy: true } && waited < 20; waited += 0.1)
                        await Wait(0.1);
                    await Wait(0.2);
                    if (world.CurrentBattle is { } open && !open.Battle.Over)
                    {
                        Press("ui_accept");   // Fight â†’ move list
                        await Wait(0.2);
                    }
                }
                for (var waited = 0.0; world.CurrentBattle is not null && waited < 10; waited += 0.1)
                    await Wait(0.1);
                await Wait(0.6);
                Save("16-after-battle");
                GD.Print($"[capture] battle {battle?.Outcome}: {lead.Name} Lv{lead.Level} HP {lead.Hp}/{lead.MaxHp} EXP {lead.Exp}; back in the overworld: {world.CurrentBattle is null}");

                // Catch a Lillipup: Bag â†’ first ball, until it's caught or the balls run out.
                if (world.MenuOpen)
                    world.ToggleMenu();
                Press("ui_accept");   // close any message box
                await Wait(0.5);
                world.StartBattle(Core.Battle.Pokemon.Create(World.OfflineSession.Data, 506, 2, new Random(3)));
                await Wait(1.2);
                for (var waited = 0.0; world.CurrentBattle is { Busy: true } && waited < 15; waited += 0.1)
                    await Wait(0.1);
                var catchBattle = world.CurrentBattle?.Battle;
                for (var throwNo = 0; throwNo < 10 && world.CurrentBattle is not null && !(catchBattle?.Over ?? true); throwNo++)
                {
                    Press("ui_right");   // Fight â†’ Bag
                    await Wait(0.1);
                    Press("ui_accept");
                    await Wait(0.4);
                    if (throwNo == 0)
                        Save("17-bag");
                    Press("ui_accept");   // the first ball
                    await Wait(throwNo == 0 ? 2.4 : 0.5);
                    if (throwNo == 0)
                        Save("17b-throw");
                    for (var waited = 0.0; world.CurrentBattle is { Busy: true, Naming: false } && waited < 20; waited += 0.1)
                        await Wait(0.1);
                    if (world.CurrentBattle is { Naming: true } naming)
                    {
                        // The nickname prompt after the catch.
                        await Wait(0.4);
                        Save("17c-nickname");
                        naming.SubmitNickname("Pup");
                        for (var waited = 0.0; world.CurrentBattle is { Busy: true } && waited < 10; waited += 0.1)
                            await Wait(0.1);
                    }
                    await Wait(0.2);
                    if (catchBattle is { NeedsSwitch: true })
                        break;
                }
                for (var waited = 0.0; world.CurrentBattle is not null && waited < 10; waited += 0.1)
                    await Wait(0.1);
                await Wait(0.6);
                Save("18-caught");
                GD.Print($"[capture] catch battle {catchBattle?.Outcome}; party: {string.Join(", ", World.OfflineSession.Party.Select(p => $"{p.Name} Lv{p.Level} {p.Hp}/{p.MaxHp}"))}; balls left {World.OfflineSession.Bag.Count(Core.Battle.ItemEffect.PokeBall)}");
                Press("ui_accept");   // close "added to your party"
                await Wait(0.3);
                world.ToggleMenu();
                await Wait(0.5);
                Save("19-party-menu");
                world.ToggleMenu();
                await Wait(0.2);

                // Held items: Snivy holds an Oran Berry from the Bag (eaten once it drops to half HP in a later battle).
                if (World.OfflineSession.Bag.Take(Core.Battle.HeldItems.OranBerry))
                    lead.HeldItem = Core.Battle.HeldItems.OranBerry;
                world.ToggleMenu();
                await Wait(0.5);
                Save("19h-holding");
                world.ToggleMenu();
                await Wait(0.2);

                // Weather: a Kyogre's Drizzle brings rain to the battle; then run (Run is bottom right of the commands).
                world.StartBattle(Core.Battle.Pokemon.Create(World.OfflineSession.Data, 382, 5, new Random(5)));
                for (var waited = 0.0; world.CurrentBattle is not { Battle.Weather: not Core.Battle.Weather.None } && waited < 10; waited += 0.1)
                    await Wait(0.1);
                await Wait(4.5);   // the intro messages, then Drizzle's banner and the rain
                Save("19w-rain");
                GD.Print($"[capture] weather battle: {world.CurrentBattle?.Battle.Weather}");
                for (var attempt = 0; attempt < 10 && world.CurrentBattle is not null; attempt++)
                {
                    for (var waited = 0.0; world.CurrentBattle is { Busy: true } && waited < 15; waited += 0.1)
                        await Wait(0.1);
                    Press("ui_down");
                    Press("ui_right");
                    Press("ui_accept");
                    await Wait(0.5);
                }
                for (var waited = 0.0; world.CurrentBattle is not null && waited < 10; waited += 0.1)
                    await Wait(0.1);
                await Wait(0.5);

                // PokÃ©mon Center: hurt the party, talk to the nurse across the counter in Accumula Town's Center.
                const int accumulaCenter = 398;
                if (world.ZoneNpcs(accumulaCenter).FirstOrDefault(n => n.Script == 2100) is { } nurse)
                {
                    foreach (var mon in World.OfflineSession.Party)
                        mon.Hp = 1;
                    var nurseTile = new Vector2I(nurse.X, nurse.Y);
                    world.Teleport(accumulaCenter, nurseTile + new Vector2I(0, 3));
                    await Wait(1.0);
                    await Walk(FindPath(world, world.PlayerTile, t => t + Vector2I.Up == nurseTile || (t + Vector2I.Up * 2 == nurseTile && !world.IsWalkable(t + Vector2I.Up))), run: false);
                    world.TryStep(Vector2I.Up);   // faces the counter
                    await Wait(0.2);
                    Save("20a-nurse-counter");
                    var talked = world.Interact();
                    await Wait(0.4);
                    Save("20-nurse");
                    GD.Print($"[capture] nurse at {nurseTile}, player {world.PlayerTile}: talked {talked}; party HP {string.Join(", ", World.OfflineSession.Party.Select(p => $"{p.Hp}/{p.MaxHp}"))}");
                    Press("ui_accept");
                    await Wait(0.3);

                    // The Center's PC, left of the nurse's counter: walk up in front of it, face it and boot it up.
                    var pcs = world.CenterPcTiles(accumulaCenter).ToHashSet();
                    var dirs = new[] { Vector2I.Up, Vector2I.Left, Vector2I.Right, Vector2I.Down };
                    var data = World.OfflineSession.Data;
                    foreach (var (species, level) in new[] { (504, 4), (506, 6), (519, 5), (509, 7) })
                        World.OfflineSession.Box.Add(Core.Battle.Pokemon.Create(data, species, level, new Random(species)));
                    await Walk(FindPath(world, world.PlayerTile, t => pcs.Contains(t + Vector2I.Up)), run: false);
                    var face = dirs.FirstOrDefault(d => pcs.Contains(world.PlayerTile + d));
                    world.TryStep(face);
                    await Wait(0.3);
                    Save("20ab-pc-front");
                    var booted = world.Interact();
                    await Wait(0.5);
                    Save("20b-pc");
                    var pcMenu = world.Menu;
                    var deposit = World.OfflineSession.State.Deposit(World.OfflineSession.Party.Count - 1);
                    pcMenu?.Show(World.GameMenu.Tab.Pc);
                    await Wait(0.3);
                    Save("20c-pc-deposited");
                    pcMenu?.PickPc(World.OfflineSession.Box.Count - 1);
                    await Wait(0.3);
                    Save("20d-pc-picked");
                    var withdraw = World.OfflineSession.State.Withdraw(World.OfflineSession.Box.Count - 1);
                    GD.Print($"[capture] PC tiles {string.Join(" ", pcs)}; player {world.PlayerTile} facing {face}: booted {booted}, tab {pcMenu?.Current}; {deposit.Message} / {withdraw.Message}; box {World.OfflineSession.Box.Count}");
                    if (world.MenuOpen)
                        world.ToggleMenu();
                    await Wait(0.2);

                    // The PokÃ© Mart clerks behind the right-hand counter: talk across it, buy ten PokÃ© Balls.
                    if (world.ZoneNpcs(accumulaCenter).FirstOrDefault(n => n.Sprite == 77) is { } clerk)
                    {
                        var clerkTile = new Vector2I(clerk.X, clerk.Y);
                        await Walk(FindPath(world, world.PlayerTile, t => dirs.Any(d => t + d == clerkTile || (t + d * 2 == clerkTile && !world.IsWalkable(t + d)))), run: false);
                        var toward = dirs.First(d => world.PlayerTile + d == clerkTile || world.PlayerTile + d * 2 == clerkTile);
                        world.TryStep(toward);
                        await Wait(0.3);
                        var shopping = world.Interact();
                        await Wait(0.5);
                        Save("20e-mart");
                        var bought = Core.Player.PokeMart.Buy(World.OfflineSession.State, data, Core.Battle.ItemEffect.PokeBall, 10);
                        world.Menu?.Show(World.GameMenu.Tab.Mart);
                        await Wait(0.3);
                        Save("20f-mart-bought");
                        GD.Print($"[capture] clerk at {clerkTile}, player {world.PlayerTile}: shop {shopping}, tab {world.Menu?.Current}; {bought.Message} money {World.OfflineSession.Money}");
                        if (world.MenuOpen)
                            world.ToggleMenu();
                        await Wait(0.2);
                    }

                    // Ride the right-hand escalator up to the raised floor: the sprite must stay whole on the steps.
                    var climb = FindPath(world, world.PlayerTile, t => t == new Vector2I(12, 7));
                    for (var i = 0; i < climb.Count; i++)
                    {
                        world.TryStep(climb[i], run: false);
                        await Wait(0.12);
                        if (world.PlayerTile.Y is >= 7 and <= 12)
                            Save($"20g-stairs-{i:D2}");
                        while (world.Busy)
                            await Wait(0.05);
                    }
                    await Wait(0.3);
                    Save("20h-upstairs");
                    GD.Print($"[capture] escalator: {climb.Count} steps to {world.PlayerTile}, height {world.PlayerHeight:0.00}");
                }

                // Trainer: Youngster Jimmy on Route 2 spots the player, walks up and battles a levelled-up Snivy.
                const int route2 = 319;
                if (world.ZoneNpcs(route2).FirstOrDefault(n => n.TrainerId == 1) is { } jimmy)
                {
                    var data = World.OfflineSession.Data;
                    // One point short of Lv 16, where Snivy learns a fifth move: the win asks which to forget.
                    lead.GainExp(Core.Battle.ExpCurve.ForLevel(lead.Species.GrowthRate, 16) - 1 - lead.Exp, data);
                    World.OfflineSession.HealParty();
                    await Wait(0.5);
                    var eye = new Vector2I(jimmy.X, jimmy.Y);
                    world.Teleport(route2, eye + new Vector2I(0, jimmy.Sight + 3));
                    await Wait(1.2);
                    world.TrainerBattles = true;
                    var approach = FindPath(world, world.PlayerTile, t => t.X == eye.X && t.Y > eye.Y && t.Y <= eye.Y + jimmy.Sight);
                    GD.Print($"[capture] Jimmy at {eye} sight {jimmy.Sight}; walking {approach.Count} steps from {world.PlayerTile}");
                    foreach (var step in approach)
                    {
                        world.TryStep(step);
                        await Wait(0.35);
                        if (world.Busy)
                            break;
                    }
                    await Wait(0.4);
                    Save("21-trainer-spotted");
                    var introLines = 0;
                    for (var waited = 0.0; world.CurrentBattle is null && waited < 15; waited += 0.1)
                    {
                        if (world.Talking)
                        {
                            // Jimmy's opening line from the ROM, a page at a time.
                            if (introLines++ == 0)
                                Save("21b-trainer-intro");
                            await Wait(0.3);
                            Press("ui_accept");
                        }
                        await Wait(0.1);
                    }
                    GD.Print($"[capture] trainer intro pages: {introLines}");
                    await Wait(1.6);
                    Save("22-trainer-battle");
                    var trainerBattle = world.CurrentBattle?.Battle;
                    var learnedMoves = "";
                    for (var turn = 0; turn < 30 && world.CurrentBattle is not null; turn++)
                    {
                        for (var waited = 0.0; world.CurrentBattle is { Busy: true, Asking: false } && waited < 20; waited += 0.1)
                            await Wait(0.1);
                        await Wait(0.2);
                        if (world.CurrentBattle is { Asking: true })
                        {
                            // A fifth move: forget the first one (the focused choice).
                            Save("22b-learn-move");
                            Press("ui_accept");
                            await Wait(0.3);
                            learnedMoves = string.Join(", ", lead.Moves.Select(m => m.Move.Name));
                            turn--;
                            continue;
                        }
                        if (world.CurrentBattle is not { Battle.Over: false })
                            break;
                        Press("ui_accept");   // Fight
                        await Wait(0.2);
                        Press("ui_accept");   // first move
                        await Wait(0.3);
                    }
                    for (var waited = 0.0; world.CurrentBattle is not null && waited < 10; waited += 0.1)
                        await Wait(0.1);
                    await Wait(0.6);
                    Save("23-after-trainer");
                    GD.Print($"[capture] learn-move prompt answered: {(learnedMoves == "" ? "never asked" : learnedMoves)}");
                    GD.Print($"[capture] {lead.Name} holds {(lead.HeldItem == 0 ? "nothing (its Oran Berry was eaten)" : World.OfflineSession.Data.ItemName(lead.HeldItem))}");
                    GD.Print($"[capture] trainer battle {trainerBattle?.Outcome} vs {trainerBattle?.Trainer?.Title}; money {World.OfflineSession.Money}; beaten {string.Join(",", World.OfflineSession.DefeatedTrainers)}; spotted again: {world.Busy}");
                }

                // Gym: a levelled-up Snivy takes on Striaton's Chili for the Trio Badge, which finishes the story quests.
                Press("ui_accept");
                await Wait(0.3);
                if (World.OfflineSession.Trainers.FirstOrDefault(t => t.Name == "Chili" && t.ClassName == "Leader") is { } chili)
                {
                    lead.GainExp(Core.Battle.ExpCurve.ForLevel(lead.Species.GrowthRate, 40) - lead.Exp, World.OfflineSession.Data);
                    World.OfflineSession.HealParty();
                    world.StartTrainerBattle(chili);
                    await Wait(1.5);
                    var gym = world.CurrentBattle?.Battle;
                    for (var turn = 0; turn < 30 && world.CurrentBattle is not null; turn++)
                    {
                        for (var waited = 0.0; world.CurrentBattle is { Busy: true, Asking: false } && waited < 20; waited += 0.1)
                            await Wait(0.1);
                        await Wait(0.2);
                        if (world.CurrentBattle is { Asking: true })
                        {
                            Press("ui_accept");   // forget the first move
                            await Wait(0.3);
                            turn--;
                            continue;
                        }
                        if (world.CurrentBattle is not { Battle.Over: false })
                            break;
                        Press("ui_accept");
                        await Wait(0.2);
                        Press("ui_accept");
                        await Wait(0.3);
                    }
                    for (var waited = 0.0; world.CurrentBattle is not null && waited < 10; waited += 0.1)
                        await Wait(0.1);
                    await Wait(0.8);
                    Save("24-badge");
                    var p = World.OfflineSession.Progress;
                    GD.Print($"[capture] gym {gym?.Outcome}: badges {string.Join(",", p.Badges.Keys)}; quests active {string.Join(", ", World.OfflineSession.Quests.Active.Select(q => q.Info.Id))}; done {string.Join(", ", World.OfflineSession.Quests.Completed.Select(c => c.Quest.Id))}; mail {World.OfflineSession.Mail.Messages.Count} ({World.OfflineSession.Mail.Unread} unread)");
                    Press("ui_accept");
                    await Wait(0.3);
                }

                // The X menu, one tab at a time.
                world.ToggleMenu();
                await Wait(0.4);
                if (world.Menu is { } menu)
                {
                    foreach (var tab in Enum.GetValues<World.GameMenu.Tab>().Except([World.GameMenu.Tab.Pc, World.GameMenu.Tab.Mart]))
                    {
                        menu.Show(tab);
                        await Wait(0.3);
                        if (tab == World.GameMenu.Tab.Mail)
                        {
                            Press("ui_accept");   // open the newest letter
                            await Wait(0.3);
                        }
                        Save($"25-menu-{tab.ToString().ToLowerInvariant()}");
                    }
                    GD.Print($"[capture] market {World.OfflineSession.Market.Listings.Count} listings; friends {World.OfflineSession.Friends.Friends.Count()}; mail unread {World.OfflineSession.Mail.Unread}");
                    world.ToggleMenu();
                    await Wait(0.3);
                }
            }
            if (main.CurrentScreen is World.OverworldScreen last)
            {
                last.SaveGame();
                GD.Print($"[capture] saved to {World.OfflineSession.SavePath}: {File.Exists(World.OfflineSession.SavePath)}; at {World.OfflineSession.State.Location}");
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
