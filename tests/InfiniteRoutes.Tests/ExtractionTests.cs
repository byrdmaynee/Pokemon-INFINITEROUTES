using System.Buffers.Binary;
using InfiniteRoutes.Extractor;
using InfiniteRoutes.Extractor.Extraction;
using InfiniteRoutes.Extractor.Gen5;
using InfiniteRoutes.Extractor.Graphics;
using InfiniteRoutes.Extractor.Nds;
using InfiniteRoutes.Extractor.Text;

namespace InfiniteRoutes.Tests;

public sealed class ExtractionTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ir-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Stamp_round_trips_and_marks_cache_complete()
    {
        Assert.False(ExtractionCache.IsComplete(_dir));
        ExtractionCache.WriteStamp(_dir, new ExtractionStamp(ExtractionCache.ExtractorVersion, "white", "IRAO", "abc", DateTime.UtcNow));

        Assert.True(ExtractionCache.IsComplete(_dir));
        Assert.Equal("white", GameAssets.Open(_dir)!.Stamp.GameId);
    }

    [Fact]
    public void Stamp_from_older_extractor_is_not_complete()
    {
        ExtractionCache.WriteStamp(_dir, new ExtractionStamp("0.0.1", "white", "IRAO", "abc", DateTime.UtcNow));
        Assert.False(ExtractionCache.IsComplete(_dir));
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    public void Lz_literal_only_stream_decompresses(byte type)
    {
        // Header + one flag byte of zeros = 8 literal bytes.
        byte[] data = [type, 8, 0, 0, 0x00, 1, 2, 3, 4, 5, 6, 7, 8];
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], Lz.Decompress(data));
    }

    [Fact]
    public void Lz10_back_reference_repeats_bytes()
    {
        // 'A','B' literals, then copy length 6 from distance 2 → ABABABAB.
        byte[] data = [0x10, 8, 0, 0, 0b0010_0000, (byte)'A', (byte)'B', 0x30, 0x01];
        Assert.Equal("ABABABAB"u8.ToArray(), Lz.Decompress(data));
    }

    [Fact]
    public void Lz11_short_back_reference_repeats_bytes()
    {
        // LZ11 indicator ≥ 2: length = indicator + 1. 0x5 → 6 bytes from distance 2.
        byte[] data = [0x11, 8, 0, 0, 0b0010_0000, (byte)'A', (byte)'B', 0x50, 0x01];
        Assert.Equal("ABABABAB"u8.ToArray(), Lz.Decompress(data));
    }

    [Fact]
    public void Lz_rejects_reference_before_start()
    {
        byte[] data = [0x10, 4, 0, 0, 0b1000_0000, 0x10, 0x05];
        Assert.Throws<InvalidDataException>(() => Lz.Decompress(data));
    }

    [Fact]
    public void Gen5_text_decrypts_round_trip()
    {
        var file = Gen5TextBuilder.Build(["Pikachu", "Line one\nLine two", ""]);
        Assert.Equal(["Pikachu", "Line one\nLine two", ""], Gen5Text.Decode(file));
    }

    [Fact]
    public void Bgr555_converts_to_rgba()
    {
        Assert.Equal(0xFF0000FFu, Nclr.Bgr555ToRgba(0x001F));
        Assert.Equal(0x00FF00FFu, Nclr.Bgr555ToRgba(0x03E0));
        Assert.Equal(0xFFFFFFFFu, Nclr.Bgr555ToRgba(0x7FFF));
    }

    [Fact]
    public void Personal_record_decodes_bulbasaur_layout()
    {
        var r = new byte[Gen5Personal.RecordSize];
        byte[] stats = [45, 49, 49, 45, 65, 65];
        stats.CopyTo(r, 0);
        r[6] = 11; r[7] = 3; r[8] = 45;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x0A), 0x0100);   // 1 Sp. Atk EV
        r[0x12] = 31; r[0x18] = 1; r[0x1A] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(0x22), 64);

        var types = new[] { "Normal", "Fighting", "Flying", "Poison", "Ground", "Rock", "Bug", "Ghost", "Steel", "Fire", "Water", "Grass" };
        var s = Gen5Personal.Decode(1, r, ["-", "Bulbasaur"], types, ["-", "Overgrow", "Chlorophyll"]);

        Assert.Equal("Bulbasaur", s.Name);
        Assert.Equal(318, s.Stats.Total);
        Assert.Equal(["Grass", "Poison"], s.Types);
        Assert.Equal(1, s.EvYield.SpAttack);
        Assert.Equal(["Overgrow"], s.Abilities);
        Assert.Equal("Chlorophyll", s.HiddenAbility);
        Assert.Equal(64, s.BaseExp);
    }

    [Fact]
    public void Png_has_signature_and_ihdr()
    {
        var img = new Rgba(3, 2);
        img.Set(1, 1, 0x112233FF);
        var png = Png.Encode(img);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
    }
}

/// <summary>Builds Gen 5 message files using the same rolling-key encryption as the games.</summary>
static class Gen5TextBuilder
{
    public static byte[] Build(IReadOnlyList<string> lines)
    {
        var encoded = lines.Select(Encrypt).ToList();
        var tableSize = 4 + lines.Count * 8;
        var section = new MemoryStream();
        var header = new byte[tableSize];
        var offset = tableSize;
        for (var i = 0; i < encoded.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4 + i * 8), (uint)offset);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8 + i * 8), (ushort)(encoded[i].Length / 2));
            offset += encoded[i].Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)offset);
        section.Write(header);
        foreach (var e in encoded) section.Write(e);

        var file = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(file, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), (ushort)lines.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)section.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), 16);
        return [.. file, .. section.ToArray()];
    }

    static byte[] Encrypt(string s)
    {
        var chars = s.Select(c => c == '\n' ? (ushort)0xFFFE : c).Append((ushort)0xFFFF).ToArray();
        // Decryption starts from key = enc[last] ^ 0xFFFF and walks backwards; pick the
        // last key so that the terminator encrypts to itself under that rule.
        var keys = new ushort[chars.Length];
        ushort key = 0x1234;
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            keys[i] = key;
            key = (ushort)((key >> 3) | (key << 13));
        }
        // Constraint: enc[last] ^ 0xFFFF == keys[last] and enc[last] = 0xFFFF ^ keys[last]. Consistent by construction.
        var bytes = new byte[chars.Length * 2];
        for (var i = 0; i < chars.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)(chars[i] ^ keys[i]));
        return bytes;
    }
}
