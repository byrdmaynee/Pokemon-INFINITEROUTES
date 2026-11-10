using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Core.Battle;

/// <summary>
/// Trainer AI, driven by the trainer's ROM AI flags: bit 0 avoids moves that would fail or do nothing, bit 1 favours the
/// strongest attack and a knockout, bit 2 plays like an expert (status and setup moves while it's safe, healing when
/// low, finishing off). Trainers also use the medicine they carry (their four item slots) when their Pokémon is in
/// trouble. Wild Pokémon still pick moves at random.
/// </summary>
public sealed partial class SingleBattle
{
    public const int AiBasic = 1, AiEvaluate = 2, AiExpert = 4;

    List<int>? _trainerItems;

    /// <summary>The foe's move slot for this turn.</summary>
    int FoeSlot()
    {
        var moves = Foe.Mon.Moves;
        if (Trainer is not { } trainer || (trainer.Ai & (AiBasic | AiEvaluate | AiExpert)) == 0 || moves.Count == 0)
            return _rng.Next(Math.Max(1, moves.Count));
        var scores = new int[moves.Count];
        for (var i = 0; i < moves.Count; i++)
            scores[i] = moves[i].Pp <= 0 || Restricted(Foe, moves[i].Move, []) ? int.MinValue : Score(moves[i].Move, trainer.Ai);
        var best = scores.Max();
        if (best == int.MinValue)
            return 0;
        // Pick among the best-scoring moves (within a small margin, so it isn't fully predictable).
        var margin = (trainer.Ai & (AiEvaluate | AiExpert)) != 0 ? 10 : 1000;
        var candidates = Enumerable.Range(0, moves.Count).Where(i => scores[i] != int.MinValue && scores[i] >= best - margin && scores[i] > -50).ToList();
        if (candidates.Count == 0)
            candidates = Enumerable.Range(0, moves.Count).Where(i => scores[i] == best).ToList();
        return candidates[_rng.Next(candidates.Count)];
    }

    /// <summary>How much the trainer's Pokémon wants to use <paramref name="move"/> now.</summary>
    int Score(MoveInfo move, int ai)
    {
        var user = Foe;
        var target = Player;
        var expert = (ai & AiExpert) != 0;
        var evaluate = (ai & AiEvaluate) != 0;
        var type = MoveType(user, move);
        var damaging = move.Category != MoveCategory.Status && (move.Power > 0 || FixedDamage(user, target, move) is not null);
        var score = 100;

        if (damaging)
        {
            var effectiveness = Effectiveness(user, target, type);
            if (effectiveness == 0 || Absorbed(target, type))
                return -1000;   // no effect at all
            if (!evaluate)
                return score;
            var estimate = EstimateDamage(user, target, move, type, effectiveness);
            var percent = estimate * 100 / Math.Max(1, target.Mon.MaxHp);
            score = Math.Min(percent, 100) + 50;
            if (estimate >= target.Mon.Hp)
                score += 100 + move.Priority * 20 + (move.Accuracy > 100 ? 10 : move.Accuracy / 10);   // a sure knockout first
            if (Moves.Recharges(move.Id) && estimate < target.Mon.Hp)
                score -= 40;
            if (move.Recoil < 0 && user.Mon.Hp * 3 < user.Mon.MaxHp)
                score -= 30;
            if (move.Id is Moves.Explosion or Moves.SelfDestruct or Moves.FinalGambit && user.Mon.Hp * 3 > user.Mon.MaxHp)
                score -= 80;
            return score;
        }

        // Status moves: drop the ones that would fail (basic), weigh the rest (expert).
        if (move.Ailment is > Ailment.None and <= Ailment.Poison && move.Kind is MoveKind.Ailment)
        {
            if (target.Mon.Status != Ailment.None || !CanInflict(target, move.Ailment, user) || target.Substitute > 0
                || (move.Ailment == Ailment.Paralysis && move.Type == TypeChart.Electric && target.Types.Contains(TypeChart.Ground)))
                return -1000;
            return expert ? (move.Ailment is Ailment.Sleep or Ailment.Paralysis ? 130 : 110) : score;
        }
        if (move.Ailment == Ailment.Confusion && move.Kind != MoveKind.Swagger && target.ConfusionTurns > 0)
            return -1000;
        if (move.Ailment == Ailment.LeechSeed && (target.Seeded || target.Types.Contains(TypeChart.Grass)))
            return -1000;
        if (WeatherMove(move) is var weather and not Weather.None && Weather == weather)
            return -1000;
        if (move.Kind == MoveKind.Heal || move.Id == Moves.Rest)
        {
            if (user.Mon.Hp == user.Mon.MaxHp)
                return -1000;
            return expert && user.Mon.Hp * 2 < user.Mon.MaxHp ? 160 : 60;
        }
        if (move.StatChanges.Count > 0 && move.Kind == MoveKind.StatChange)
        {
            var raisesSelf = move.StatChanges.All(c => c.Stages > 0);
            var who = raisesSelf ? user : target;
            var useful = move.StatChanges.Any(c => c.Stat == BattleStat.All ||
                (raisesSelf ? who.Stage(c.Stat) < 6 : who.Stage(c.Stat) > -6));
            if (!useful)
                return -1000;
            if (!expert)
                return score;
            // Set up while healthy and early; drop stats while the foe is still strong.
            var healthy = user.Mon.Hp * 3 >= user.Mon.MaxHp * 2;
            return raisesSelf ? (healthy && user.TurnsOut < 3 ? 115 : 40) : (target.Mon.Hp * 2 > target.Mon.MaxHp ? 90 : 50);
        }
        if (move.Id is Moves.Protect or Moves.Detect && user.ProtectChain > 0)
            return -100;
        if (move.Id == Moves.Substitute && (user.Substitute > 0 || user.Mon.Hp <= user.Mon.MaxHp / 4))
            return -1000;
        if (move.Id is Moves.Reflect or Moves.LightScreen && (move.Id == Moves.Reflect ? SideOf(user).Reflect : SideOf(user).LightScreen) > 0)
            return -1000;
        return expert ? 70 : score;
    }

