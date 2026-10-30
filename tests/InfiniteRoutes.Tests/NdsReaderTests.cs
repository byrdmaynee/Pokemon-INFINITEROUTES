using InfiniteRoutes.Extractor.Nds;
using InfiniteRoutes.Tests.Fakes;

namespace InfiniteRoutes.Tests;

public class NdsReaderTests
{
    [Fact]
    public void Crc16_matches_known_modbus_vector()
    {
        Assert.Equal(0x4B37, Crc16.Compute("123456789"u8));
    }

    [Fact]
    public void Header_fields_are_read()
    {
        var rom = new TestRomBuilder { Title = "POKEMON PL", GameCode = "CPUE", Revision = 1 }.Add("x.bin", [1]).Build();

        var h = NdsHeader.Parse(rom);

        Assert.Equal("POKEMON PL", h.Title);
        Assert.Equal("CPUE", h.GameCode);
        Assert.Equal("CPU", h.BaseCode);
        Assert.Equal('E', h.RegionCode);
        Assert.Equal(1, h.Revision);
        Assert.True(h.HeaderCrcValid);
    }

    [Fact]
    public void Corrupt_header_crc_is_detected()
    {
        var rom = new TestRomBuilder { CorruptHeaderCrc = true }.Add("x.bin", [1]).Build();
        Assert.False(NdsHeader.Parse(rom).HeaderCrcValid);
    }

    [Fact]
    public void Too_small_file_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => NdsHeader.Read(new MemoryStream(new byte[0x100])));
    }

    [Fact]
    public void NitroFs_lists_nested_paths_and_reads_files()
    {
        var rom = new TestRomBuilder()
            .Add("a/0/1/6", [9, 8, 7])
            .Add("a/0/0/2", [6])
            .Add("poketool/personal/pl_personal.narc", [5, 5])
            .Add("root.bin", [1, 2])
            .Build();

        using var fs = NitroFs.Open(new MemoryStream(rom));

        Assert.Equal(4, fs.Files.Count);
        Assert.Equal([9, 8, 7], fs.ReadFile("a/0/1/6"));
        Assert.Equal([5, 5], fs.ReadFile("/poketool/personal/pl_personal.narc"));
        Assert.Equal([1, 2], fs.ReadFile("root.bin"));
        Assert.False(fs.Exists("a/0/1/7"));
        Assert.Equal(["a/", "poketool/", "root.bin"], fs.List());
        Assert.Equal(["2"], fs.List("a/0/0"));
    }

    [Fact]
    public void NitroFs_rejects_truncated_rom()
    {
        var rom = new TestRomBuilder().Add("big.bin", new byte[4096]).Build();
        var truncated = rom[..^2048];

        Assert.Throws<InvalidDataException>(() => NitroFs.Open(new MemoryStream(truncated)));
    }

    [Fact]
    public void Narc_round_trips_members()
    {
        var narc = Narc.Parse(NarcBuilder.Build([1, 2, 3], [], [4, 5, 6, 7, 8]));

        Assert.Equal(3, narc.Count);
        Assert.Equal([1, 2, 3], narc.Read(0));
        Assert.Empty(narc.Read(1));
        Assert.Equal([4, 5, 6, 7, 8], narc.Read(2));
        Assert.Empty(narc.NamedFiles);
    }

    [Fact]
    public void Narc_rejects_garbage_and_truncation()
    {
        Assert.Throws<InvalidDataException>(() => Narc.Parse("NOPE not a narc"u8.ToArray()));
        var good = NarcBuilder.Build([1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Throws<InvalidDataException>(() => Narc.Parse(good[..^6]));
    }
}
