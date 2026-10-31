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
