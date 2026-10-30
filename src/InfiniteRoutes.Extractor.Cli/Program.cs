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
      ir-extract extract <rom>           Validate, then extract assets into the local cache
      ir-extract bmd <rom> <narc> <i> <outdir>      Export a self-textured BMD0 model to glTF
      ir-extract map <rom> <chunk> <area> <outdir>  Export a Black/White map chunk to glTF
      ir-extract text <rom> <narc> <i>   Decode a Gen 5 message file from a NARC
      ir-extract sprite <rom> <narc> <i> <pal-i> <width> <out.png>  Render an NCGR with an NCLR from the same NARC
      ir-extract hex <rom> <narc> <i> [n]  Hex dump the first n bytes (default 256) of a NARC member
    """;

Console.OutputEncoding = System.Text.Encoding.UTF8;

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
        "extract" when args.Length >= 2 => Extract(args[1]),
        "bmd" when args.Length >= 5 => BmdExport(args[1], args[2], int.Parse(args[3]), args[4]),
        "map" when args.Length >= 5 => MapExport(args[1], int.Parse(args[2]), int.Parse(args[3]), args[4]),
        "text" when args.Length >= 4 => Text(args[1], args[2], int.Parse(args[3])),
        "sprite" when args.Length >= 7 => Sprite(args[1], args[2], int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]), args[6]),
        "hex" when args.Length >= 4 => Hex(args[1], args[2], int.Parse(args[3]), args.Length >= 5 ? int.Parse(args[4]) : 256),
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

int Extract(string path)
{
    var r = validator.Validate(path);
    if (r.Status != RomStatus.Valid)
        return Fail($"{r.Label}: {r.Message}");
    if (!InfiniteRoutes.Extractor.Extraction.Gen5Extractor.Supports(r.Game!))
        return Fail($"Extraction for {r.Game!.Name} isn't available yet (Black/White only in M1a).");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var last = "";
    var dir = InfiniteRoutes.Extractor.Extraction.Gen5Extractor.Extract(path, r.Game!, r.Header!.GameCode, r.Sha1!,
        new InlineProgress<InfiniteRoutes.Extractor.Extraction.ExtractionProgress>(p =>
        {
            if (p.Step != last) Console.Error.Write((char)13 + p.Step.PadRight(50));
            last = p.Step;
        }));
    Console.Error.WriteLine();
    Console.WriteLine($"Extracted to {dir} in {sw.Elapsed.TotalSeconds:F1}s");
    return 0;
}

int BmdExport(string rom, string narcPath, int index, string outDir)
{
    using var fs = NitroFs.Open(RomSource.OpenSeekable(rom));
    var bytes = Lz.DecompressIfNeeded(Narc.Parse(fs.ReadFile(narcPath)).Read(index));
    var model = InfiniteRoutes.Extractor.Nitro3D.Nsbmd.Decode(bytes);
    var tex = model.Textures;
    var all = model.Meshes.SelectMany(m => m.Vertices).Select(v => v.Position).ToList();
    Console.WriteLine($"{narcPath}[{index}] '{model.Name}': {model.Materials.Count} materials, {all.Count / 3} triangles, bounds {all.Aggregate(System.Numerics.Vector3.Min)} .. {all.Aggregate(System.Numerics.Vector3.Max)}, {tex?.Textures.Count ?? 0} textures");
    var texDir = Path.Combine(outDir, "textures");
    Directory.CreateDirectory(texDir);
    string? Uri(InfiniteRoutes.Extractor.Nitro3D.NitroMaterial m)
    {
        if (tex is null || m.Texture is null || !tex.Textures.TryGetValue(m.Texture, out var t)) return null;
        var file = $"{m.Texture}__{m.Palette}.png";
        InfiniteRoutes.Extractor.Graphics.Png.Save(tex.Decode(t, m.Palette), Path.Combine(texDir, file));
        return "textures/" + file;
    }
    (int, int) Size(InfiniteRoutes.Extractor.Nitro3D.NitroMaterial m) => tex is not null && m.Texture is not null && tex.Textures.TryGetValue(m.Texture, out var t) ? (t.Width, t.Height) : (1, 1);
    bool Alpha(InfiniteRoutes.Extractor.Nitro3D.NitroMaterial m) => tex is not null && m.Texture is not null && tex.Textures.TryGetValue(m.Texture, out var t) && t.HasAlpha;
    InfiniteRoutes.Extractor.Nitro3D.Gltf.Save(InfiniteRoutes.Extractor.Nitro3D.Gltf.Build(model, 1f / 16, System.Numerics.Vector3.Zero, Uri, Size, Alpha),
        Path.Combine(outDir, $"model{index}.gltf"));
    return 0;
}

int MapExport(string rom, int chunk, int area, string outDir)
{
    using var fs = NitroFs.Open(RomSource.OpenSeekable(rom));
    var map = InfiniteRoutes.Extractor.Gen5.Gen5Map.Parse(Narc.Parse(fs.ReadFile("a/0/0/8")).Read(chunk));
    var areaTable = fs.ReadFile("a/0/1/3");
    var texSet = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(areaTable.AsSpan(area * 10 + 2));
    var tex = InfiniteRoutes.Extractor.Nitro3D.Tex0.FromFile(Narc.Parse(fs.ReadFile("a/0/1/4")).Read(texSet))!;
    var model = InfiniteRoutes.Extractor.Nitro3D.Nsbmd.Decode(map.Model);

    var all = model.Meshes.SelectMany(m => m.Vertices).Select(v => v.Position).ToList();
    var min = all.Aggregate(System.Numerics.Vector3.Min);
    var max = all.Aggregate(System.Numerics.Vector3.Max);
    Console.WriteLine($"chunk {chunk} ({map.Magic}): model '{model.Name}', {model.Materials.Count} materials, {model.Meshes.Count} meshes, {all.Count / 3} triangles");
    Console.WriteLine($"bounds {min} .. {max}; texture set {texSet} ({tex.Textures.Count} textures, {tex.PaletteNames.Count} palettes); {map.Buildings.Count} buildings; collision {(map.Collision is null ? "none" : "32x32")}");

    var texDir = Path.Combine(outDir, "textures");
    Directory.CreateDirectory(texDir);
    var missing = new HashSet<string>();
    string? Uri(InfiniteRoutes.Extractor.Nitro3D.NitroMaterial m)
    {
        if (m.Texture is null || !tex.Textures.TryGetValue(m.Texture, out var t)) { if (m.Texture is not null) missing.Add(m.Texture); return null; }
        var file = $"{m.Texture}__{m.Palette}.png";
        var path = Path.Combine(texDir, file);
        if (!File.Exists(path)) InfiniteRoutes.Extractor.Graphics.Png.Save(tex.Decode(t, m.Palette), path);
        return "textures/" + file;
    }
    (int, int) Size(InfiniteRoutes.Extractor.Nitro3D.NitroMaterial m) => m.Texture is not null && tex.Textures.TryGetValue(m.Texture, out var t) ? (t.Width, t.Height) : (1, 1);
    bool Alpha(InfiniteRoutes.Extractor.Nitro3D.NitroMaterial m) => m.Texture is not null && tex.Textures.TryGetValue(m.Texture, out var t) && t.HasAlpha;

    var gltf = InfiniteRoutes.Extractor.Nitro3D.Gltf.Build(model, 1f / 16, System.Numerics.Vector3.Zero, Uri, Size, Alpha);
    InfiniteRoutes.Extractor.Nitro3D.Gltf.Save(gltf, Path.Combine(outDir, $"chunk{chunk}.gltf"));
    if (missing.Count > 0) Console.WriteLine($"missing textures: {string.Join(", ", missing)}");

    if (map.Collision is not null)
    {
        var img = new InfiniteRoutes.Extractor.Graphics.Rgba(32, 32);
        var seen = new SortedDictionary<string, int>();
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x++)
        {
            var t = map.Tile(x, y);
            var key = $"{t.Terrain:X4}/{t.Behaviour:X4}/{t.Collision:X4}/{t.Flags:X4}";
            seen[key] = seen.GetValueOrDefault(key) + 1;
            img.Set(x, y, map.IsBlocked(x, y) ? 0x802020FFu : 0xD0E0C0FFu);
        }
        InfiniteRoutes.Extractor.Graphics.Png.Save(img, Path.Combine(outDir, $"chunk{chunk}-collision.png"));
        Console.WriteLine("tile values: " + string.Join("  ", seen.Select(kv => $"{kv.Key}x{kv.Value}")));
    }
    foreach (var bld in map.Buildings.Take(5))
        Console.WriteLine($"  building {bld}");
    return 0;
}

byte[] NarcMember(string rom, string narcPath, int index)
{
    using var fs = NitroFs.Open(RomSource.OpenSeekable(rom));
    return Narc.Parse(fs.ReadFile(narcPath)).Read(index);
}

int Text(string rom, string narcPath, int index)
{
    var lines = InfiniteRoutes.Extractor.Text.Gen5Text.Decode(NarcMember(rom, narcPath, index));
    for (var i = 0; i < lines.Count; i++)
        Console.WriteLine($"[{i,4}] {lines[i].Replace("\n", "⏎").Replace("\r", "⤵").Replace("\f", "⏏")}");
    return 0;
}

int Sprite(string rom, string narcPath, int index, int paletteIndex, int width, string output)
{
    using var fs = NitroFs.Open(RomSource.OpenSeekable(rom));
    var narc = Narc.Parse(fs.ReadFile(narcPath));
    var ncgr = InfiniteRoutes.Extractor.Graphics.Ncgr.Parse(Lz.DecompressIfNeeded(narc.Read(index)));
    var nclr = InfiniteRoutes.Extractor.Graphics.Nclr.Parse(Lz.DecompressIfNeeded(narc.Read(paletteIndex)));
    Console.WriteLine($"{ncgr.BitsPerPixel}bpp, {ncgr.Indices.Length} px, tiles {ncgr.TilesWide}x{ncgr.TilesHigh}, {(ncgr.Tiled ? "tiled" : "linear")}; palette {nclr.Colors.Length} colours");
    InfiniteRoutes.Extractor.Graphics.Png.Save(width == 0 ? ncgr.RenderGen5Battler(nclr) : ncgr.Render(nclr, width), output);
    Console.WriteLine($"Wrote {output}");
    return 0;
}

int Hex(string rom, string narcPath, int index, int count)
{
    var data = NarcMember(rom, narcPath, index);
    if (Lz.IsCompressed(data))
    {
        Console.WriteLine($"{narcPath}[{index}]: {data.Length:N0} bytes, LZ{data[0]:X2}-compressed; showing decompressed");
        data = Lz.Decompress(data);
    }
    Console.WriteLine($"{narcPath}[{index}]: {data.Length:N0} bytes");
    for (var off = 0; off < Math.Min(count, data.Length); off += 16)
    {
        var row = data.AsSpan(off, Math.Min(16, data.Length - off));
        var hex = string.Join(" ", row.ToArray().Select(b => b.ToString("X2")));
        var ascii = new string(row.ToArray().Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
        Console.WriteLine($"{off:X6}  {hex,-47}  {ascii}");
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

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
