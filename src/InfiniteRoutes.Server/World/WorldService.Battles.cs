using InfiniteRoutes.Core.Battle;
using InfiniteRoutes.Core.Data;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;

namespace InfiniteRoutes.Server.World;

/// <summary>A battle a player is in: the server's copy of it, which is the one that counts.</summary>
public sealed class ActiveBattle(int id, SingleBattle battle)
{
    public int Id { get; } = id;
    public SingleBattle Battle { get; } = battle;
    /// <summary>The last action's sequence number (each must be higher).</summary>
    internal int Seq;
    /// <summary>Moves the battle offered to teach (a full moveset): the client answers each with <c>learn</c>.</summary>
    internal readonly List<(int Party, MoveInfo Move)> Learns = [];
    internal bool Named;
}

/// <summary>
/// Battles online (BUILD_SPEC §4.3): the server rolls wild encounters on each step into tall grass and runs the battle.
/// The client builds the same battle from the game the server sends with it and replays each action with the seed the
/// server gives once the action is chosen, so its animations match without it deciding anything; the server's game is
/// the one saved. Encounter starts and ends are written at once (the save plan).
/// </summary>
public sealed partial class WorldService
{
    readonly Random _dice = new();
    int _nextBattle = 1;

    /// <summary>The season wild Pokémon are picked for (spring = 0), as the client shows it.</summary>
    static int Season => (DateTime.Now.Month - 1) % 4;

    /// <summary>The battle a player is in, if any.</summary>
    public ActiveBattle? BattleOf(WorldPlayer p)
    {
        lock (_gate)
            return p.Battle;
    }

    /// <summary>After an accepted step: Repel counts down, then tall grass may hold a wild Pokémon.</summary>
    void AfterStepLocked(WorldPlayer p)
    {
        if (rules is null || p.State is not { } state || p.Battle is not null || p.Spectating is not null)
            return;
        if (state.RepelStep())
        {
            p.StateDirty = true;
            p.Link.Send(new Notice("The Repel's effect wore off."));
        }
        // Staff in Admin Mode walk without being stopped by wild Pokémon.
        if (p.Elevated || !world.TallGrass(p.Zone, p.X, p.Y) || state.Lead is not { } lead)
            return;
        EncounterSlot slot;
        if (world.CustomEncounters(p.Zone) is var (rate, wild))
        {
            // A hand-made route's own wild Pokémon, picked by weight.
            if (_dice.Next(100) >= rate)
                return;
            var roll = _dice.Next(wild.Sum(s => s.Weight));
            var picked = wild.First(s => (roll -= s.Weight) < 0);
            if (!rules.Data.HasSpecies(picked.Species))
                return;
            slot = new EncounterSlot(picked.Species, 0, picked.MinLevel, picked.MaxLevel);
        }
        else
        {
            if (world.EncounterTable(p.Zone) is not (>= 0 and var table) || table >= rules.Encounters.Count)
                return;
            var set = rules.Encounters[table].ForSeason(Season);
            if (set.GrassRate <= 0 || _dice.Next(100) >= set.GrassRate
                || EncounterSet.Pick(set.Grass, EncounterSet.GrassWeights, _dice) is not { } found || !rules.Data.HasSpecies(found.Species))
                return;
            slot = found;
        }
        var level = _dice.Next(slot.MinLevel, slot.MaxLevel + 1);
        // A Repel keeps away wild Pokémon below the lead's level.
        if (state.RepelSteps > 0 && level < lead.Level)
            return;
        StartBattleLocked(p, Pokemon.Create(rules.Data, slot.Species, level, _dice), null);
    }

    /// <summary>Starts a wild battle against <paramref name="wild"/> (tests and staff tools; steps in grass roll their own).</summary>
    public bool StartWildBattle(WorldPlayer p, Pokemon wild)
    {
        lock (_gate)
        {
            if (rules is null || !_players.ContainsKey(p.Id) || p.State?.Lead is null || p.Battle is not null)
                return false;
            StartBattleLocked(p, wild, null);
            return true;
        }
    }

    /// <summary>
    /// Starts a battle against <paramref name="wild"/>, or (when it's null) <paramref name="trainer"/>'s team, which both
    /// sides build from the seed: <c>new Random(Seed)</c> picks the team and then runs the battle's start.
    /// </summary>
    void StartBattleLocked(WorldPlayer p, Pokemon? wild, TrainerInfo? trainer)
    {
        // Both sides start from exactly the same game: the server's state written out and read back is what it fights
        // with, and that text is what the client gets.
        var json = rules!.Json(p.State!);
        var state = rules.Load(json);
        p.State = state;
        var seed = _dice.Next();
        var wildJson = "";
        SingleBattle battle;
        if (wild is not null)
        {
            wildJson = SaveGame.PokemonJson(wild.ToSave());
            var foe = Pokemon.FromSave(rules.Data, SaveGame.PokemonFromJson(wildJson)!)!;
            battle = new SingleBattle(rules.Data, state.Party, state.Bag, foe, new Random(seed));
        }
        else
        {
            var rng = new Random(seed);
            battle = new SingleBattle(rules.Data, state.Party, state.Bag, SingleBattle.TrainerParty(rules.Data, trainer!, rng), trainer!, rng);
        }
        battle.Start();
        p.Battle = new ActiveBattle(_nextBattle++, battle);
        p.StateDirty = true;
        SaveNowLocked(p);
        p.Link.Send(new StateSnapshot(json));
        p.Link.Send(new BattleStart(p.Battle.Id, wildJson, seed, Trainer: trainer?.Id ?? -1));
    }

