using System.Buffers.Binary;
using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Extractor.Gen5;

/// <summary>Decoders for Black/White move data, level-up learnsets and wild encounter tables.</summary>
public static class Gen5Battle
{
    public const int MoveSize = 36;
    public const int EncounterSetSize = 232;

    /// <summary>
    /// A 36-byte move record: type, effect class, category, power, accuracy, PP, priority, hits (min | max &lt;&lt; 4),
    /// u16 ailment, ailment chance, duration, min/max turns, crit stage, flinch chance, u16 effect, recoil, heal,
    /// target, three stats, their s8 stages and chances, then u32 flags at 0x20.
    /// </summary>
    public static MoveInfo DecodeMove(int id, ReadOnlySpan<byte> r, IReadOnlyList<string> moveNames)
    {
        if (r.Length < MoveSize)
            throw new InvalidDataException($"Move {id} is {r.Length} bytes; expected {MoveSize}.");

        var changes = new List<StatChange>();
        for (var i = 0; i < 3; i++)
            if (r[0x15 + i] != 0 && (sbyte)r[0x18 + i] != 0)
                changes.Add(new StatChange((BattleStat)r[0x15 + i], (sbyte)r[0x18 + i], r[0x1B + i]));

        int min = r[7] & 0xF, max = r[7] >> 4;
        return new MoveInfo(
            Id: id,
            Name: id < moveNames.Count ? moveNames[id] : $"#{id}",
            Type: r[0],
            Kind: (MoveKind)Math.Min((int)r[1], (int)MoveKind.Unique),
            Category: (MoveCategory)Math.Min((int)r[2], 2),
            Power: r[3],
            Accuracy: r[4],
            Pp: r[5],
            Priority: (sbyte)r[6],
            MinHits: Math.Max(1, (int)min),
            MaxHits: Math.Max(Math.Max(1, (int)min), max),
            Ailment: (Ailment)BinaryPrimitives.ReadUInt16LittleEndian(r[8..]),
            AilmentChance: r[0xA],
            CritStage: r[0xE],
            FlinchChance: r[0xF],
            Effect: BinaryPrimitives.ReadUInt16LittleEndian(r[0x10..]),
            Recoil: (sbyte)r[0x12],
            Heal: r[0x13],
            Target: r[0x14],
            StatChanges: changes,
            Flags: BinaryPrimitives.ReadInt32LittleEndian(r[0x20..]));
    }

    /// <summary>A learnset: (u16 move, u16 level) pairs ending with 0xFFFF.</summary>
    public static List<LevelMove> DecodeLearnset(ReadOnlySpan<byte> r)
    {
        var moves = new List<LevelMove>();
        for (var o = 0; o + 4 <= r.Length; o += 4)
        {
            var move = BinaryPrimitives.ReadUInt16LittleEndian(r[o..]);
            if (move == 0xFFFF)
                break;
            moves.Add(new LevelMove(BinaryPrimitives.ReadUInt16LittleEndian(r[(o + 2)..]), move));
        }
        return moves;
    }

    /// <summary>
    /// Trainer lines: <paramref name="table"/> holds (u16 trainer, u16 type) for each of <paramref name="lines"/>, in
    /// order. Returns each trainer's intro (type 0), defeat (1) and after-battle (2) line.
    /// </summary>
    public static Dictionary<int, (string? Intro, string? Defeat, string? After)> DecodeTrainerLines(ReadOnlySpan<byte> table,
        IReadOnlyList<string> lines)
    {
        var result = new Dictionary<int, (string?, string?, string?)>();
        for (var i = 0; i * 4 + 4 <= table.Length && i < lines.Count; i++)
        {
            int trainer = BinaryPrimitives.ReadUInt16LittleEndian(table[(i * 4)..]), type = BinaryPrimitives.ReadUInt16LittleEndian(table[(i * 4 + 2)..]);
            var (intro, defeat, after) = result.GetValueOrDefault(trainer);
            switch (type)
            {
                case 0:
                    intro ??= lines[i];
                    break;
                case 1:
                    defeat ??= lines[i];
                    break;
                case 2:
                    after ??= lines[i];
                    break;
            }
            result[trainer] = (intro, defeat, after);
        }
        return result;
    }

