using System.Buffers.Binary;
using System.Text;
using InfiniteRoutes.Extractor.Nds;

namespace InfiniteRoutes.Tests.Fakes;

/// <summary>
/// Builds small synthetic NDS ROMs with a real header, FNT/FAT and file data, so the readers
/// can be tested without any copyrighted ROM in the repo.
/// </summary>
public sealed class TestRomBuilder
{
    readonly SortedDictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public string Title { get; set; } = "POKEMON B";
    public string GameCode { get; set; } = "IRBO";
    public byte Revision { get; set; }
    public bool CorruptHeaderCrc { get; set; }

    public TestRomBuilder Add(string path, byte[] data)
    {
        _files[path] = data;
        return this;
    }

    public TestRomBuilder AddNarc(string path, params byte[][] members) => Add(path, NarcBuilder.Build(members));

    /// <summary>A ROM that passes validation for the given manifest game.</summary>
    public static TestRomBuilder ForGame(string gameCode, string requiredFile) =>
        new TestRomBuilder { GameCode = gameCode }
            .AddNarc(requiredFile, [1, 2, 3], [4, 5])
            .Add("readme.txt", "hello"u8.ToArray());

    public byte[] Build()
    {
        var (fnt, orderedFiles) = BuildFnt();
        var fntOffset = 0x200;
        var fatOffset = Align(fntOffset + fnt.Length);
        var dataOffset = Align(fatOffset + orderedFiles.Count * 8);

        var fat = new byte[orderedFiles.Count * 8];
        var data = new MemoryStream();
        for (var i = 0; i < orderedFiles.Count; i++)
        {
            var start = dataOffset + (int)data.Length;
            data.Write(orderedFiles[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan(i * 8), (uint)start);
            BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan(i * 8 + 4), (uint)(start + orderedFiles[i].Length));
            while (data.Length % 4 != 0) data.WriteByte(0xFF);
        }

        var rom = new byte[dataOffset + data.Length];
        Encoding.ASCII.GetBytes(Title.PadRight(12, '\0')[..12]).CopyTo(rom, 0);
        Encoding.ASCII.GetBytes(GameCode).CopyTo(rom, 0x0C);
        Encoding.ASCII.GetBytes("01").CopyTo(rom, 0x10);
        rom[0x1E] = Revision;
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x40), (uint)fntOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x44), (uint)fnt.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x48), (uint)fatOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x4C), (uint)fat.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x80), (uint)rom.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(rom.AsSpan(0x84), 0x4000);
        fnt.CopyTo(rom, fntOffset);
        fat.CopyTo(rom, fatOffset);
        data.ToArray().CopyTo(rom, dataOffset);

        var crc = Crc16.Compute(rom.AsSpan(0, 0x15E));
        BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0x15E), CorruptHeaderCrc ? (ushort)~crc : crc);
        return rom;
    }

    public string WriteTo(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, Build());
        return path;
    }

    sealed class Dir(string name)
    {
        public string Name { get; } = name;
        public SortedDictionary<string, Dir> Dirs { get; } = new(StringComparer.Ordinal);
        public List<(string Name, byte[] Data)> Files { get; } = [];
        public int Index { get; set; }
        public int FirstFileId { get; set; }
        public Dir? Parent { get; set; }
    }

    (byte[] Fnt, List<byte[]> OrderedFiles) BuildFnt()
    {
        var root = new Dir("");
        foreach (var (path, data) in _files)
        {
            var parts = path.Split('/');
            var dir = root;
            foreach (var part in parts[..^1])
            {
                if (!dir.Dirs.TryGetValue(part, out var child))
                    dir.Dirs[part] = child = new Dir(part) { Parent = dir };
                dir = child;
            }
            dir.Files.Add((parts[^1], data));
        }

        var dirs = new List<Dir>();
        void Number(Dir d)
        {
            d.Index = dirs.Count;
            dirs.Add(d);
            foreach (var c in d.Dirs.Values) Number(c);
        }
        Number(root);

        var ordered = new List<byte[]>();
        foreach (var d in dirs)
        {
            d.FirstFileId = ordered.Count;
            ordered.AddRange(d.Files.Select(f => f.Data));
        }

        var main = new byte[dirs.Count * 8];
        var subs = new MemoryStream();
        foreach (var d in dirs)
        {
            var o = d.Index * 8;
            BinaryPrimitives.WriteUInt32LittleEndian(main.AsSpan(o), (uint)(main.Length + subs.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(main.AsSpan(o + 4), (ushort)d.FirstFileId);
            BinaryPrimitives.WriteUInt16LittleEndian(main.AsSpan(o + 6),
                d.Parent is null ? (ushort)dirs.Count : (ushort)(0xF000 + d.Parent.Index));

            foreach (var (name, _) in d.Files)
            {
                subs.WriteByte((byte)name.Length);
                subs.Write(Encoding.ASCII.GetBytes(name));
            }
            foreach (var c in d.Dirs.Values)
            {
                subs.WriteByte((byte)(0x80 | c.Name.Length));
                subs.Write(Encoding.ASCII.GetBytes(c.Name));
                var id = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(id, (ushort)(0xF000 + c.Index));
                subs.Write(id);
            }
            subs.WriteByte(0);
        }

        return ([.. main, .. subs.ToArray()], ordered);
    }

    static int Align(int value) => (value + 0x1FF) & ~0x1FF;
}

public static class NarcBuilder
{
    /// <summary>Builds an unnamed NARC, the layout most Pokémon archives use.</summary>
    public static byte[] Build(params byte[][] members)
    {
        var gmif = new MemoryStream();
        var btafBody = new byte[4 + members.Length * 8];
        BinaryPrimitives.WriteUInt16LittleEndian(btafBody, (ushort)members.Length);
        for (var i = 0; i < members.Length; i++)
        {
            var start = (uint)gmif.Length;
            gmif.Write(members[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(btafBody.AsSpan(4 + i * 8), start);
            BinaryPrimitives.WriteUInt32LittleEndian(btafBody.AsSpan(8 + i * 8), start + (uint)members[i].Length);
            while (gmif.Length % 4 != 0) gmif.WriteByte(0xFF);
        }

        // Nameless BTNF: one root record whose sub-table offset (4) lands on its own zero first-file-id.
        byte[] btnfBody = [4, 0, 0, 0, 0, 0, 1, 0];

        var sections = new MemoryStream();
        WriteSection(sections, "BTAF", btafBody);
        WriteSection(sections, "BTNF", btnfBody);
        WriteSection(sections, "GMIF", gmif.ToArray());

        var header = new byte[16];
        "NARC"u8.CopyTo(header);
        header[4] = 0xFE;
        header[5] = 0xFF;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 0x0100);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)(16 + sections.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), 3);
        return [.. header, .. sections.ToArray()];
    }

    static void WriteSection(Stream s, string magic, byte[] body)
    {
        s.Write(Encoding.ASCII.GetBytes(magic));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(8 + body.Length));
        s.Write(size);
        s.Write(body);
    }
}