    void BattleInputLocked(WorldPlayer p, BattleInput input)
    {
        if (p.Battle is not { } active || input.Battle != active.Id)
        {
            if (input.Kind is "turn" or "locked" or "send-out" or "rotate")
                p.Link.Send(new BattleTurn(input.Battle, input.Seq, false, 0, Reason: "That battle is over."));
            return;
        }
        var b = active.Battle;
        switch (input.Kind)
        {
            case "learn":
                Learn(p, active, input);
                return;
            case "nickname":
                if (b.Outcome == BattleOutcome.Caught && !active.Named)
                {
                    active.Named = true;
                    var name = new string(input.Text.Where(c => !char.IsControl(c)).ToArray()).Trim();
                    (b.Caught ?? b.Foe.Mon).Rename(name.Length > Pokemon.MaxNicknameLength ? name[..Pokemon.MaxNicknameLength] : name);
                    p.StateDirty = true;
                }
                return;
            case "end":
                FinishBattleLocked(p);
                return;
        }
        if (input.Seq <= active.Seq)
            return;
        active.Seq = input.Seq;
        if (Plan(b, input) is not { } play)
        {
            p.Link.Send(new BattleTurn(active.Id, input.Seq, false, 0, Reason: "You can't do that now."));
            return;
        }
        var seed = _dice.Next();
        b.Reseed(seed);
        IReadOnlyList<BattleEvent> events;
        try
        {
            events = play();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            // Half a turn may have happened: stop the battle rather than carry on from a state the client can't match.
            p.Link.Send(new BattleTurn(active.Id, input.Seq, false, 0, Reason: "The battle had to stop."));
            FinishBattleLocked(p);
            return;
        }
        foreach (var learn in events.OfType<LearnMoveEvent>())
            active.Learns.Add((learn.PartyIndex, learn.Move));
        p.StateDirty = true;
        p.Link.Send(new BattleTurn(active.Id, input.Seq, true, seed, b.Digest()));
    }

    /// <summary>What an action does, or null when it can't be taken now (the engine checks the finer rules itself).</summary>
    static Func<IReadOnlyList<BattleEvent>>? Plan(SingleBattle b, BattleInput input)
    {
        if (b.Over)
            return null;
        var party = b.Party.Count;
        switch (input.Kind)
        {
            case "turn":
                if (b.NeedsSwitch || b.SlotsToFill.Count > 0 || input.Choices is not { } choices || choices.Count != b.Width)
                    return null;
                var list = new List<BattleChoice?>();
                for (var i = 0; i < b.Width; i++)
                {
                    var user = b.PlayerField[i];
                    if (choices[i] is not { } c)
                    {
                        list.Add(null);
                        continue;
                    }
                    BattleChoice? choice = c.Kind switch
                    {
                        "fight" when c.A >= 0 && c.A < Math.Max(1, user.Mon.Moves.Count) && c.B is 0 or 1 && c.C >= 0 && c.C < b.Width
                            => new BattleChoice.Fight(c.A, (BattleSide)c.B, c.C),
                        "switch" when b.CanSendOut(c.A) && !b.IsOut(c.A) => new BattleChoice.Switch(c.A),
                        "item" when c.A > 0 && b.Bag.Count(c.A) > 0 && c.B >= -1 && c.B < party => new BattleChoice.Item(c.A, c.B),
                        "run" => new BattleChoice.Run(),
                        _ => null,
                    };
                    if (choice is null)
                        return null;
                    list.Add(choice);
                }
                return () => b.Turn(list);
            case "locked":
                return b.PlayerLocked ? () => b.Fight(0) : null;
            case "send-out":
                return (b.NeedsSwitch || b.SlotsToFill.Count > 0) && b.CanSendOut(input.A) && !b.IsOut(input.A) ? () => b.SendOut(input.A) : null;
            case "rotate":
                return b.CanRotate && b.CanSendOut(input.A) && !b.IsOut(input.A) && b.RotationTeam.Contains(b.Party[input.A]) ? () => b.Rotate(input.A) : null;
            default:
                return null;
        }
    }

    /// <summary>The answer to a move the battle offered to teach: forget slot C for it, or (C = -1) don't learn it.</summary>
    static void Learn(WorldPlayer p, ActiveBattle active, BattleInput input)
    {
        var i = active.Learns.FindIndex(l => l.Party == input.A && l.Move.Id == input.B);
        if (i < 0)
            return;
        var (index, move) = active.Learns[i];
        active.Learns.RemoveAt(i);
        if (input.C >= 0 && input.C < 4 && index < active.Battle.Party.Count)
        {
            active.Battle.Party[index].LearnMove(move, input.C);
            p.StateDirty = true;
        }
    }

    /// <summary>
    /// Ends the player's battle (the client finished showing it, they left, or it had to stop): records it in their game
    /// (money, wins, catches), sends them home healed if they lost, saves at once and sends the result.
    /// </summary>
    void FinishBattleLocked(WorldPlayer p)
    {
        if (p.Battle is not { } active || p.State is not { } state)
            return;
        p.Battle = null;
        var b = active.Battle;
        var lines = b.Over ? state.RecordBattle(b, DateTime.UtcNow) : [];
        lines.AddRange(QuestsLocked(p, notify: false));
        if (b.Outcome == BattleOutcome.Lost)
        {
            state.HealParty();
            lines.Add("You hurried home to heal your Pokémon.");
            var home = world.Start;
            if (world.MapOf(home.Zone) is { } map)
            {
                MoveLocked(p, home.Zone, home.X, home.Y, home.Facing, map);
                p.Link.Send(new Warped(0, p.Location));
            }
        }
        p.StateDirty = true;
        SaveNowLocked(p);
        p.Link.Send(new StateSnapshot(rules!.Json(state)));
        p.Link.Send(new BattleOver(active.Id, (int)b.Outcome, lines));
        RefreshBuddy(p);
    }
}
