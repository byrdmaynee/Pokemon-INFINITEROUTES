using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Core.Battle;

/// <summary>
/// Double and triple battles (two or three Pokémon a side) and rotation battles. In a double or triple battle each of
/// the player's Pokémon gets a <see cref="BattleChoice"/>; switches go first, then items, then every move by priority
/// and speed. Moves aim at one Pokémon (the ROM's move target 0, picked by the player; a fainted target's neighbour is
/// hit instead) or spread (4: everyone adjacent, 5: the adjacent foes, each for ×0.75); in a triple battle the Pokémon
/// at the two ends can't reach each other. Fainted Pokémon are replaced at the end of the turn while the bench lasts.
/// A rotation battle is fought one at a time, but once a turn the player may rotate to another of their first three
/// Pokémon for free.
/// </summary>
public sealed partial class SingleBattle
{
    List<Pokemon>? _rotation;
    int _rotatedOnTurn = -1;

    /// <summary>In a rotation battle, the three Pokémon the player rotates between.</summary>
    public IReadOnlyList<Pokemon> RotationTeam => _rotation ?? [];

    /// <summary>Whether the player can still rotate this turn (rotation battles only).</summary>
    public bool CanRotate => Format == BattleFormat.Rotation && !Waiting && _rotatedOnTurn != _turns && !Locked(Player);

    /// <summary>Rotates party member <paramref name="partyIndex"/> (one of <see cref="RotationTeam"/>) to the front, free of the turn.</summary>
    public IReadOnlyList<BattleEvent> Rotate(int partyIndex)
    {
        if (!CanRotate || !CanSendOut(partyIndex) || partyIndex == ActiveIndex || !_rotation!.Contains(_party[partyIndex]))
            return [];
        _rotatedOnTurn = _turns;
        var events = new List<BattleEvent> { new MessageEvent($"{Player.Mon.Name} rotated out!") };
        OnSwitchOut(Player);
        Enter(partyIndex, events);
        return events;
    }

    /// <summary>The Pokémon a move would hit, given the player's pick of side and slot for single-target moves.</summary>
    public List<Battler> Targets(Battler user, MoveInfo move, BattleSide side, int slot)
    {
        var foes = FoesOf(user);
        var allies = _field[(int)user.Side].Where(a => a != user && !a.Mon.Fainted && Adjacent(user, a)).ToList();
        switch (move.Target)
        {
            case 4:
                return [.. foes, .. allies];
            case 5:
                return foes;
            case 9:
                return foes.Count == 0 ? [] : [foes[_rng.Next(foes.Count)]];
            case 2:
                return allies.Count == 0 ? [] : [allies[0]];
            case 0 or 3 or 13:
            {
                var row = _field[(int)side];
                var chosen = slot >= 0 && slot < row.Length ? row[slot] : null;
                if (chosen is not null && !chosen.Mon.Fainted && chosen != user && Adjacent(user, chosen))
                    return [chosen];
                // The pick fainted (or is out of reach): the nearest foe takes it instead.
                if (chosen is null || chosen.Side != user.Side)
                    return foes.Count == 0 ? [] : [foes.OrderBy(f => Math.Abs(f.Slot - slot)).First()];
                return [];
            }
            default:
                // The user, its side or the whole field: the existing effects take the foe it faces as the target.
                return [Opponent(user)];
        }
    }

    /// <summary>Whether a move needs the player to pick its target (with more than one Pokémon it could hit).</summary>
    public bool NeedsTarget(Battler user, MoveInfo move) =>
        Width > 1 && move.Target is 0 or 3 && AllField.Count(b => b != user && !b.Mon.Fainted && Adjacent(user, b)) > 1;

