using System.Buffers.Binary;
using System.Text;

namespace InfiniteRoutes.Extractor.Nds;

/// <summary>The fields of the 0x200-byte cartridge header that the extractor needs.</summary>
public sealed record NdsHeader(
    string Title,
    string GameCode,
    string MakerCode,
    byte Revision,
    uint FntOffset,
    uint FntSize,
    uint FatOffset,
    uint FatSize,
    uint UsedRomSize,
    ushort StoredHeaderCrc,
    ushort ComputedHeaderCrc)
{
    public const int Size = 0x200;
    const int CrcCoveredLength = 0x15E;

    /// <summary>First three characters of the game code — identifies the game regardless of region.</summary>
    public string BaseCode => GameCode.Length >= 3 ? GameCode[..3] : GameCode;

    /// <summary>Last character of the game code: E = USA, P = Europe, O = international English, J = Japan, ...</summary>
    public char RegionCode => GameCode.Length == 4 ? GameCode[3] : '?';

    public bool HeaderCrcValid => StoredHeaderCrc == ComputedHeaderCrc;

    /// <exception cref="InvalidDataException">The stream is too short to be an NDS ROM.</exception>
    public static NdsHeader Read(Stream stream)
    {
        var buf = new byte[(int)Math.Min(Size, stream.Length)];
        stream.Position = 0;
        stream.ReadExactly(buf);
        return Parse(buf);
    }

    public static NdsHeader Parse(ReadOnlySpan<byte> h)
    {
        if (h.Length < 0x160)
            throw new InvalidDataException("File is too small to be a Nintendo DS ROM.");

        return new NdsHeader(
            Title: Ascii(h[..12]),
            GameCode: Ascii(h.Slice(0x0C, 4)),
            MakerCode: Ascii(h.Slice(0x10, 2)),
            Revision: h[0x1E],
            FntOffset: U32(h, 0x40),
            FntSize: U32(h, 0x44),
            FatOffset: U32(h, 0x48),
            FatSize: U32(h, 0x4C),
            UsedRomSize: U32(h, 0x80),
            StoredHeaderCrc: BinaryPrimitives.ReadUInt16LittleEndian(h[0x15E..]),
            ComputedHeaderCrc: Crc16.Compute(h[..CrcCoveredLength]));
    }

    static uint U32(ReadOnlySpan<byte> h, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(h[offset..]);

    static string Ascii(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        var s = Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]);
        return new string(s.Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());
    }
}
