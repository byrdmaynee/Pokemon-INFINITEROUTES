using Godot;
using InfiniteRoutes.Client.Online;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// Online play (M2): the world as the server says it is. Each step is sent as it's taken (the player moves at once and
/// is snapped back if the server refuses); doors, stairs and mats ask the server first and go where it says; other
/// players on the channel appear, walk and leave as the server's 10 Hz updates arrive, with their names above them.
/// No battles, NPC scripts or menus online yet. Esc leaves the world (back to the character list).
/// </summary>
public partial class OverworldScreen
{
    readonly bool _online;

    /// <summary>This player's own role beacon, over their head.</summary>
    readonly BeaconArt.Emblem MyBeacon = new(2.5f);

    /// <summary>The beacon id over this player and over each player in view; for smoke tests.</summary>
    public string MyBeaconId => MyBeacon.Id;
    public IEnumerable<(string Name, string Beacon)> RemoteBeacons => _remotes.Values.Select(r => (r.PlayerName, r.Beacon.Id));
    readonly Dictionary<int, RemotePlayer> _remotes = [];
    int _seq;
    /// <summary>The seq of a warp sent to the server and not answered yet.</summary>
    int? _pendingWarp;
    NetLocation? _correction;
    int _channel;

    // Godot needs a parameterless constructor to recreate script instances.
    public OverworldScreen() : this(false) { }

    /// <param name="online">Play on the server <see cref="OnlineSession"/> joined, instead of the offline save.</param>
    public OverworldScreen(bool online) => _online = online;

    /// <summary>Other players in view; for smoke tests.</summary>
    public IReadOnlyCollection<string> OtherPlayers => _remotes.Values.Select(r => r.PlayerName).ToList();

    public bool Online => _online;

    string PlayerCharacter => _online && OnlineSession.Character?.Appearance == 1 ? "heroine" : "hero";

    /// <summary>Where the server puts the player on joining.</summary>
    (int Zone, Vector2I Tile, Facing Facing)? OnlineStart() =>
        OnlineSession.Game?.Welcome is { } w ? (w.Location.Zone, new Vector2I(w.Location.X, w.Location.Y), (Facing)Math.Clamp(w.Location.Facing, 0, 3)) : null;

    /// <summary>Tells the server about a step just taken.</summary>
    void SendStep(Vector2I from, Vector2I to, bool run)
    {
        if (!_online || OnlineSession.Game is not { } game)
            return;
        var d = to - from;
        if (Math.Abs(d.X) + Math.Abs(d.Y) == 1)
        {
            game.Send(new Step(++_seq, d.X, d.Y, run));
            _sentFacing = (int)DirToFacing(d);
        }
    }

    int _sentFacing = -1;

    /// <summary>Tells the server the player turned (once per change, not every frame a key is held against a wall).</summary>
    void SendTurn()
    {
        var facing = (int)DirToFacing(_dir);
        if (!_online || OnlineSession.Game is not { } game || facing == _sentFacing)
            return;
        _sentFacing = facing;
        game.Send(new Turn(facing));
    }

    /// <summary>Online, a warp is the server's to take: ask, and go where the answer says.</summary>
    bool AskWarp(WarpInfo warp, Vector2I tile)
    {
        if (!_online || OnlineSession.Game is not { } game)
            return false;
        if (_pendingWarp is null)
        {
            _pendingWarp = ++_seq;
            _moving = true;
            ShowFrame(0);
            game.Send(new Warp(_pendingWarp.Value, tile.X, tile.Y));
        }
        return true;
    }

