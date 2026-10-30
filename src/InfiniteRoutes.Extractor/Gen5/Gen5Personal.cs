using System.Buffers.Binary;
using InfiniteRoutes.Core.Data;

namespace InfiniteRoutes.Extractor.Gen5;

/// <summary>Decodes one 60-byte Black/White "personal" record (base stats and species traits).</summary>
public static class Gen5Personal
{
    public const int RecordSize = 0x3C;

    public static SpeciesInfo Decode(int id, ReadOnlySpan<byte> r, IReadOnlyList<string> speciesNames,
        IReadOnlyList<string> typeNames, IReadOnlyList<string> abilityNames)
    {
        if (r.Length < RecordSize)
            throw new InvalidDataException($"Personal record {id} is {r.Length} bytes; expected {RecordSize}.");

        var evs = BinaryPrimitives.ReadUInt16LittleEndian(r[0x0A..]);
        int Ev(int slot) => (evs >> (slot * 2)) & 3;

        string Name(IReadOnlyList<string> list, int index) => index < list.Count ? list[index] : $"#{index}";

        var types = new List<string> { Name(typeNames, r[6]) };
        if (r[7] != r[6])
            types.Add(Name(typeNames, r[7]));

        var abilities = new List<string> { Name(abilityNames, r[0x18]) };
        if (r[0x19] != 0 && r[0x19] != r[0x18])
            abilities.Add(Name(abilityNames, r[0x19]));

        return new SpeciesInfo(
            Id: id,
            Name: Name(speciesNames, id),
            Stats: new BaseStats(r[0], r[1], r[2], r[3], r[4], r[5]),
            Types: types,
            CatchRate: r[8],
            EvYield: new BaseStats(Ev(0), Ev(1), Ev(2), Ev(3), Ev(4), Ev(5)),
            GenderRatio: r[0x12],
            HatchCycles: r[0x13],
            BaseFriendship: r[0x14],
            GrowthRate: r[0x15],
            EggGroups: r[0x16] == r[0x17] ? [r[0x16]] : [r[0x16], r[0x17]],
            Abilities: abilities,
            HiddenAbility: r[0x1A] == 0 ? null : Name(abilityNames, r[0x1A]),
            BaseExp: BinaryPrimitives.ReadUInt16LittleEndian(r[0x22..]),
            HeightDecimetres: BinaryPrimitives.ReadUInt16LittleEndian(r[0x24..]),
            WeightHectograms: BinaryPrimitives.ReadUInt16LittleEndian(r[0x26..]),
            FormCount: r[0x20]);
    }
}