    /// <summary>
    /// A trainer: its 20-byte (or 16-byte) record — u8 Pokémon data flags (bit 0 custom moves, bit 1 held items),
    /// class, battle type, Pokémon count, four u16 items, u32 AI flags, u8 ?, u8 prize base — and its Pokémon,
    /// each u8 difficulty, u8 gender/ability, u8 level, u8 0, u16 species, u16 form, then a u16 held item and
    /// four u16 moves when the flags say so.
    /// </summary>
    public static TrainerInfo DecodeTrainer(int id, ReadOnlySpan<byte> r, ReadOnlySpan<byte> party,
        IReadOnlyList<string> names, IReadOnlyList<string> classNames)
    {
        if (r.Length < 16)
            throw new InvalidDataException($"Trainer {id} is {r.Length} bytes; expected 16 or 20.");
        int flags = r[0], trainerClass = r[1], count = r[3];
        var hasMoves = (flags & 1) != 0;
        var hasItem = (flags & 2) != 0;
        var size = 8 + (hasItem ? 2 : 0) + (hasMoves ? 8 : 0);
        if (party.Length < count * size)
            throw new InvalidDataException($"Trainer {id}'s party is {party.Length} bytes; {count} Pokémon need {count * size}.");

        var pokemon = new List<TrainerPokemon>(count);
        for (var i = 0; i < count; i++)
        {
            var p = party.Slice(i * size, size);
            var o = 8;
            var item = 0;
            if (hasItem)
            {
                item = BinaryPrimitives.ReadUInt16LittleEndian(p[o..]);
                o += 2;
            }
            List<int>? moves = null;
            if (hasMoves)
            {
                moves = new List<int>(4);
                for (var m = 0; m < 4; m++)
                    if (BinaryPrimitives.ReadUInt16LittleEndian(p[(o + m * 2)..]) is var move and > 0)
                        moves.Add(move);
            }
            pokemon.Add(new TrainerPokemon(BinaryPrimitives.ReadUInt16LittleEndian(p[4..]), BinaryPrimitives.ReadUInt16LittleEndian(p[6..]),
                p[2], p[0], p[1], item, moves));
        }

        var items = new List<int>();
        for (var i = 0; i < 4; i++)
            if (BinaryPrimitives.ReadUInt16LittleEndian(r[(4 + i * 2)..]) is var it and > 0)
                items.Add(it);
        return new TrainerInfo(id, id < names.Count ? names[id] : $"#{id}", trainerClass,
            trainerClass < classNames.Count ? classNames[trainerClass] : "", r[2], items,
            BinaryPrimitives.ReadInt32LittleEndian(r[12..]), r.Length > 17 ? r[17] : 0, pokemon);
    }

    /// <summary>
    /// An encounter file: one 232-byte set, or four (one per season). A set is 8 rate bytes (grass, double grass,
    /// shaking grass, surf, surf spots, fishing, fishing spots, unused) and then 12 + 12 + 12 + 5 + 5 + 5 + 5 slots of
    /// u16 species (low 11 bits; form above), u8 min level, u8 max level.
    /// </summary>
    public static EncounterTable DecodeEncounters(int id, ReadOnlySpan<byte> file)
    {
        if (file.Length == 0 || file.Length % EncounterSetSize != 0)
            throw new InvalidDataException($"Encounter file {id} is {file.Length} bytes; expected a multiple of {EncounterSetSize}.");

        var sets = new List<EncounterSet>();
        for (var s = 0; s < file.Length; s += EncounterSetSize)
        {
            var r = file.Slice(s, EncounterSetSize);
            var o = 8;
            List<EncounterSlot> Slots(int count, ReadOnlySpan<byte> data)
            {
                var slots = new List<EncounterSlot>(count);
                for (var i = 0; i < count; i++, o += 4)
                {
                    var sp = BinaryPrimitives.ReadUInt16LittleEndian(data[o..]);
                    slots.Add(new EncounterSlot(sp & 0x7FF, sp >> 11, data[o + 2], Math.Max(data[o + 2], data[o + 3])));
                }
                return slots;
            }
            sets.Add(new EncounterSet(r[0], r[1], r[2], r[3], r[4], r[5], r[6],
                Slots(12, r), Slots(12, r), Slots(12, r), Slots(5, r), Slots(5, r), Slots(5, r), Slots(5, r)));
        }
        return new EncounterTable(id, sets);
    }
}
