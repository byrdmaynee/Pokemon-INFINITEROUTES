using InfiniteRoutes.Core;
using InfiniteRoutes.Extractor.Nds;
using InfiniteRoutes.Extractor.Roms;

const string Usage = """
    ir-extract — Infinite Routes ROM tool

    Usage:
      ir-extract info <rom>              Show the cartridge header
      ir-extract validate <rom>          Run the full validation the game client runs (prints SHA-1)
      ir-extract ls <rom> [dir]          List the ROM filesystem
      ir-extract narc <rom> <path>       List the files inside a NARC archive in the ROM
      ir-extract scan <folder>           Find and validate every supported ROM in a folder
      ir-extract library                 Show the player's ROM library (roms.pkir)
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return 0;
}

var manifest = RomManifest.LoadDefault();
var validator = new RomValidator(manifest);

try
{
    return args[0] switch
    {
        "info" when args.Length >= 2 => Info(args[1]),
        "validate" when args.Length >= 2 => Validate(args[1]),
        "ls" when args.Length >= 2 => List(args[1], args.Length >= 3 ? args[2] : ""),
        "narc" when args.Length >= 3 => NarcList(args[1], args[2]),
        "scan" when args.Length >= 2 => Scan(args[1]),
        "library" => Library(),
        _ => Fail($"Unknown command or missing arguments.\n\n{Usage}"),
    };
}
catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
{
    return Fail(e.Message);
}

int Info(string path)
{
    using var s = RomSource.OpenSeekable(path);
    var h = NdsHeader.Read(s);
    Console.WriteLine($"Title        {h.Title}");
    Console.WriteLine($"Game code    {h.GameCode} (region '{h.RegionCode}')");
    Console.WriteLine($"Maker        {h.MakerCode}");
    Console.WriteLine($"Revision     {h.Revision}");
    Console.WriteLine($"Header CRC   {h.StoredHeaderCrc:X4} ({(h.HeaderCrcValid ? "ok" : $"MISMATCH, computed {h.ComputedHeaderCrc:X4}")})");
    Console.WriteLine($"FNT          0x{h.FntOffset:X8} (+0x{h.FntSize:X})");
    Console.WriteLine($"FAT          0x{h.FatOffset:X8} (+0x{h.FatSize:X})");
    Console.WriteLine($"Used size    {h.UsedRomSize:N0} bytes; file {s.Length:N0} bytes");
    Console.WriteLine($"Game         {manifest.FindByBaseCode(h.BaseCode)?.Name ?? "(not a supported game)"}");
    return 0;
}

int Validate(string path)
{
    var lastPercent = -1;
    var progress = new SyncProgress(p =>
    {
        var percent = (int)(p * 100);
        if (percent / 10 != lastPercent / 10)
            Console.Error.Write($"\rHashing {percent}%   ");
        lastPercent = percent;
    });
    var r = validator.Validate(path, progress);
    Console.Error.Write("\r                \r");
    Console.WriteLine($"Status   {r.Label}");
    Console.WriteLine($"Details  {r.Message}");
    if (r.Game is not null) Console.WriteLine($"Game     {r.Game.Name} [{r.Game.Id}, profile {r.Game.Profile}]");
    if (r.Sha1 is not null) Console.WriteLine($"SHA-1    {r.Sha1}");
    return r.Status == RomStatus.Valid ? 0 : 2;
}

int List(string path, string dir)
{
    using var fs = NitroFs.Open(RomSource.OpenSeekable(path));
    foreach (var name in fs.List(dir))
    {
        var entry = name.EndsWith('/') ? null : fs.Find(dir.Length == 0 ? name : dir.TrimEnd('/') + "/" + name);
        Console.WriteLine(entry is null ? name : $"{name,-40} {entry.Length,12:N0}  id {entry.Id}");
    }
    return 0;
}

int NarcList(string path, string narcPath)
{
    using var fs = NitroFs.Open(RomSource.OpenSeekable(path));
    var narc = Narc.Parse(fs.ReadFile(narcPath));
    Console.WriteLine($"{narcPath}: {narc.Count} files{(narc.NamedFiles.Count > 0 ? $", {narc.NamedFiles.Count} named" : "")}");
    for (var i = 0; i < narc.Count; i++)
    {
        var data = narc.Get(i).Span;
        var magic = data.Length >= 4 && data[..4].ToArray().All(b => b is >= 0x20 and < 0x7F)
            ? System.Text.Encoding.ASCII.GetString(data[..4])
            : "";
        Console.WriteLine($"  [{i,4}] {data.Length,10:N0}  {magic}");
    }
    return 0;
}

int Scan(string folder)
{
    var library = new RomLibrary(manifest);
    foreach (var r in library.Scan(folder, validator))
        Console.WriteLine($"{(r.AssignedTo is null ? "  " : "✓ ")}{r.Result.Label,-34} {r.Path}{(r.AssignedTo is null ? "" : $"  → {r.AssignedTo}")}");
    PrintRegions(library);
    return 0;
}

int Library()
{
    var library = RomLibrary.Load(AppPaths.RomLibraryFile, manifest);
    Console.WriteLine($"File: {AppPaths.RomLibraryFile}");
    foreach (var e in library.Entries)
        Console.WriteLine($"  {e.GameId,-11} {e.Label,-34} {e.Path ?? "-"}");
    PrintRegions(library);
    return 0;
}

void PrintRegions(RomLibrary library)
{
    Console.WriteLine();
    foreach (var r in library.Regions())
        Console.WriteLine($"  {(r.Available ? "unlocked" : "locked  ")}  {r.Region.Name}{(r.Region.InBaseGame ? "" : " (post-base)")}{(r.Region.RequiredToPlay ? " [required]" : "")}");
    Console.WriteLine(library.CanPlay ? "Ready to play." : "Not playable yet: Black or White is required.");
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

/// <summary>Progress&lt;T&gt; posts to the thread pool; a console wants callbacks inline.</summary>
sealed class SyncProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
