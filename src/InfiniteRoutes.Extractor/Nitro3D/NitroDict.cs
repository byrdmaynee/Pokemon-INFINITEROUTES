using System.Buffers.Binary;
using System.Text;

namespace InfiniteRoutes.Extractor.Nitro3D;

/// <summary>
/// The name dictionary used throughout Nitro 3D files: a lookup tree (skipped) followed by an
/// info block of fixed-size entries and a table of 16-byte names.
/// Layout: u8 0, u8 count, u16 size; u16 at +6 is the info block's offset from the dictionary start.
/// Info block: u16 entrySize, u16 namesOffset (from info start), then the entries.
/// </summary>
public readonly record struct DictEntry(string Name, int EntryOffset);

public static class NitroDict
{
    public static List<DictEntry> Read(ReadOnlySpan<byte> b, int offset)
    {
        int count = b[offset + 1];
        var info = offset + BinaryPrimitives.ReadUInt16LittleEndian(b[(offset + 6)..]);
        int entrySize = BinaryPrimitives.ReadUInt16LittleEndian(b[info..]);
        var names = info + BinaryPrimitives.ReadUInt16LittleEndian(b[(info + 2)..]);

        var result = new List<DictEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var raw = b.Slice(names + i * 16, 16);
            var end = raw.IndexOf((byte)0);
            result.Add(new DictEntry(Encoding.ASCII.GetString(end < 0 ? raw : raw[..end]), info + 4 + i * entrySize));
        }
        return result;
    }

    public static int Section(ReadOnlySpan<byte> file, string magic)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(file[0x0E..]);
        var want = Encoding.ASCII.GetBytes(magic);
        for (var i = 0; i < count; i++)
        {
            var off = (int)BinaryPrimitives.ReadUInt32LittleEndian(file[(0x10 + i * 4)..]);
            if (off + 4 <= file.Length && file.Slice(off, 4).SequenceEqual(want))
                return off;
        }
        return -1;
    }

    public static ushort U16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16LittleEndian(b[o..]);
    public static short S16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt16LittleEndian(b[o..]);
    public static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    public static int S32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
    public static float Fx32(ReadOnlySpan<byte> b, int o) => S32(b, o) / 4096f;
    public static float Fx16(ReadOnlySpan<byte> b, int o) => S16(b, o) / 4096f;
}
