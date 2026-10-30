using System.Globalization;
using System.Text;
using InfiniteRoutes.Core.Config;

namespace InfiniteRoutes.Extractor.Roms;

public sealed class RomLibraryEntry
{
    public required string GameId { get; init; }
    public string? Path { get; set; }
    public RomStatus Status { get; set; } = RomStatus.Missing;
    public string Message { get; set; } = "";
    public string? Sha1 { get; set; }
    public bool HashVerified { get; set; }

    /// <summary>Full 4-character cartridge code, e.g. IRAO. Part of the extraction cache path.</summary>
    public string? GameCode { get; set; }

    /// <summary>Path + size + last-write time when last validated. A mismatch means "check again".</summary>
    public string? Fingerprint { get; set; }

    public string Label => RomStatusText.Label(Status, HashVerified);
}

public sealed record RegionAvailability(RegionDefinition Region, bool Available, string? ProvidedBy);

public sealed record ScanResult(string Path, RomValidationResult Result, string? AssignedTo);

/// <summary>
/// The player's ROM assignments, one slot per supported game, stored in <c>roms.pkir</c>.
/// Hashing is slow, so on load an entry keeps its last result while the file looks unchanged
/// and drops to <see cref="RomStatus.Unchecked"/> otherwise.
/// </summary>
public sealed class RomLibrary
{
    readonly Dictionary<string, RomLibraryEntry> _entries;

    public RomManifest Manifest { get; }
    public bool WizardCompleted { get; set; }

    public RomLibrary(RomManifest manifest)
    {
        Manifest = manifest;
        _entries = manifest.Games.ToDictionary(g => g.Id, g => new RomLibraryEntry { GameId = g.Id }, StringComparer.OrdinalIgnoreCase);
    }

    public IEnumerable<RomLibraryEntry> Entries => Manifest.Games.Select(g => _entries[g.Id]);

    public RomLibraryEntry this[string gameId] => _entries[gameId];

    public void Assign(string gameId, string path, RomValidationResult result)
    {
        var e = _entries[gameId];
        e.Path = path;
        e.Status = result.Status;
        e.Message = result.Message;
        e.Sha1 = result.Sha1;
        e.HashVerified = result.HashVerified;
        e.GameCode = result.Header?.GameCode;
        e.Fingerprint = Fingerprint(path);
    }

    public void Remove(string gameId)
    {
        var e = _entries[gameId];
        e.Path = e.Sha1 = e.Fingerprint = e.GameCode = null;
        e.Status = RomStatus.Missing;
        e.Message = "";
        e.HashVerified = false;
    }

    /// <summary>
    /// Validates a file the player picked for a specific slot. The file is only assigned if it
    /// turns out to be that game (or the other version of a pair, which is moved to its own slot).
    /// </summary>
    public RomValidationResult AssignFromFile(string gameId, string path, RomValidator validator,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var result = validator.Validate(path, progress, ct);
        var targetId = result.Game?.Id ?? gameId;
        Assign(targetId, path, result);
        return result;
    }

    /// <summary>Finds every supported ROM under a folder and fills empty or unusable slots.</summary>
    public List<ScanResult> Scan(string folder, RomValidator validator, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 3,
                IgnoreInaccessible = true,
            })
            .Where(RomSource.HasSupportedExtension)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<ScanResult>();
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((i, files.Count));
            var result = validator.Validate(files[i], null, ct);
            string? assigned = null;
            if (result is { Status: RomStatus.Valid, Game: { } game } && !RomStatusText.IsUsable(_entries[game.Id].Status))
            {
                Assign(game.Id, files[i], result);
                assigned = game.Id;
            }
            results.Add(new ScanResult(files[i], result, assigned));
        }
        progress?.Report((files.Count, files.Count));
        return results;
    }

    public IReadOnlyList<RegionAvailability> Regions() =>
        Manifest.Regions.Select(r =>
        {
            var provider = r.AnyOf.FirstOrDefault(id => _entries.TryGetValue(id, out var e) && RomStatusText.IsUsable(e.Status));
            return new RegionAvailability(r, provider is not null, provider);
        }).ToList();

    /// <summary>True when every region marked <c>requiredToPlay</c> has a usable ROM.</summary>
    public bool CanPlay => Regions().Where(r => r.Region.RequiredToPlay).All(r => r.Available);

    public static RomLibrary Load(string file, RomManifest manifest)
    {
        var library = new RomLibrary(manifest);
        var root = Pkir.LoadOrEmpty(file);
        library.WizardCompleted = root.GetTable("wizard")?.GetBool("completed") ?? false;

        var roms = root.GetTable("roms");
        if (roms is null)
            return library;

        foreach (var (gameId, value) in roms)
        {
            if (value is not Tomlyn.Model.TomlTable t || !library._entries.TryGetValue(gameId, out var e))
                continue;
            e.Path = t.GetString("path");
            if (string.IsNullOrEmpty(e.Path))
            {
                e.Path = null;
                continue;
            }

            e.Sha1 = t.GetString("sha1");
            e.GameCode = t.GetString("game_code");
            e.HashVerified = t.GetBool("verified") ?? false;
            e.Fingerprint = t.GetString("fingerprint");
            var stored = Enum.TryParse<RomStatus>(t.GetString("status"), out var s) ? s : RomStatus.Unchecked;

            if (!File.Exists(e.Path))
            {
                e.Status = RomStatus.Unreadable;
                e.Message = "File not found. It may have been moved, renamed, or be on a drive that isn't connected.";
            }
            else if (e.Fingerprint is null || e.Fingerprint != Fingerprint(e.Path))
            {
                e.Status = RomStatus.Unchecked;
                e.Message = "The file changed since it was last checked.";
            }
            else if (stored == RomStatus.Valid && e.GameCode is null)
            {
                // Written by an older client that didn't record the game code; one re-check fills it in.
                e.Status = RomStatus.Unchecked;
                e.Message = "Needs a quick re-check.";
            }
            else
            {
                e.Status = stored;
                e.Message = t.GetString("message") ?? "";
            }
        }
        return library;
    }

    public void Save(string file)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Infinite Routes — ROM library.");
        sb.AppendLine("# Managed by the game (Settings → ROM Library). You can edit 'path' by hand;");
        sb.AppendLine("# the game re-checks any file that changed.");
        sb.AppendLine();
        sb.AppendLine("[wizard]");
        sb.AppendLine($"completed = {(WizardCompleted ? "true" : "false")}");

        foreach (var e in Entries.Where(e => e.Path is not null))
        {
            sb.AppendLine();
            sb.AppendLine($"[roms.{e.GameId}]");
            sb.AppendLine($"path = {Pkir.Quote(e.Path!)}");
            sb.AppendLine($"status = {Pkir.Quote(e.Status.ToString())}");
            sb.AppendLine($"message = {Pkir.Quote(e.Message)}");
            if (e.Sha1 is not null)
                sb.AppendLine($"sha1 = {Pkir.Quote(e.Sha1)}");
            if (e.GameCode is not null)
                sb.AppendLine($"game_code = {Pkir.Quote(e.GameCode)}");
            sb.AppendLine($"verified = {(e.HashVerified ? "true" : "false")}");
            if (e.Fingerprint is not null)
                sb.AppendLine($"fingerprint = {Pkir.Quote(e.Fingerprint)}");
        }

        Pkir.WriteAtomic(file, sb.ToString());
    }

    static string? Fingerprint(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            return string.Create(CultureInfo.InvariantCulture,
                $"{System.IO.Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc:O}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
