using System.Buffers.Binary;
using System.Text;

namespace InfiniteRoutes.Extractor.Nds;

/// <param name="Path">Slash-separated, no leading slash (e.g. <c>a/0/1/6</c>).</param>
/// <param name="Start">Offset of the first byte. Absolute in a ROM; relative to the data section in a NARC.</param>
/// <param name="End">Offset one past the last byte.</param>
public sealed record NitroFileEntry(int Id, string Path, uint Start, uint End)
{
    public uint Length => End - Start;
}

/// <summary>
/// Decodes a Nitro file name table (FNT), shared by the ROM filesystem and NARC archives.
/// Layout: a main table of 8-byte directory records, then per-directory sub-tables of
/// length-prefixed names. Directory IDs are 0xF000 + index.
/// </summary>
public static class NitroFileTable
{
    const int MaxDirectories = 0x1000;

    public static List<NitroFileEntry> Decode(ReadOnlySpan<byte> fnt, IReadOnlyList<(uint Start, uint End)> fat)
    {
        if (fnt.Length < 8)
            throw new InvalidDataException("File name table is too small.");

        int dirCount = BinaryPrimitives.ReadUInt16LittleEndian(fnt[6..]);
        if (dirCount is 0 or > MaxDirectories || dirCount * 8 > fnt.Length)
            throw new InvalidDataException($"File name table has an invalid directory count ({dirCount}).");

        var result = new List<NitroFileEntry>();
        var visited = new bool[dirCount];
        Walk(fnt, fat, 0, "", visited, result);
        return result;
    }

    static void Walk(ReadOnlySpan<byte> fnt, IReadOnlyList<(uint Start, uint End)> fat, int dirIndex, string prefix,
        bool[] visited, List<NitroFileEntry> result)
    {
        if (visited[dirIndex])
            throw new InvalidDataException("File name table contains a directory cycle.");
        visited[dirIndex] = true;

        var record = fnt.Slice(dirIndex * 8, 8);
        var pos = (int)BinaryPrimitives.ReadUInt32LittleEndian(record);
        int fileId = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);

        while (true)
        {
            if (pos >= fnt.Length)
                throw new InvalidDataException("File name table entry runs past the end of the table.");

            var typeLength = fnt[pos++];
            if (typeLength == 0x00)
                return;
            if (typeLength == 0x80)
                throw new InvalidDataException("File name table uses a reserved entry type.");

            var nameLength = typeLength & 0x7F;
            if (pos + nameLength > fnt.Length)
                throw new InvalidDataException("File name runs past the end of the table.");
            var name = Encoding.Latin1.GetString(fnt.Slice(pos, nameLength));
            pos += nameLength;

            if ((typeLength & 0x80) != 0)
            {
                if (pos + 2 > fnt.Length)
                    throw new InvalidDataException("Directory entry runs past the end of the table.");
                var childIndex = BinaryPrimitives.ReadUInt16LittleEndian(fnt[pos..]) - 0xF000;
                pos += 2;
                if (childIndex <= 0 || childIndex >= visited.Length)
                    throw new InvalidDataException($"Directory '{prefix}{name}' has an invalid ID.");
                Walk(fnt, fat, childIndex, prefix + name + "/", visited, result);
            }
            else
            {
                if (fileId >= fat.Count)
                    throw new InvalidDataException($"File '{prefix}{name}' has no allocation table entry.");
                var (start, end) = fat[fileId];
                result.Add(new NitroFileEntry(fileId, prefix + name, start, end));
                fileId++;
            }
        }
    }

    public static List<(uint Start, uint End)> DecodeAllocationTable(ReadOnlySpan<byte> fat)
    {
        var entries = new List<(uint, uint)>(fat.Length / 8);
        for (var i = 0; i + 8 <= fat.Length; i += 8)
        {
            var start = BinaryPrimitives.ReadUInt32LittleEndian(fat[i..]);
            var end = BinaryPrimitives.ReadUInt32LittleEndian(fat[(i + 4)..]);
            if (end < start)
                throw new InvalidDataException($"Allocation table entry {i / 8} ends before it starts.");
            entries.Add((start, end));
        }
        return entries;
    }
}