    /// <summary>The target's ability would swallow a move of <paramref name="type"/> (Levitate, Volt Absorb, Flash Fire…).</summary>
    static bool Absorbed(Battler target, int type) => (type, target.Ability) switch
    {
        (TypeChart.Ground, Abilities.Levitate) or (TypeChart.Electric, Abilities.VoltAbsorb or Abilities.MotorDrive or Abilities.LightningRod)
            or (TypeChart.Water, Abilities.WaterAbsorb or Abilities.StormDrain or Abilities.DrySkin) or (TypeChart.Fire, Abilities.FlashFire)
            or (TypeChart.Grass, Abilities.SapSipper) => true,
        (TypeChart.Ground, _) => ItemOf(target) == HeldItems.AirBalloon || target.MagnetRise > 0,
        _ => false,
    };

    /// <summary>An average-roll damage estimate (no randomness, no side effects) for the AI.</summary>
    int EstimateDamage(Battler user, Battler target, MoveInfo move, int type, double effectiveness)
    {
        if (FixedDamage(user, target, move) is { } fixedDamage)
            return Math.Max(0, fixedDamage);
        var physical = move.Category == MoveCategory.Physical;
        var attack = ItemAttack(user, physical, Staged(user.Mon.StatValue(physical ? Stat.Attack : Stat.SpAttack),
            user.Stage(physical ? BattleStat.Attack : BattleStat.SpAttack)));
        var defense = Math.Max(1, ItemDefense(target, physical, Staged(target.Mon.StatValue(physical ? Stat.Defense : Stat.SpDefense),
            target.Stage(physical ? BattleStat.Defense : BattleStat.SpDefense))));
        var power = ItemPower(user, move, type, WeatherPower(move, MovePower(user, target, move, movedFirst: true)));
        var damage = BaseDamage(user.Mon.Level, Math.Max(1, power), Math.Max(1, attack), defense) * 92 / 100;
        damage = (int)(damage * WeatherDamage(type));
        if (user.Types.Contains(type))
            damage = Has(user, Abilities.Adaptability) ? damage * 2 : damage * 3 / 2;
        damage = (int)(damage * effectiveness);
        if (physical && user.Mon.Status == Ailment.Burn && !Has(user, Abilities.Guts))
            damage /= 2;
        return Math.Max(1, damage * HitCount(user, move));
    }

    /// <summary>
    /// A trainer uses one of its medicines on its Pokémon instead of attacking: a healing item at a quarter of its HP or
    /// less, a status cure when it has a status. True when it did (its turn is used up).
    /// </summary>
    bool TrainerUsesItem(List<BattleEvent> events)
    {
        if (Trainer is not { } trainer || Foe.Mon.Fainted)
            return false;
        _trainerItems ??= trainer.Items.Where(i => i != 0).ToList();
        var mon = Foe.Mon;
        foreach (var item in _trainerItems)
        {
            var effect = ItemEffect.For(item);
            var wanted = effect switch
            {
                ItemEffect.Heal => mon.Hp * 4 <= mon.MaxHp,
                ItemEffect.Cure cure => mon.Status != Ailment.None && (cure.Status == Ailment.None || cure.Status == mon.Status),
                _ => false,
            };
            if (!wanted || ItemUse.Apply(effect!, mon) is not { } message)
                continue;
            _trainerItems.Remove(item);
            events.Add(new MessageEvent($"{trainer.Title} used a {_data.ItemName(item)}!"));
            events.Add(new HpEvent(BattleSide.Foe, mon.Hp, mon.MaxHp));
            events.Add(new StatusEvent(BattleSide.Foe, mon.Status));
            events.Add(new MessageEvent(message));
            Foe.Toxic = 0;
            return true;
        }
        return false;
    }
}