    /// <summary>Handles what the server sent since the last frame; leaves the world if the connection closed.</summary>
    void PumpOnline(double delta)
    {
        if (!_online)
            return;
        if (OnlineSession.Game is not { } game)
            return;
        while (game.Inbox.TryDequeue(out var message))
            switch (message)
            {
                case StepResult { Accepted: false } refused:
                    if (refused.Seq == _pendingWarp)
                    {
                        _pendingWarp = null;
                        _moving = false;
                    }
                    _correction = refused.Location;
                    break;
                case Warped warped:
                    _pendingWarp = null;
                    ArriveFromServer(warped.Location);
                    break;
                case BeaconChanged beacon:
                    if (_remotes.TryGetValue(beacon.Player, out var wearer))
                        wearer.Beacon.Set(beacon.Beacon);
                    else if (beacon.Player == OnlineSession.Game?.Welcome.PlayerId)
                        MyBeacon.Set(beacon.Beacon);
                    break;
                case BuddyChanged buddy:
                    if (_remotes.TryGetValue(buddy.Player, out var who))
                        who.Buddy.SetPokemon(buddy.Species, buddy.Shiny, buddy.Female);
                    break;
                case PlayersAppeared appeared:
                    foreach (var p in appeared.Players)
                        AddRemote(p);
                    break;
                case PlayersMoved moved:
                    foreach (var m in moved.Moves)
                        if (_remotes.TryGetValue(m.Id, out var remote))
                            remote.MoveTo(new Vector2I(m.X, m.Y), (Facing)Math.Clamp(m.Facing, 0, 3), m.Run, GroundHeight(new Vector2I(m.X, m.Y), remote.Position.Y));
                    break;
                case PlayersLeft left:
                    foreach (var id in left.Ids)
                        if (_remotes.Remove(id, out var gone))
                            gone.QueueFree();
                    break;
                case ChatLine chat:
                    ShowChat(chat);
                    break;
                case AdminReply reply:
                    if (!ShowAdminWindow(reply))
                        ShowAdminReply(reply);
                    break;
                case AdminPlayers players:
                    SetAdminRows(players);
                    break;
                case SpectateStatus spectate:
                    SetSpectating(spectate);
                    break;
                case SocialReply or SocialBadges or DmReceived or MarketPage or TradeList or FriendsList or MailList or DmThreads or DmConversation:
                    ShowSocial(message);
                    break;
                case BattleStart start:
                    QueueOnlineBattle(start);
                    break;
                case BattleTurn turn:
                    _onlineBattle?.Answer(turn);
                    break;
                case BattleOver over:
                    BattleResult(over);
                    break;
                case AdminPlayerDetail or AdminRoles or AdminAudit:
                    ShowAdminData(message);
                    break;
                case AdminStatus status:
                    SetAdminStatus(status);
                    break;
                case StateSnapshot snapshot:
                    SetOnlineState(snapshot.Json);
                    UpdateQuestHud();
                    RefreshQuestMarks();
                    break;
                case NpcResult npcResult:
                    OnNpcResult(npcResult);
                    break;
                case Notice notice:
                    if (notice.Channel > 0)
                    {
                        _channel = notice.Channel;
                        UpdateHud();
                    }
                    Toast(notice.Text);
                    break;
            }
        OpenPendingBattle();
        // A refused step puts the player back where the server has them.
        if (_correction is { } c && !_moving)
        {
            _correction = null;
            var tile = new Vector2I(c.X, c.Y);
            if (c.Zone != _zone && _zones.TryGetValue(c.Zone, out var z) && z.Map != _map.Key)
                ArriveFromServer(c);
            else if (tile != _tile)
            {
                _tile = tile;
                StreamChunks();
                _height = GroundHeight(_tile, _height);
                _player.Position = TileCenter(_tile, _height);
                PlaceBuddy();
                UpdateHud();
            }
        }
        if (game.Closed)
        {
            var reason = game.CloseReason ?? "The connection to the server was lost.";
            OnlineSession.Leave();
            Main.Instance.Toast(reason);
            Main.Instance.Back();
        }
    }

    /// <summary>Goes where the server put the player (after a warp, or a correction onto another map).</summary>
    async void ArriveFromServer(NetLocation at)
    {
        if (!_zones.TryGetValue(at.Zone, out var zone) || _assets.LoadMap(zone.Map) is not { } map)
        {
            _moving = false;
            return;
        }
        _moving = true;
        var fadeOut = CreateTween();
        fadeOut.TweenProperty(_fade, "modulate", Colors.White, FadeSeconds);
        await ToSignal(fadeOut, Tween.SignalName.Finished);
        if (!IsInstanceValid(this))
            return;
        foreach (var remote in _remotes.Values)
            remote.QueueFree();
        _remotes.Clear();   // the server sends who is there
        var newMap = map.Key != _map.Key;
        _zone = zone.Id;
        EnterMap(map);
        _tile = new Vector2I(at.X, at.Y);
        StreamChunks();
        UpdateHud();
        // Doors and stairs: step off them onto the floor (that step goes to the server like any other).
        var dest = zone.Warps.FirstOrDefault(w => w.Contains(at.X, at.Y));
        Arrive(_tile, (Facing)Math.Clamp(at.Facing, 0, 3), autoStep: newMap && (dest?.Kind is WarpKind.Door or WarpKind.Stairs || dest?.Kind == WarpKind.Edge && IsBlocked(_tile)));
        CreateTween().TweenProperty(_fade, "modulate", Colors.Transparent, FadeSeconds).SetDelay(0.05);
    }

