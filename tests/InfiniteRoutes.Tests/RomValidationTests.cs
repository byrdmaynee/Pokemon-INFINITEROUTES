using System.IO.Compression;
using System.Security.Cryptography;
using InfiniteRoutes.Extractor.Roms;
using InfiniteRoutes.Tests.Fakes;

namespace InfiniteRoutes.Tests;

public sealed class RomValidationTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ir-tests-").FullName;
    readonly RomManifest _manifest = RomManifest.LoadDefault();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    RomValidator Validator => new(_manifest);

    [Fact]
    public void Default_manifest_covers_all_nine_ds_games()
    {
        Assert.Equal(
            ["black", "black2", "diamond", "heartgold", "pearl", "platinum", "soulsilver", "white", "white2"],
            _manifest.Games.Select(g => g.Id).Order());
        Assert.All(_manifest.Regions.SelectMany(r => r.AnyOf), id => Assert.NotNull(_manifest.FindById(id)));
    }

    [Theory]
    [InlineData("IRBO", "black")]
    [InlineData("IRAO", "white")]
    [InlineData("IREO", "black2")]
    [InlineData("IRDO", "white2")]
    [InlineData("CPUE", "platinum")]
    [InlineData("IPKE", "heartgold")]
    [InlineData("IPGP", "soulsilver")]
    [InlineData("ADAE", "diamond")]
    [InlineData("APAE", "pearl")]
    public void Each_supported_game_validates(string gameCode, string expectedId)
    {
        var game = _manifest.FindById(expectedId)!;
        var path = TestRomBuilder.ForGame(gameCode, game.RequiredFiles[0]).WriteTo(_dir, $"{expectedId}.nds");

        var r = Validator.Validate(path);

        Assert.Equal(RomStatus.Valid, r.Status);
        Assert.Equal(expectedId, r.Game!.Id);
        Assert.False(r.HashVerified);
        Assert.Equal(40, r.Sha1!.Length);
    }

    [Fact]
    public void Unknown_game_is_not_supported()
    {
        var path = new TestRomBuilder { Title = "MARIO", GameCode = "AMCE" }.Add("x", [1]).WriteTo(_dir, "x.nds");
        Assert.Equal(RomStatus.NotSupportedGame, Validator.Validate(path).Status);
    }

    [Fact]
    public void Non_rom_file_is_not_supported()
    {
        var path = Path.Combine(_dir, "notes.nds");
        File.WriteAllText(path, "definitely not a rom");
        Assert.Equal(RomStatus.NotSupportedGame, Validator.Validate(path).Status);
    }

    [Fact]
    public void Japanese_dump_is_unsupported_region()
    {
        var path = TestRomBuilder.ForGame("IRBJ", "a/0/1/6").WriteTo(_dir, "b-jp.nds");
        var r = Validator.Validate(path);
        Assert.Equal(RomStatus.UnsupportedRegion, r.Status);
        Assert.Equal("black", r.Game!.Id);
    }

    [Fact]
    public void Bad_header_crc_is_corrupt()
    {
        var builder = TestRomBuilder.ForGame("IRBO", "a/0/1/6");
        builder.CorruptHeaderCrc = true;
        Assert.Equal(RomStatus.Corrupt, Validator.Validate(builder.WriteTo(_dir, "b.nds")).Status);
    }

    [Fact]
    public void Missing_required_file_is_corrupt()
    {
        var path = new TestRomBuilder { GameCode = "IRBO" }.Add("a/0/1/5", NarcBuilder.Build([1])).WriteTo(_dir, "b.nds");
        var r = Validator.Validate(path);
        Assert.Equal(RomStatus.Corrupt, r.Status);
        Assert.Contains("a/0/1/6", r.Message);
    }

    [Fact]
    public void Required_file_that_is_not_a_narc_is_corrupt()
    {
        var path = new TestRomBuilder { GameCode = "IRBO" }.Add("a/0/1/6", [1, 2, 3]).WriteTo(_dir, "b.nds");
        Assert.Equal(RomStatus.Corrupt, Validator.Validate(path).Status);
    }

    [Fact]
    public void Known_dump_list_verifies_or_rejects_by_sha1()
    {
        var bytes = TestRomBuilder.ForGame("IRBO", "a/0/1/6").Build();
        var good = Path.Combine(_dir, "good.nds");
        File.WriteAllBytes(good, bytes);
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes));

        var withDump = _manifest with
        {
            Games = _manifest.Games
                .Select(g => g.Id == "black" ? g with { KnownDumps = [new KnownDump("O", 0, sha1.ToUpperInvariant())] } : g)
                .ToList(),
        };
        var validator = new RomValidator(withDump);

        var verified = validator.Validate(good);
        Assert.Equal(RomStatus.Valid, verified.Status);
        Assert.True(verified.HashVerified);

        var other = TestRomBuilder.ForGame("IRBO", "a/0/1/6").Add("extra.bin", [42]).WriteTo(_dir, "other.nds");
        Assert.Equal(RomStatus.UnknownDump, validator.Validate(other).Status);
    }

    [Fact]
    public void Rom_inside_zip_validates()
    {
        var zipPath = Path.Combine(_dir, "white.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("Pokemon - White Version (USA, Europe).nds");
            using var s = entry.Open();
            s.Write(TestRomBuilder.ForGame("IRAO", "a/0/1/6").Build());
        }

        var r = Validator.Validate(zipPath);
        Assert.Equal(RomStatus.Valid, r.Status);
        Assert.Equal("white", r.Game!.Id);
    }

    [Fact]
    public void Zip_without_rom_is_not_supported()
    {
        var zipPath = Path.Combine(_dir, "empty.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            zip.CreateEntry("readme.txt");

        Assert.Equal(RomStatus.NotSupportedGame, Validator.Validate(zipPath).Status);
    }

    [Fact]
    public void Missing_file_is_unreadable()
    {
        Assert.Equal(RomStatus.Unreadable, Validator.Validate(Path.Combine(_dir, "nope.nds")).Status);
    }
}
