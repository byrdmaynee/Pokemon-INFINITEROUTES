using System.Buffers.Binary;

namespace InfiniteRoutes.Extractor.Nds;

/// <summary>
/// A NARC archive: "NARC" header followed by BTAF (allocation), BTNF (names) and GMIF (data) sections.
/// Most Pokémon NARCs are unnamed, so files are usually addressed by index.
/// </summary>
public sealed class Narc
{
    readonly byte[] _data;
    readonly int _dataStart;
    readonly List<(uint Start, uint End)> _fat;

    public int Count => _fat.Count;

    /// <summary>Named entries, if the archive has a name table (most don't).</summary>
    public IReadOnlyList<NitroFileEntry> NamedFiles { get; }

    Narc(byte[] data, int dataStart, List<(uint, uint)> fat, List<NitroFileEntry> named)
    {
        _data = data;
        _dataStart = dataStart;
        _fat = fat;
        NamedFiles = named;
    }

    public static bool IsNarc(ReadOnlySpan<byte> data) =>
        data.Length >= 16 && data[..4].SequenceEqual("NARC"u8) && data[4] == 0xFE && data[5] == 0xFF;

    /// <exception cref="InvalidDataException">Not a NARC, or a section is malformed.</exception>
    public static Narc Parse(byte[] data)
    {
        if (!IsNarc(data))
            throw new InvalidDataException("Not a NARC archive.");

        int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x0C));
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x0E));

        ReadOnlySpan<byte> btaf = default, btnf = default;
        var gmifStart = -1;
        var pos = headerSize;
        for (var i = 0; i < sectionCount; i++)
        {
            if (pos + 8 > data.Length)
                throw new InvalidDataException("NARC section header runs past the end of the file.");
            var magic = data.AsSpan(pos, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            if (size < 8 || pos + size > data.Length)
                throw new InvalidDataException("NARC section size is invalid.");

            var body = data.AsSpan(pos + 8, size - 8);
            if (magic.SequenceEqual("BTAF"u8)) btaf = body;
            else if (magic.SequenceEqual("BTNF"u8)) btnf = body;
            else if (magic.SequenceEqual("GMIF"u8)) gmifStart = pos + 8;
            pos += size;
        }

        if (btaf.Length < 4 || gmifStart < 0)
            throw new InvalidDataException("NARC is missing its allocation or data section.");

        int count = BinaryPrimitives.ReadUInt16LittleEndian(btaf);
        if (4 + count * 8 > btaf.Length)
            throw new InvalidDataException("NARC allocation table is truncated.");
        var fat = NitroFileTable.DecodeAllocationTable(btaf.Slice(4, count * 8));

        foreach (var (_, end) in fat)
            if (gmifStart + end > data.Length)
                throw new InvalidDataException("NARC entry points past the end of the file.");

        var named = btnf.Length >= 8 ? NitroFileTable.Decode(btnf, fat) : [];
        return new Narc(data, gmifStart, fat, named);
    }

    public ReadOnlyMemory<byte> Get(int index)
    {
        var (start, end) = _fat[index];
        return _data.AsMemory(_dataStart + (int)start, (int)(end - start));
    }

    public byte[] Read(int index) => Get(index).ToArray();
}
