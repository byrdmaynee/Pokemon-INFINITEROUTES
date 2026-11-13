using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;
using InfiniteRoutes.Extractor.Extraction;
using InfiniteRoutes.Server.Accounts;
using InfiniteRoutes.Server.Admin;
using InfiniteRoutes.Server.Config;
using InfiniteRoutes.Server.Data;

namespace InfiniteRoutes.Server.World;

/// <summary>A connected player's way back to their client.</summary>
public interface IClientLink
{
    void Send(Message message);
    void Close(string reason);
}

/// <summary>A character in the world, with its account's staff standing and its game state.</summary>
public sealed class WorldPlayer(int id, Character character, Account account, GameState? state, IClientLink link, int channel, string map)
{
    public int Id { get; } = id;
    public long CharacterId { get; } = character.Id;
    /// <summary>The HM obstacles (zone, NPC id) this player has cleared on the map they're on.</summary>
    public HashSet<(int Zone, int Npc)> Cleared { get; } = [];
    public string Name { get; } = character.Name;
    public int Appearance { get; } = character.Appearance;
    public long AccountId { get; } = account.Id;
    public string AccountName { get; } = account.Name;
    /// <summary>The account's role name (its permissions come from the server's <see cref="RoleService"/>, so edits apply at once).</summary>
    public string RoleName { get; internal set; } = account.Role;
    public IClientLink Link { get; } = link;
    public int Channel { get; set; } = channel;
    public string Map { get; set; } = map;
    public int Zone { get; set; } = character.Zone;
    public int X { get; set; } = character.X;
    public int Y { get; set; } = character.Y;
    public int Facing { get; set; } = character.Facing;
    /// <summary>The character's game (party, Bag, progress); null when the server has no game data.</summary>
    public GameState? State { get; internal set; } = state;

    /// <summary>In Admin Mode (password + code checked), until <see cref="ElevatedUntil"/> unless they keep acting.</summary>
    public bool Elevated { get; internal set; }
    internal DateTime ElevatedUntil;
    public bool Invisible { get; internal set; }
    public bool Noclip { get; internal set; }
    /// <summary>Chat is blocked until then (kept on the account, so logging out doesn't lift it).</summary>
    public DateTime? MutedUntil { get; internal set; } = account.MutedUntil;
    public string? MuteReason { get; internal set; } = account.MuteReason;
    /// <summary>The player this staff member is watching (they follow them, invisible), or null.</summary>
    public WorldPlayer? Spectating { get; internal set; }

    /// <summary>Characters this player has blocked (their whispers don't arrive).</summary>
    internal HashSet<long> Blocked { get; set; } = [];

    /// <summary>The battle the player is in, if any (they can't walk meanwhile).</summary>
    public ActiveBattle? Battle { get; internal set; }

    /// <summary>Moved or turned since the last update to the players who see them.</summary>
    internal bool Moved;
    internal bool Ran;
    /// <summary>Position or game state not written to the database yet.</summary>
    internal bool Dirty;
    internal bool StateDirty;
    /// <summary>When this player was last written (any immediate write restarts the batch timer).</summary>
    internal DateTime LastSaved = DateTime.UtcNow;
    /// <summary>The players this one sees now.</summary>
    internal readonly HashSet<int> Seen = [];
    /// <summary>Seconds of movement allowed right now (refills with time, so bursts of lag are fine).</summary>
    internal double StepBudget = 0.5;
    internal DateTime LastStep = DateTime.UtcNow;
    internal DateTime LastInput = DateTime.UtcNow;
    internal readonly Queue<DateTime> ChatTimes = new();

    public NetLocation Location => new(Zone, X, Y, Facing);
    public NetPlayer AsNet => new(Id, Name, Appearance, Location, BuddySpecies, BuddyShiny, BuddyFemale, Beacon);

    /// <summary>The beacon over this player's head: their role's.</summary>
    public string Beacon { get; internal set; } = "";

    /// <summary>The species following the player (0 none), as others are shown it.</summary>
    internal int BuddySpecies;
    internal bool BuddyShiny, BuddyFemale;
}