    /// <summary>
    /// Plays one turn of a double or triple battle with a choice for each of the player's slots (null for an empty or
    /// fainted one; a move-locked Pokémon's choice is ignored). In a single or rotation battle the first choice is used.
    /// Returns a single message and plays nothing when a choice isn't allowed.
    /// </summary>
    public IReadOnlyList<BattleEvent> Turn(IReadOnlyList<BattleChoice?> choices)
    {
        if (Width == 1)
            return choices.FirstOrDefault() switch
            {
                BattleChoice.Switch s => Switch(s.PartyIndex),
                BattleChoice.Item i => UseItem(i.ItemId, i.PartyIndex),
                BattleChoice.Run => Run(),
                BattleChoice.Fight f => Fight(f.Slot),
                _ => Fight(0),
            };
        if (Waiting)
            return [];

        var events = new List<BattleEvent>();
        var plans = new List<(Battler User, MoveInfo Move, int Slot, BattleSide Side, int TargetSlot)>();
        var switches = new List<(Battler User, int Index)>();
        var items = new List<BattleChoice.Item>();
        var run = false;
        for (var i = 0; i < Width; i++)
        {
            var b = _field[0][i];
            if (b.Mon.Fainted)
                continue;
            var choice = i < choices.Count ? choices[i] : null;
            if (Forced(b) is { } forced)
            {
                plans.Add((b, forced.Move, forced.Slot, BattleSide.Foe, Opponent(b).Slot));
                continue;
            }
            switch (choice)
            {
                case BattleChoice.Switch s:
                    if (!CanSendOut(s.PartyIndex) || IsOut(s.PartyIndex) || switches.Any(x => x.Index == s.PartyIndex))
                        return [new MessageEvent("That Pokémon can't be sent out!")];
                    if (Trapped(b) is { } trap)
                        return [Popup(Opponent(b)), new MessageEvent(trap)];
                    switches.Add((b, s.PartyIndex));
                    break;
                case BattleChoice.Item item:
                    if (ItemEffect.For(item.ItemId) is ItemEffect.Ball && Trainer is null && _field[1].Count(f => !f.Mon.Fainted) > 1)
                        return [new MessageEvent("There are two Pokémon out! You can't aim a Poké Ball!")];
                    if (_bag.Count(item.ItemId) <= items.Count(x => x.ItemId == item.ItemId))
                        return [new MessageEvent("You don't have enough of that item!")];
                    items.Add(item);
                    break;
                case BattleChoice.Run:
                    run = true;
                    break;
                default:
                {
                    var fight = choice as BattleChoice.Fight;
                    var slot = fight?.Slot ?? 0;
                    var moves = b.Mon.Moves;
                    if (ChoiceLock(b) is { } locked && !moves.All(m => m.Pp <= 0) && slot >= 0 && slot < moves.Count && moves[slot].Move.Id != locked)
                        return [new MessageEvent($"{b.Mon.Name} can only use {_data.Move(locked)?.Name}!")];
                    if (Choose(b, slot) is not { } picked)
                        return [new MessageEvent("There's no PP left for this move!")];
                    if (Restricted(b, picked.Move, events))
                        return events;
                    plans.Add((b, picked.Move, picked.Slot, fight?.TargetSide ?? BattleSide.Foe, fight?.TargetSlot ?? Opponent(b).Slot));
                    break;
                }
            }
        }

        if (run)
        {
            if (Trainer is not null)
                return [new MessageEvent("No! There's no running from a Trainer battle!")];
            _escapeAttempts++;
            var runner = _field[0].First(b => !b.Mon.Fainted);
            int mine = EffectiveSpeed(runner), theirs = Math.Max(1, _field[1].Where(f => !f.Mon.Fainted).Max(EffectiveSpeed));
            if (Has(runner, Abilities.RunAway) || ItemOf(runner) == HeldItems.SmokeBall || mine >= theirs
                || _rng.Next(256) < (mine * 128 / theirs + 30 * _escapeAttempts) % 256)
            {
                Outcome = BattleOutcome.Escaped;
                return [new MessageEvent("Got away safely!")];
            }
            events.Add(new MessageEvent("Can't escape!"));
        }
        _turns++;

        // The foe decides: medicine, or a move at whichever of the player's Pokémon suits it best.
        foreach (var foe in _field[1])
        {
            if (foe.Mon.Fainted)
                continue;
            if (Forced(foe) is { } forced)
            {
                plans.Add((foe, forced.Move, forced.Slot, BattleSide.Player, Opponent(foe).Slot));
                continue;
            }
            if (TrainerUsesItem(foe, events))
                continue;
            var reachable = FoesOf(foe);
            if (reachable.Count == 0)
                continue;
            var target = reachable[_rng.Next(reachable.Count)];
            var slot = FoeSlot(foe, target);
            if (Trainer is { Ai: not 0 })
            {
                var best = reachable.Select(t => (Target: t, Choice: FoeChoice(foe, t))).MaxBy(x => x.Choice.Score);
                (target, slot) = (best.Target, best.Choice.Slot);
            }
            if (Choose(foe, slot) is { } picked)
                plans.Add((foe, picked.Move, picked.Slot, BattleSide.Player, target.Slot));
        }
        foreach (var plan in plans)
            plan.User.ChosenMove = plan.Move.Id;

        // Switches first, fastest first; then the Bag.
        foreach (var (user, index) in switches.OrderByDescending(s => EffectiveSpeed(s.User)))
        {
            events.Add(new MessageEvent($"{user.Mon.Name}, come back!"));
            OnSwitchOut(user);
            Enter(index, events, user.Slot);
        }
        foreach (var item in items)
            if (!Over)
                UseItemInTurn(item, events);

        // Then the moves, by priority, Stall, Quick Claw / Lagging Tail and speed.
        var order = plans
            .Select(p => (Plan: p, Priority: Priority(p.User, p.Move), Stall: Has(p.User, Abilities.Stall) ? 0 : 1,
                Item: ItemOrder(p.User), Speed: EffectiveSpeed(p.User) * (_trickRoom > 0 ? -1 : 1), Tie: _rng.Next()))
            .OrderByDescending(x => x.Priority).ThenByDescending(x => x.Stall).ThenByDescending(x => x.Item)
            .ThenByDescending(x => x.Speed).ThenBy(x => x.Tie)
            .Select(x => x.Plan).ToList();
        foreach (var plan in order)
        {
            if (Over)
                break;
            var user = plan.User;
            if (user.Mon.Fainted || _field[(int)user.Side][user.Slot] != user)
                continue;
            var targets = Targets(user, plan.Move, plan.Side, plan.TargetSlot);
            if (targets.Count == 0)
            {
                events.Add(new MessageEvent($"{Label(user)} used {plan.Move.Name}!"));
                events.Add(new MessageEvent("But there was no target..."));
                continue;
            }
            _targets = targets;
            UseMove(user, targets[0], plan.Move, plan.Slot, movedFirst: !targets[0].Moved, events);
            _targets = null;
            user.Moved = true;
            CheckFaints(events);
        }
        if (!Over)
            EndOfTurn(events);
        return events;
    }