    void AddRemote(NetPlayer p)
    {
        if (_remotes.Remove(p.Id, out var old))
            old.QueueFree();
        var sheet = _assets.OverworldSpritePath(p.Appearance == 1 ? "heroine" : "hero");
        var remote = new RemotePlayer(p.Id, p.Name, BuildSprite(sheet), new BuddyFollower(_assets));
        _world.AddChild(remote);
        _world.AddChild(remote.Buddy);
        var tile = new Vector2I(p.Location.X, p.Location.Y);
        remote.Place(tile, (Facing)Math.Clamp(p.Location.Facing, 0, 3), GroundHeight(tile, _height));
        remote.Buddy.SetPokemon(p.Buddy, p.BuddyShiny, p.BuddyFemale);
        remote.Beacon.Set(p.Beacon);
        _remotes[p.Id] = remote;
    }

    /// <summary>Another player: their sprite walking from tile to tile, and their name above.</summary>
    sealed partial class RemotePlayer : Node3D
    {
        readonly Sprite3D _sprite;
        Facing _facing;
        int _steps;
        Tween? _tween;

        public int Id { get; }
        public string PlayerName { get; }
        public Vector2I Tile { get; private set; }

        /// <summary>The Pokémon following them (hidden when there's none).</summary>
        public BuddyFollower Buddy { get; }

        /// <summary>Their role's beacon over their name.</summary>
        public BeaconArt.Emblem Beacon { get; } = new();

        public RemotePlayer() : this(0, "", new Sprite3D(), new BuddyFollower()) { }

        public RemotePlayer(int id, string name, Sprite3D sprite, BuddyFollower buddy)
        {
            Id = id;
            Buddy = buddy;
            PlayerName = name;
            _sprite = sprite;
            AddChild(sprite);
            AddChild(new Label3D
            {
                Text = name, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 40, PixelSize = 0.01f,
                Position = new Vector3(0, 2.4f, 0), OutlineSize = 10, NoDepthTest = true, Modulate = new Color(1, 1, 0.85f),
            });
            AddChild(Beacon);
        }

        public override void _ExitTree()
        {
            if (IsInstanceValid(Buddy))
                Buddy.QueueFree();
        }

        public void Place(Vector2I tile, Facing facing, float height)
        {
            Tile = tile;
            _facing = facing;
            Position = TileCenter(tile, height);
            Show(0, false);
            Buddy.Place(tile - FacingToDir(facing), facing, height);
        }

        void Show(int step, bool run) => _sprite.Frame = (int)_facing * CharacterSheet.Columns + CharacterSheet.Column(run, step);

        /// <summary>Walks to a neighbouring tile (anything farther is a jump).</summary>
        public void MoveTo(Vector2I tile, Facing facing, bool run, float height)
        {
            _facing = facing;
            if (tile == Tile)
            {
                Show(0, false);
                return;
            }
            var distance = Math.Abs(tile.X - Tile.X) + Math.Abs(tile.Y - Tile.Y);
            var from = Tile;
            Tile = tile;
            _tween?.Kill();
            if (distance > 2)
            {
                Position = TileCenter(tile, height);
                Show(0, false);
                Buddy.Place(tile - FacingToDir(facing), facing, height);
                return;
            }
            var seconds = (run ? RunSeconds : WalkSeconds) * distance;
            Buddy.StepTo(from, facing, Position.Y, seconds);
            _tween = CreateTween();
            _tween.TweenProperty(this, "position", TileCenter(tile, height), seconds);
            Show(1 + _steps++ % 2, run);
            _tween.Parallel().TweenCallback(Callable.From(() => Show(0, run))).SetDelay(seconds / 2);
        }
    }
}
