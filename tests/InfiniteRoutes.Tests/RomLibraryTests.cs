using InfiniteRoutes.Core.Config;
using InfiniteRoutes.Extractor.Roms;
using InfiniteRoutes.Tests.Fakes;

namespace InfiniteRoutes.Tests;

public sealed class RomLibraryTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ir-tests-").FullName;
    readonly RomManifest _manifest = RomManifest.LoadDefault();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Rom(string code, string file, string name, string sub = "") =>
        TestRomBuilder.ForGame(code, file).WriteTo(Directory.CreateDirectory(Path.Combine(_dir, sub)).FullName, name);

    [Fact]
    public void Empty_library_cannot_play_and_has_all_regions_locked()
    {
        var lib = new RomLibrary(_manifest);
        Assert.False(lib.CanPlay);
        Assert.All(lib.Regions(), r => Assert.False(r.Available));
    }

    [Fact]
    public void Scan_assigns_valid_roms_and_unlocks_regions()
    {
        Rom("IRAO", "a/0/1/6", "white.nds");
        Rom("CPUE", "poketool/personal/pl_personal.narc", "plat.nds", "sub");
        Rom("IRBJ", "a/0/1/6", "black-jp.nds");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "ignored");

        var lib = new RomLibrary(_manifest);
        var results = lib.Scan(_dir, new RomValidator(_manifest));

        Assert.Equal(3, results.Count);
        Assert.Equal(RomStatus.Valid, lib["white"].Status);
        Assert.Equal(RomStatus.Valid, lib["platinum"].Status);
        Assert.Equal(RomStatus.Missing, lib["black"].Status);
        Assert.True(lib.CanPlay);

        var regions = lib.Regions().ToDictionary(r => r.Region.Id);
        Assert.Equal("white", regions["unova"].ProvidedBy);
        Assert.True(regions["sinnoh"].Available);
        Assert.False(regions["johto_kanto"].Available);
    }

    [Fact]
    public void Picking_the_other_version_moves_it_to_its_own_slot()
    {
        var path = Rom("IRAO", "a/0/1/6", "white.nds");
        var lib = new RomLibrary(_manifest);

        var r = lib.AssignFromFile("black", path, new RomValidator(_manifest));

        Assert.Equal("white", r.Game!.Id);
        Assert.Equal(path, lib["white"].Path);
        Assert.Null(lib["black"].Path);
    }

    [Fact]
    public void Save_and_load_round_trip_keeps_status_while_file_is_unchanged()
    {
        var path = Rom("IRBO", "a/0/1/6", "it's black.nds");   // apostrophe forces a basic TOML string
        var file = Path.Combine(_dir, "config", "roms.pkir");
        var lib = new RomLibrary(_manifest) { WizardCompleted = true };
        lib.AssignFromFile("black", path, new RomValidator(_manifest));
        lib.Save(file);

        var loaded = RomLibrary.Load(file, _manifest);

        Assert.True(loaded.WizardCompleted);
        Assert.Equal(path, loaded["black"].Path);
        Assert.Equal(RomStatus.Valid, loaded["black"].Status);
        Assert.Equal(lib["black"].Sha1, loaded["black"].Sha1);
        Assert.True(loaded.CanPlay);
    }

    [Fact]
    public void Changed_file_needs_rechecking_and_deleted_file_is_unreadable()
    {
        var black = Rom("IRBO", "a/0/1/6", "black.nds");
        var white = Rom("IRAO", "a/0/1/6", "white.nds");
        var file = Path.Combine(_dir, "roms.pkir");
        var lib = new RomLibrary(_manifest);
        lib.Scan(_dir, new RomValidator(_manifest));
        lib.Save(file);

        File.AppendAllText(black, "x");
        File.Delete(white);
        var loaded = RomLibrary.Load(file, _manifest);

        Assert.Equal(RomStatus.Unchecked, loaded["black"].Status);
        Assert.Equal(RomStatus.Unreadable, loaded["white"].Status);
        Assert.False(loaded.CanPlay);
    }

    [Fact]
    public void Hand_edited_path_is_picked_up()
    {
        var path = Rom("IRBO", "a/0/1/6", "black.nds");
        var file = Path.Combine(_dir, "roms.pkir");
        File.WriteAllText(file, $"[roms.black]\npath = {Pkir.Quote(path)}\n");

        var loaded = RomLibrary.Load(file, _manifest);

        Assert.Equal(path, loaded["black"].Path);
        Assert.Equal(RomStatus.Unchecked, loaded["black"].Status);
    }

    [Fact]
    public void Broken_pkir_reports_file_name()
    {
        var file = Path.Combine(_dir, "roms.pkir");
        File.WriteAllText(file, "[roms.black\npath = = 3");
        var e = Assert.Throws<PkirException>(() => RomLibrary.Load(file, _manifest));
        Assert.Contains("roms.pkir", e.Message);
    }

    [Theory]
    [InlineData(@"C:\Games\Pokemon Black.nds")]
    [InlineData(@"C:\It's\here.nds")]
    [InlineData("tab\there")]
    public void Quote_round_trips(string value)
    {
        var parsed = Pkir.Parse($"v = {Pkir.Quote(value)}");
        Assert.Equal(value, parsed.GetString("v"));
    }
}