    /// <summary>A Bag item used as one of the player's actions in a double or triple battle.</summary>
    void UseItemInTurn(BattleChoice.Item choice, List<BattleEvent> events)
    {
        if (_bag.Count(choice.ItemId) <= 0 || ItemEffect.For(choice.ItemId) is not { } effect)
            return;
        var name = _data.ItemName(choice.ItemId);
        if (effect is ItemEffect.Ball ball)
        {
            _bag.Take(choice.ItemId);
            events.Add(new MessageEvent($"You threw a {name}!"));
            if (Trainer is not null)
            {
                events.Add(new MessageEvent("The Trainer blocked the Ball! Don't be a thief!"));
                return;
            }
            var wild = _field[1].First(f => !f.Mon.Fainted);
            var shakes = CatchShakes(ball, choice.ItemId, wild);
            events.Add(new CatchEvent(choice.ItemId, Math.Min(3, shakes), shakes == 4));
            if (shakes == 4)
            {
                events.Add(new MessageEvent($"Gotcha! {wild.Mon.Name} was caught!"));
                Caught = wild.Mon;
                Outcome = BattleOutcome.Caught;
                return;
            }
            events.Add(new MessageEvent(shakes switch
            {
                0 => "Oh no! The Pokémon broke free!",
                1 => "Aww! It appeared to be caught!",
                2 => "Aargh! Almost had it!",
                _ => "Gah! It was so close, too!",
            }));
            return;
        }
        var index = choice.PartyIndex >= 0 && choice.PartyIndex < _party.Count ? choice.PartyIndex : ActiveIndex;
        var mon = _party[index];
        var onField = _field[0].FirstOrDefault(b => ReferenceEquals(b.Mon, mon));
        var wasConfused = onField is { ConfusionTurns: > 0 };
        var message = ItemUse.Apply(effect, mon);
        if (effect is ItemEffect.Cure { Status: Ailment.None } && wasConfused)
        {
            onField!.ConfusionTurns = 0;
            message ??= $"{mon.Name} snapped out of its confusion.";
        }
        if (message is null)
        {
            events.Add(new MessageEvent($"The {name} won't have any effect on {mon.Name}."));
            return;
        }
        _bag.Take(choice.ItemId);
        events.Add(new MessageEvent($"You used the {name}."));
        if (onField is not null)
        {
            events.Add(new HpEvent(BattleSide.Player, mon.Hp, mon.MaxHp, onField.Slot));
            events.Add(new StatusEvent(BattleSide.Player, mon.Status, onField.Slot));
        }
        events.Add(new MessageEvent(message));
    }
}
