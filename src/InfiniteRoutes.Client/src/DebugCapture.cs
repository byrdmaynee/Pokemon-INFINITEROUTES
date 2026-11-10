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
                GD.Print($"[capture] door {door.Key} → zone {world.CurrentZone} at {world.PlayerTile}; {world.Npcs.Count()} NPCs");

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
                        Press("ui_accept");   // Fight → move list
                        await Wait(0.2);
                    }
                }
                for (var waited = 0.0; world.CurrentBattle is not null && waited < 10; waited += 0.1)
                    await Wait(0.1);
                await Wait(0.6);
                Save("16-after-battle");
                GD.Print($"[capture] battle {battle?.Outcome}: {lead.Name} Lv{lead.Level} HP {lead.Hp}/{lead.MaxHp} EXP {lead.Exp}; back in the overworld: {world.CurrentBattle is null}");

                // Catch a Lillipup: Bag → first ball, until it's caught or the balls run out.
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
                    Press("ui_right");   // Fight → Bag
                    await Wait(0.1);
                    Press("ui_accept");
                    await Wait(0.4);
                    if (throwNo == 0)
                        Save("17-bag");
                    Press("ui_accept");   // the first ball
                    await Wait(throwNo == 0 ? 2.4 : 0.5);
                    if (throwNo == 0)
                        Save("17b-throw");
                    for (var waited = 0.0; world.CurrentBattle is { Busy: true } && waited < 20; waited += 0.1)
                        await Wait(0.1);
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

                // Pokémon Center: hurt the party, talk to the nurse across the counter in Accumula Town's Center.
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

                    // The Poké Mart clerks behind the right-hand counter: talk across it, buy ten Poké Balls.
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
                    for (var waited = 0.0; world.CurrentBattle is null && waited < 8; waited += 0.1)
                        await Wait(0.1);
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