/// <summary>
/// The shared world (BUILD_SPEC §4.2, §4.3): players on channels, server-checked movement, who sees whom, chat, and
/// each character's game state. Every step is checked against the map's collision and a speed budget; warps only from
/// the tile they're on or next to. Players see others on the same channel and map (on the overworld, within
/// <see cref="ViewRadius"/> tiles); each <see cref="Tick"/> (10 a second) sends what came into view, what moved and
/// what left.
/// </summary>
/// <remarks>
/// Saving follows the agreed plan: map changes, leaving and important events write the player at once (position and
/// any changed state together), and each write restarts that player's batch timer; otherwise changes are written
/// <see cref="ServerConfig.SaveIntervalSeconds"/> after the last write. Everything is written on shutdown. All methods
/// are thread-safe.
/// </remarks>
public sealed partial class WorldService(IWorldData world, ServerConfig config, Func<IReadOnlyList<PlayerSave>, Task> save,
    Func<GameState, string>? stateJson, RoleService roles, Game.GameRules? rules = null)
{
    /// <summary>The player's role as it is now.</summary>
    public RoleInfo RoleOf(WorldPlayer p) => roles.Get(p.RoleName);

    public const int ViewRadius = 40;
    public const int MaxChat = 200;
    const double WalkSeconds = 0.26, RunSeconds = 0.13;
    /// <summary>Steps may come this much faster than the client's own pace (timer jitter) before they're refused.</summary>
    const double Tolerance = 0.7;
    const double MaxBudget = 0.6;

    readonly object _gate = new();
    readonly Dictionary<int, WorldPlayer> _players = [];
    readonly List<PlayerSave> _urgent = [];
    int _nextId = 1;

    public int Online
    {
        get
        {
            lock (_gate)
                return _players.Count;
        }
    }

    public IReadOnlyList<int> ChannelCounts()
    {
        lock (_gate)
            return Enumerable.Range(1, config.Channels).Select(c => _players.Values.Count(p => p.Channel == c)).ToList();
    }

    /// <summary>Everyone online now (a snapshot).</summary>
    public IReadOnlyList<WorldPlayer> Players()
    {
        lock (_gate)
            return [.. _players.Values.OrderBy(p => p.Id)];
    }

    /// <summary>The online player whose character (or, failing that, account) is called <paramref name="name"/>.</summary>
    public WorldPlayer? Find(string name)
    {
        lock (_gate)
            return _players.Values.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? _players.Values.FirstOrDefault(p => p.AccountName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Puts a character into the world (the first channel with room) with its game state; returns the player, or the
    /// reason it can't.
    /// </summary>
    public (WorldPlayer? Player, string? Refusal) Join(Character character, Account account, GameState? state, IClientLink link)
    {
        WorldPlayer? previous;
        WorldPlayer player;
        lock (_gate)
        {
            previous = _players.Values.FirstOrDefault(p => p.CharacterId == character.Id);
            if (previous is not null)
            {
                // Logged in again while still on: carry on from where the live session was, not the last save.
                (character.Zone, character.X, character.Y, character.Facing) = (previous.Zone, previous.X, previous.Y, previous.Facing);
                if (previous.State is { } live)
                    state = live;
                RemoveLocked(previous);
            }
            if (_players.Count >= config.MaxPlayers && !roles.Get(account.Role).IsStaff)
                return (null, "The server is full.");
            var counts = Enumerable.Range(1, config.Channels).Select(c => (Channel: c, Count: _players.Values.Count(p => p.Channel == c))).ToList();
            // Fill channels in order, so players meet rather than being spread thin.
            var channel = counts.FirstOrDefault(c => c.Count < config.ChannelCapacity);
            if (channel.Channel == 0)
                return (null, "Every channel is full.");
            // A character somewhere the data pack doesn't know goes back to the start.
            if (world.MapOf(character.Zone) is not { } map || world.Blocked(character.Zone, character.X, character.Y))
            {
                (character.Zone, character.X, character.Y, character.Facing) = (world.Start.Zone, world.Start.X, world.Start.Y, world.Start.Facing);
                map = world.MapOf(character.Zone) ?? "";
            }
            player = new WorldPlayer(_nextId++, character, account, state, link, channel.Channel, map) { Dirty = true };
            player.Beacon = roles.Get(account.Role).Beacon;
            _players[player.Id] = player;
        }
        previous?.Link.Close("You logged in somewhere else.");
        return (player, null);
    }

    /// <summary>Takes a player out of the world (disconnected or kicked) and writes them at once.</summary>
    public void Leave(WorldPlayer player)
    {
        lock (_gate)
            RemoveLocked(player);
    }

    void RemoveLocked(WorldPlayer player)
    {
        if (!_players.ContainsKey(player.Id))
            return;
        // Leaving mid-battle ends it where it stands (what happened so far counts).
        FinishBattleLocked(player);
        _players.Remove(player.Id);
        SaveNowLocked(player);
        foreach (var other in _players.Values)
            if (other.Seen.Remove(player.Id))
                other.Link.Send(new PlayersLeft([player.Id]));
    }

    PlayerSave SaveOf(WorldPlayer p) =>
        new(p.CharacterId, p.Zone, p.X, p.Y, p.Facing, p.StateDirty && p.State is { } s && stateJson is not null ? stateJson(s) : null);

    /// <summary>An immediate write of everything changed for this player; restarts their batch timer.</summary>
    void SaveNowLocked(WorldPlayer p)
    {
        _urgent.Add(SaveOf(p));
        p.Dirty = p.StateDirty = false;
        p.LastSaved = DateTime.UtcNow;
    }

    /// <summary>
    /// Writes the player at once (catches, badges, trades, battle results, encounter starts … call this) and restarts
    /// their batch timer.
    /// </summary>
    public void SaveNow(WorldPlayer player)
    {
        lock (_gate)
            if (_players.ContainsKey(player.Id))
                SaveNowLocked(player);
    }

    /// <summary>Handles one message from a player's client (admin messages go to <see cref="AdminService"/>).</summary>
    public void Handle(WorldPlayer player, Message message)
    {
        lock (_gate)
        {
            if (!_players.ContainsKey(player.Id))
                return;
            player.LastInput = DateTime.UtcNow;
            switch (message)
            {
                case Step step:
                    var result = TryStep(player, step);
                    player.Link.Send(result);
                    if (result is StepResult { Accepted: true })
                        AfterStepLocked(player);
                    break;
                case BattleInput input:
                    BattleInputLocked(player, input);
                    break;
                case Warp warp:
                    player.Link.Send(TryWarp(player, warp));
                    break;
                case Turn turn when turn.Facing is >= 0 and <= 3:
                    player.Facing = turn.Facing;
                    player.Moved = true;
                    player.Ran = false;
                    break;
                case SwitchChannel sw:
                    player.Link.Send(TrySwitch(player, sw.Channel));
                    break;
                case Say say:
                    Chat(player, say);
                    break;
                case SetLead lead:
                    MakeLead(player, lead.Index);
                    break;
                case SetBuddy buddy:
                    ChooseBuddy(player, buddy.Index);
                    break;
                case UseItem use:
                    UseItemLocked(player, use);
                    break;
                case NpcAction npc:
                    NpcActionLocked(player, npc);
                    break;
                case Ping ping:
                    player.Link.Send(new Pong(ping.Time));
                    break;
            }
        }
    }

    static int FacingOf(int dx, int dy) => (dx, dy) switch { (0, -1) => 1, (-1, 0) => 2, (1, 0) => 3, _ => 0 };

    Message TryStep(WorldPlayer p, Step step)
    {
        if (p.Spectating is not null || p.Battle is not null)
            return new StepResult(step.Seq, false, p.Location);
        var now = DateTime.UtcNow;
        p.StepBudget = Math.Min(MaxBudget, p.StepBudget + (now - p.LastStep).TotalSeconds);
        p.LastStep = now;
        if (Math.Abs(step.Dx) + Math.Abs(step.Dy) != 1)
            return new StepResult(step.Seq, false, p.Location);
        var cost = (step.Run ? RunSeconds : WalkSeconds) * Tolerance;
        p.Facing = FacingOf(step.Dx, step.Dy);
        int x = p.X + step.Dx, y = p.Y + step.Dy;
        // Admin Mode's noclip walks through anything, at any speed.
        var noclip = p.Elevated && p.Noclip;
        // An HM obstacle is in the way until this player clears it (it's back when they leave the map).
        var obstacle = world.ObstacleAt(p.Zone, x, y) is { } o && !p.Cleared.Contains((p.Zone, o.Id));
        if (!noclip && (p.StepBudget < cost || world.Blocked(p.Zone, x, y) || obstacle))
        {
            p.Moved = true;
            return new StepResult(step.Seq, false, p.Location);
        }
        if (!noclip)
            p.StepBudget -= cost;
        (p.X, p.Y) = (x, y);
        if (world.IsShared(p.Map) && world.ZoneAt(p.Map, x, y) is { } zone)
            p.Zone = zone;
        p.Moved = p.Dirty = true;
        p.Ran = step.Run;
        return new StepResult(step.Seq, true, p.Location);
    }

    Message TryWarp(WorldPlayer p, Warp warp)
    {
        var reject = new StepResult(warp.Seq, false, p.Location);
        if (p.Battle is not null)
            return reject;
        if (world.WarpAt(p.Zone, warp.X, warp.Y) is not { } w)
            return reject;
        var distance = Math.Abs(warp.X - p.X) + Math.Abs(warp.Y - p.Y);
        // Mats and map edges are taken standing on them; doors, stairs and edges on a solid tile (town gates) from the
        // tile in front.
        var standOn = w.Kind == WarpKind.Mat || w.Kind == WarpKind.Edge && !world.Blocked(p.Zone, warp.X, warp.Y);
        if (standOn ? distance != 0 : distance != 1)
            return reject;
        if (world.Destination(w) is not { } dest || world.MapOf(dest.Zone) is not { } map)
            return reject;
        MoveLocked(p, dest.Zone, dest.X, dest.Y, dest.Facing, map);
        return new Warped(warp.Seq, p.Location);
    }

    void MoveLocked(WorldPlayer p, int zone, int x, int y, int facing, string map)
    {
        if (map != p.Map)
            p.Cleared.Clear();
        (p.Zone, p.X, p.Y, p.Facing, p.Map) = (zone, x, y, facing, map);
        p.Moved = true;
        p.Ran = false;
        p.StepBudget = MaxBudget;
        // A map change is written at once.
        SaveNowLocked(p);
    }

    Message TrySwitch(WorldPlayer p, int channel)
    {
        if (channel < 1 || channel > config.Channels)
            return new Notice($"There's no channel {channel}.", p.Channel);
        if (channel == p.Channel)
            return new Notice($"You're already on channel {channel}.", p.Channel);
        if (_players.Values.Count(o => o.Channel == channel) >= config.ChannelCapacity)
            return new Notice($"Channel {channel} is full.", p.Channel);
        p.Channel = channel;
        return new Notice($"Moved to channel {channel}.", channel);
    }

    bool Sees(WorldPlayer viewer, WorldPlayer other) =>
        viewer != other && viewer.Channel == other.Channel && viewer.Map == other.Map
        // Invisible staff are seen only by other staff in Admin Mode.
        && (!(other.Elevated && other.Invisible) || viewer.Elevated)
        && (world.IsShared(viewer.Map)
            ? Math.Abs(viewer.X - other.X) <= ViewRadius && Math.Abs(viewer.Y - other.Y) <= ViewRadius
            : viewer.Zone == other.Zone);

    // ---- Chat ---------------------------------------------------------------------------------------------------

    /// <summary>Text as it may be shown: no control characters, trimmed, at most <see cref="MaxChat"/> characters.</summary>
    public static string Clean(string? text)
    {
        var clean = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > MaxChat ? clean[..MaxChat] : clean;
    }

    void Chat(WorldPlayer p, Say say)
    {
        var text = Clean(say.Text);
        if (text.Length == 0)
            return;
        var now = DateTime.UtcNow;
        if (p.MutedUntil is { } until && until > now)
        {
            p.Link.Send(new ChatLine(ChatKind.System, "", $"You're muted until {until.ToLocalTime():HH:mm}{(p.MuteReason is { Length: > 0 } r ? $" ({r})" : "")}."));
            return;
        }
        while (p.ChatTimes.Count > 0 && (now - p.ChatTimes.Peek()).TotalSeconds > config.ChatWindowSeconds)
            p.ChatTimes.Dequeue();
        if (p.ChatTimes.Count >= config.ChatLines && !p.Elevated)
        {
            p.Link.Send(new ChatLine(ChatKind.System, "", "You're sending messages too fast. Wait a moment."));
            return;
        }
        p.ChatTimes.Enqueue(now);
        var line = new ChatLine(say.Kind, p.Name, text, null, p.Elevated);
        switch (say.Kind)
        {
            case ChatKind.Local:
                foreach (var o in _players.Values)
                    if (o == p || Sees(o, p))
                        o.Link.Send(line);
                break;
            case ChatKind.Global:
                foreach (var o in _players.Values)
                    o.Link.Send(line);
                break;
            case ChatKind.Whisper:
                var to = _players.Values.FirstOrDefault(o => o.Name.Equals(say.To?.Trim(), StringComparison.OrdinalIgnoreCase));
                // Invisible staff can't be found by whispering their name.
                if (to is null || (to.Elevated && to.Invisible && !p.Elevated) || to.Blocked.Contains(p.CharacterId))
                {
                    p.Link.Send(new ChatLine(ChatKind.System, "", $"{say.To} isn't online."));
                    return;
                }
                var whisper = line with { To = to.Name };
                to.Link.Send(whisper);
                if (to != p)
                    p.Link.Send(whisper);
                break;
        }
    }

    // ---- Game state -------------------------------------------------------------------------------------------------

    void MakeLead(WorldPlayer p, int index)
    {
        if (p.State is not { } state || index <= 0 || index >= state.Party.Count || p.Battle is not null)
            return;
        var mon = state.Party[index];
        state.Party.RemoveAt(index);
        state.Party.Insert(0, mon);
        p.StateDirty = true;
        SendState(p);
    }

    /// <summary>An item from the Bag outside battle: medicine on a party Pokémon, a Repel, or an item to hold.</summary>
    void UseItemLocked(WorldPlayer p, UseItem use)
    {
        if (rules is null || p.State is not { } state || p.Battle is not null)
            return;
        void Say(string text) => p.Link.Send(new Notice(text));
        var name = rules.Data.ItemName(use.Item);
        if (state.Bag.Count(use.Item) <= 0)
        {
            Say($"You have no {name}.");
            return;
        }
        var mon = use.Party >= 0 && use.Party < state.Party.Count ? state.Party[use.Party] : null;
        string done;
        if (use.Hold)
        {
            if (mon is null || !state.Bag.Take(use.Item))
                return;
            var swapped = mon.HeldItem;
            if (swapped != 0)
                state.Bag.Add(swapped);
            mon.HeldItem = use.Item;
            done = swapped != 0
                ? $"{mon.Name} was given the {name} to hold; the {rules.Data.ItemName(swapped)} went back to the Bag."
                : $"{mon.Name} was given the {name} to hold.";
        }
        else if (Core.Battle.ItemEffect.RepelSteps(use.Item) > 0)
        {
            var (ok, message) = state.UseRepel(use.Item, name);
            Say(message);
            if (!ok)
                return;
            done = "";
        }
        else if (Core.Battle.ItemEffect.For(use.Item) is Core.Battle.ItemEffect.Heal or Core.Battle.ItemEffect.Cure or Core.Battle.ItemEffect.Revive)
        {
            if (mon is null)
                return;
            if (Core.Battle.ItemUse.Apply(Core.Battle.ItemEffect.For(use.Item)!, mon) is not { } message)
            {
                Say("It won't have any effect.");
                return;
            }
            state.Bag.Take(use.Item);
            done = message;
        }
        else
        {
            Say($"The {name} can't be used here.");
            return;
        }
        p.StateDirty = true;
        SendState(p);
        if (done.Length > 0)
            Say(done);
    }

    void ChooseBuddy(WorldPlayer p, int index)
    {
        if (p.State is not { } state || p.Battle is not null)
            return;
        var (ok, message) = state.SetBuddy(index);
        p.Link.Send(new Notice(message));
        if (!ok)
            return;
        p.StateDirty = true;
        SendState(p);
    }

    /// <summary>
    /// After the player's game changed: if the Pokémon following them changed (chosen, put away, fainted, healed, left
    /// the party), everyone who can see them is told.
    /// </summary>
    void RefreshBuddy(WorldPlayer p)
    {
        lock (_gate)
        {
            var buddy = p.State?.ActiveBuddy;
            var (species, shiny, female) = buddy is null ? (0, false, false) : (buddy.Species.Id, buddy.Shiny, buddy.Gender == Core.Battle.Gender.Female);
            if (species == p.BuddySpecies && shiny == p.BuddyShiny && female == p.BuddyFemale)
                return;
            (p.BuddySpecies, p.BuddyShiny, p.BuddyFemale) = (species, shiny, female);
            var change = new BuddyChanged(p.Id, species, shiny, female);
            foreach (var viewer in _players.Values.Where(v => v.Seen.Contains(p.Id)))
                viewer.Link.Send(change);
        }
    }

    /// <summary>Sends the player their game state (after joining and after changes).</summary>
    public void SendState(WorldPlayer p)
    {
        RefreshBuddy(p);
        if (p.State is { } state && stateJson is not null)
            p.Link.Send(new StateSnapshot(stateJson(state)));
    }

    // ---- What admin commands do (AdminService checks who may) ------------------------------------------------------

    public AdminStatus StatusOf(WorldPlayer p) =>
        new(RoleOf(p).Name, p.Elevated, p.Elevated && p.Invisible, p.Elevated && p.Noclip, RoleOf(p).All);

    /// <summary>Tells everyone online whose role changed (or whose role's permissions changed) where they stand now.</summary>
    public void RefreshStaff()
    {
        lock (_gate)
            foreach (var p in _players.Values)
            {
                var role = RoleOf(p);
                if (role.Beacon != p.Beacon)
                {
                    p.Beacon = role.Beacon;
                    var change = new BeaconChanged(p.Id, p.Beacon);
                    p.Link.Send(change);
                    foreach (var viewer in _players.Values.Where(v => v.Seen.Contains(p.Id)))
                        viewer.Link.Send(change);
                }
                if (p.Elevated && !role.IsStaff)
                {
                    p.Elevated = p.Invisible = p.Noclip = false;
                    StopSpectatingLocked(p);
                }
                if (role.IsStaff || p.Elevated)
                    p.Link.Send(StatusOf(p));
            }
    }

    /// <summary>Replaces an online player's whole game (a revert), writes it and sends it to them.</summary>
    public string? ReplaceState(WorldPlayer p, GameState state)
    {
        lock (_gate)
        {
            if (!_players.ContainsKey(p.Id))
                return $"{p.Name} just left; try again.";
            p.State = state;
            p.StateDirty = true;
            SaveNowLocked(p);
            SendState(p);
            return null;
        }
    }

    /// <summary>An online player's game as JSON now (null when the server keeps none).</summary>
    public string? StateJsonOf(WorldPlayer p)
    {
        lock (_gate)
            return p.State is { } s && stateJson is not null ? stateJson(s) : null;
    }

    /// <summary>The online player playing character <paramref name="characterId"/>, or null.</summary>
    public WorldPlayer? OnlineCharacter(long characterId)
    {
        lock (_gate)
            return _players.Values.FirstOrDefault(p => p.CharacterId == characterId);
    }

    /// <summary>Sets whose whispers a player doesn't receive.</summary>
    public void SetBlocked(WorldPlayer p, IEnumerable<long> blocked)
    {
        lock (_gate)
            p.Blocked = [.. blocked];
    }

    /// <summary>The online player playing the character named <paramref name="name"/> (exactly), or null.</summary>
    public WorldPlayer? Playing(string name)
    {
        lock (_gate)
            return _players.Values.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Changes an online player's role (after the account was changed) and tells them.</summary>
    public void SetRole(WorldPlayer p, string role)
    {
        lock (_gate)
            p.RoleName = role;
        RefreshStaff();
        if (!roles.Get(role).IsStaff)
            p.Link.Send(StatusOf(p));
    }

    /// <summary>
    /// Runs <paramref name="edit"/> on an online player's game state under the world's lock, writes it at once (an admin
    /// edit is important) and sends them the new state. Returns the state's JSON before and after, or the edit's error.
    /// </summary>
    public (string? Before, string? After, string? Error) EditState(WorldPlayer p, Func<GameState, string?> edit)
    {
        lock (_gate)
        {
            if (p.State is not { } state || stateJson is null)
                return (null, null, "This server keeps no game state (no battle data).");
            if (!_players.ContainsKey(p.Id))
                return (null, null, $"{p.Name} just left; try again.");
            if (p.Battle is not null)
                return (null, null, $"{p.Name} is in a battle; try again when it's over.");
            var before = stateJson(state);
            if (edit(state) is { } error)
                return (null, null, error);
            p.StateDirty = true;
            SaveNowLocked(p);
            SendState(p);
            return (before, stateJson(state), null);
        }
    }

    /// <summary>Turns Admin Mode on or off for <paramref name="p"/> (off also ends invisibility and noclip).</summary>
    public void SetElevated(WorldPlayer p, bool on)
    {
        lock (_gate)
        {
            p.Elevated = on;
            p.ElevatedUntil = DateTime.UtcNow.AddMinutes(config.ElevationMinutes);
            if (!on)
            {
                p.Invisible = p.Noclip = false;
                StopSpectatingLocked(p);
            }
            p.Moved = true;
            p.Link.Send(StatusOf(p));
        }
    }

    /// <summary>Keeps Admin Mode going (each admin action counts as activity).</summary>
    public void Touch(WorldPlayer p)
    {
        lock (_gate)
            p.ElevatedUntil = DateTime.UtcNow.AddMinutes(config.ElevationMinutes);
    }

    public void SetToggles(WorldPlayer p, bool? invisible, bool? noclip)
    {
        lock (_gate)
        {
            if (invisible is { } i)
                p.Invisible = i;
            if (noclip is { } n)
                p.Noclip = n;
            p.Link.Send(StatusOf(p));
        }
    }

    /// <summary>Moves a player to a tile of a zone (teleport, summon); false when the place doesn't exist.</summary>
    public bool Teleport(WorldPlayer p, int zone, int x, int y)
    {
        lock (_gate)
        {
            if (!_players.ContainsKey(p.Id) || world.MapOf(zone) is not { } map)
                return false;
            MoveLocked(p, zone, x, y, 0, map);
            p.Link.Send(new Warped(0, p.Location));
            return true;
        }
    }

    /// <summary>The hand-made routes a joining player is sent.</summary>
    public RouteSet Routes() => new(world.RouteFiles());

    /// <summary>
    /// Saves a route from the route editor and tells everyone online (their clients draw it at once). Returns its
    /// number, or why it wasn't saved.
    /// </summary>
    public (int Node, string? Error) SaveRoute(string json)
    {
        lock (_gate)
        {
            var (node, stored, error) = world.SaveRoute(json);
            if (stored is null)
                return (-1, error);
            foreach (var p in _players.Values)
                p.Link.Send(new RouteChanged(node, stored));
            return (node, null);
        }
    }

    /// <summary>Deletes a hand-made route and tells everyone online; false if there was none.</summary>
    public bool DeleteRoute(int node)
    {
        lock (_gate)
        {
            if (!world.DeleteRoute(node))
                return false;
            foreach (var p in _players.Values)
                p.Link.Send(new RouteChanged(node, ""));
            return true;
        }
    }

    /// <summary>Reads hand-made Infinite Routes again (after the route editor saved changes).</summary>
    public void ReloadRoutes()
    {
        lock (_gate)
            world.ForgetRoutes();
    }

    /// <summary>Sends <paramref name="p"/> to the entrance of Infinite Route <paramref name="node"/>; false if it can't be built.</summary>
    public bool TeleportToRoute(WorldPlayer p, int node) =>
        node >= 0 && world.RouteEntrance(Extractor.Extraction.UnchartedRoutes.ZoneOf(node)) is var (x, y)
        && Teleport(p, Extractor.Extraction.UnchartedRoutes.ZoneOf(node), x, y);

    /// <summary>Sends <paramref name="p"/> next to <paramref name="target"/> (or onto them when nothing's free), on their channel.</summary>
    public bool TeleportTo(WorldPlayer p, WorldPlayer target)
    {
        int zone, x, y;
        lock (_gate)
        {
            if (!_players.ContainsKey(target.Id))
                return false;
            p.Channel = target.Channel;
            (zone, x, y) = (target.Zone, target.X, target.Y + 1);
            if (world.Blocked(zone, x, y))
                (x, y) = (target.X, target.Y);
        }
        return Teleport(p, zone, x, y);
    }

    /// <summary>Mutes (or with null, unmutes) a player's chat; the caller stores it on the account too.</summary>
    public void Mute(WorldPlayer p, DateTime? until, string? reason)
    {
        lock (_gate)
        {
            (p.MutedUntil, p.MuteReason) = (until, reason);
            p.Link.Send(new ChatLine(ChatKind.System, "", until is null
                ? "You can chat again."
                : $"A staff member muted you until {until.Value.ToLocalTime():HH:mm}{(reason is { Length: > 0 } ? $": {reason}" : "")}."));
        }
    }

    /// <summary>
    /// Starts watching <paramref name="target"/> (null stops): the staff member turns invisible, follows them from map to
    /// map, and their client's camera follows the target.
    /// </summary>
    public bool Spectate(WorldPlayer p, WorldPlayer? target)
    {
        lock (_gate)
        {
            if (target is null)
            {
                StopSpectatingLocked(p);
                return true;
            }
            if (target == p || !_players.ContainsKey(target.Id))
                return false;
            p.Spectating = target;
            p.Invisible = true;
            p.Channel = target.Channel;
            FollowLocked(p, target);
            p.Link.Send(StatusOf(p));
            p.Link.Send(new SpectateStatus(target.Id, target.Name));
            return true;
        }
    }

    void StopSpectatingLocked(WorldPlayer p)
    {
        if (p.Spectating is null)
            return;
        p.Spectating = null;
        p.Link.Send(new SpectateStatus(0, ""));
    }

    /// <summary>Keeps a spectator on the target's channel, map and tile (a map change sends them there like a warp).</summary>
    void FollowLocked(WorldPlayer p, WorldPlayer target)
    {
        p.Channel = target.Channel;
        if (p.Map != target.Map || p.Zone != target.Zone && !world.IsShared(target.Map))
        {
            MoveLocked(p, target.Zone, target.X, target.Y, target.Facing, target.Map);
            p.Link.Send(new Warped(0, p.Location));
        }
        else
            (p.Zone, p.X, p.Y) = (target.Zone, target.X, target.Y);
    }

    /// <summary>Everyone online as the admin panel shows them.</summary>
    public IReadOnlyList<AdminPlayerRow> AdminRows()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            return _players.Values.OrderBy(p => p.Name).Select(p => new AdminPlayerRow(p.Id, p.Name, p.AccountName, RoleOf(p).Name, p.Channel,
                p.Location, p.Elevated, p.Elevated && p.Invisible, p.MutedUntil is { } m && m > now ? m.ToLocalTime().ToString("HH:mm") : null)).ToList();
        }
    }

    public void Kick(WorldPlayer p, string reason)
    {
        Leave(p);
        p.Link.Close(reason);
    }

    /// <summary>An announcement to everyone online: a banner and a chat line.</summary>
    public void Announce(string from, string text)
    {
        lock (_gate)
            foreach (var p in _players.Values)
            {
                p.Link.Send(new ChatLine(ChatKind.System, from, text));
                p.Link.Send(new Notice(text, p.Channel));
            }
    }

    // ---- The update ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// One update (call 10 times a second): who each player sees now, what moved, Admin Mode timeouts, idle
    /// disconnects, and the writes that are due. Returns a task for those writes.
    /// </summary>
    public Task Tick()
    {
        List<PlayerSave> writes;
        List<WorldPlayer> idle;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            var players = _players.Values.ToList();
            foreach (var p in players.Where(p => p.Elevated && now > p.ElevatedUntil))
            {
                p.Elevated = p.Invisible = p.Noclip = false;
                p.Moved = true;
                p.Link.Send(StatusOf(p));
                p.Link.Send(new Notice($"Admin Mode ended after {config.ElevationMinutes} minutes without an admin action.", p.Channel));
            }
            foreach (var p in players.Where(p => p.Spectating is not null))
            {
                if (_players.ContainsKey(p.Spectating!.Id))
                    FollowLocked(p, p.Spectating);
                else
                {
                    p.Link.Send(new Notice($"{p.Spectating.Name} left; stopped spectating.", p.Channel));
                    StopSpectatingLocked(p);
                }
            }
            foreach (var viewer in players)
            {
                var appeared = new List<NetPlayer>();
                var moved = new List<NetMove>();
                var left = new List<int>();
                foreach (var other in players)
                {
                    var sees = Sees(viewer, other);
                    var saw = viewer.Seen.Contains(other.Id);
                    if (sees && !saw)
                    {
                        viewer.Seen.Add(other.Id);
                        appeared.Add(other.AsNet);
                    }
                    else if (!sees && saw)
                    {
                        viewer.Seen.Remove(other.Id);
                        left.Add(other.Id);
                    }
                    else if (sees && other.Moved)
                        moved.Add(new NetMove(other.Id, other.X, other.Y, other.Facing, other.Ran));
                }
                if (left.Count > 0)
                    viewer.Link.Send(new PlayersLeft(left));
                if (appeared.Count > 0)
                    viewer.Link.Send(new PlayersAppeared(appeared));
                if (moved.Count > 0)
                    viewer.Link.Send(new PlayersMoved(moved));
            }
            foreach (var p in players)
                p.Moved = false;

            idle = players.Where(p => (now - p.LastInput).TotalMinutes >= config.IdleMinutes).ToList();
            // Each player's batch timer: changes are written once it runs out (immediate writes restart it).
            foreach (var p in players.Where(p => (p.Dirty || p.StateDirty) && (now - p.LastSaved).TotalSeconds >= config.SaveIntervalSeconds))
                SaveNowLocked(p);
            writes = [.. _urgent];
            _urgent.Clear();
        }
        foreach (var p in idle)
        {
            Leave(p);
            p.Link.Close($"Disconnected after {config.IdleMinutes} minutes without activity.");
        }
        return writes.Count > 0 ? save(writes) : Task.CompletedTask;
    }

    /// <summary>Writes every player now (server shutdown).</summary>
    public Task SaveAll()
    {
        List<PlayerSave> writes;
        lock (_gate)
        {
            foreach (var p in _players.Values)
                SaveNowLocked(p);
            writes = [.. _urgent];
            _urgent.Clear();
        }
        return save(writes);
    }

    /// <summary>A notice to everyone online (shutdown warnings).</summary>
    public void Broadcast(string text)
    {
        lock (_gate)
            foreach (var p in _players.Values)
                p.Link.Send(new Notice(text, p.Channel));
    }
}
