using System.Buffers.Binary;
using System.Text;

namespace InfiniteRoutes.Extractor.Text;

/// <summary>
/// Decodes Gen 5 message files (one NARC member = one list of strings).
/// Layout: u16 sectionCount, u16 entryCount, u32 sectionSize, u32 reserved, u32 sectionOffsets[];
/// each section: u32 length, then entryCount × (u32 offset, u16 charCount, u16 unused), then
/// UTF-16 strings, each XOR-encrypted with a rolling key derived from its last character.
/// Control codes become markup tokens: <c>\n</c>, <c>\r</c> (scroll), <c>\f</c> (clear) and
/// <c>[VAR XXXX(a,b)]</c> for placeholders such as the player's name.
/// </summary>
public static class Gen5Text
{
    public static List<string> Decode(ReadOnlySpan<byte> file, int section = 0)
    {
        if (file.Length < 12)
            throw new InvalidDataException("Message file is too small.");

        int sectionCount = U16(file, 0);
        int entryCount = U16(file, 2);
        if (section >= sectionCount || 12 + sectionCount * 4 > file.Length)
            throw new InvalidDataException("Message file section table is invalid.");

        var sectionStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(file[(12 + section * 4)..]);
        if (sectionStart + 4 + entryCount * 8 > file.Length)
            throw new InvalidDataException("Message file entry table runs past the end of the file.");

        var result = new List<string>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var entry = sectionStart + 4 + i * 8;
            var offset = sectionStart + (int)BinaryPrimitives.ReadUInt32LittleEndian(file[entry..]);
            int length = U16(file, entry + 4);
            if (offset + length * 2 > file.Length)
                throw new InvalidDataException($"String {i} runs past the end of the file.");
            result.Add(DecodeString(file.Slice(offset, length * 2)));
        }
        return result;
    }

    static string DecodeString(ReadOnlySpan<byte> raw)
    {
        var count = raw.Length / 2;
        if (count == 0)
            return "";

        var chars = new ushort[count];
        var key = (ushort)(U16(raw, (count - 1) * 2) ^ 0xFFFF);
        for (var i = count - 1; i >= 0; i--)
        {
            chars[i] = (ushort)(U16(raw, i * 2) ^ key);
            key = (ushort)((key >> 3) | (key << 13));
        }

        var sb = new StringBuilder(count);
        for (var i = 0; i < count; i++)
        {
            var c = chars[i];
            switch (c)
            {
                case 0xFFFF:
                    return sb.ToString();
                case 0xFFFE:
                    sb.Append('\n');
                    break;
                case 0xF000 when i + 2 < count:
                {
                    // Variable/command: id, argument count, arguments.
                    var id = chars[++i];
                    int argCount = chars[++i];
                    var args = new List<string>();
                    for (var a = 0; a < argCount && i + 1 < count; a++)
                        args.Add(chars[++i].ToString());
                    sb.Append(id switch
                    {
                        0xBE00 => "\r",   // wait for button, then scroll
                        0xBE01 => "\f",   // wait for button, then clear
                        _ => $"[VAR {id:X4}({string.Join(",", args)})]",
                    });
                    break;
                }
                default:
                    sb.Append((char)c);
                    break;
            }
        }
        return sb.ToString();
    }

    static ushort U16(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b[offset..]);
}
