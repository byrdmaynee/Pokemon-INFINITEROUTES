using System.Security.Cryptography;
using InfiniteRoutes.Extractor.Nds;

namespace InfiniteRoutes.Extractor.Roms;

public sealed record RomValidationResult(
    RomStatus Status,
    string Message,
    GameDefinition? Game = null,
    NdsHeader? Header = null,
    string? Sha1 = null,
    bool HashVerified = false)
{
    public string Label => RomStatusText.Label(Status, HashVerified);
}

/// <summary>
/// Checks a ROM file against the manifest: header → game/region → header CRC → filesystem and
/// required files → SHA-1 against known dumps. Cheap checks run first so bad files fail fast.
/// </summary>
public sealed class RomValidator(RomManifest manifest)
{
    public RomManifest Manifest => manifest;

    /// <param name="progress">Reports 0..1 while hashing, the slow step.</param>
    public RomValidationResult Validate(string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Stream stream;
        try
        {
            stream = RomSource.OpenSeekable(path);
        }
        catch (InvalidDataException e)
        {
            return new(RomStatus.NotSupportedGame, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(RomStatus.Unreadable, e.Message);
        }

        using (stream)
        {
            try
            {
                return Validate(stream, progress, ct);
            }
            catch (IOException e)
            {
                return new(RomStatus.Unreadable, e.Message);
            }
        }
    }

    public RomValidationResult Validate(Stream stream, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        NdsHeader header;
        try
        {
            header = NdsHeader.Read(stream);
        }
        catch (InvalidDataException e)
        {
            return new(RomStatus.NotSupportedGame, e.Message);
        }

        var game = manifest.FindByBaseCode(header.BaseCode);
        if (game is null)
            return new(RomStatus.NotSupportedGame,
                $"'{header.Title}' ({header.GameCode}) is not one of the supported games.", Header: header);

        if (!game.RegionCodes.Contains(header.RegionCode.ToString()))
            return new(RomStatus.UnsupportedRegion,
                $"{game.Name} region/language '{header.RegionCode}' isn't supported yet (supported: {string.Join(", ", game.RegionCodes)}).",
                game, header);

        if (!header.HeaderCrcValid)
            return new(RomStatus.Corrupt,
                $"Header checksum mismatch (stored {header.StoredHeaderCrc:X4}, computed {header.ComputedHeaderCrc:X4}).",
                game, header);

        var structureError = CheckStructure(stream, game);
        if (structureError is not null)
            return new(RomStatus.Corrupt, structureError, game, header);

        var sha1 = ComputeSha1(stream, progress, ct);
        var dumpsForRegion = game.KnownDumps
            .Where(d => d.RegionCode == header.RegionCode.ToString())
            .ToList();

        if (dumpsForRegion.Count == 0)
            return new(RomStatus.Valid,
                $"{game.Name} ({header.GameCode}, rev {header.Revision}). No verified dump list for this region yet, so the SHA-1 isn't checked.",
                game, header, sha1, HashVerified: false);

        if (dumpsForRegion.Any(d => d.Sha1.Equals(sha1, StringComparison.OrdinalIgnoreCase)))
            return new(RomStatus.Valid, $"{game.Name} ({header.GameCode}, rev {header.Revision}), verified clean dump.",
                game, header, sha1, HashVerified: true);

        return new(RomStatus.UnknownDump,
            $"This is {game.Name}, but the file doesn't match any known clean dump (bad dump, ROM hack, or trimmed file).",
            game, header, sha1);
    }

    static string? CheckStructure(Stream stream, GameDefinition game)
    {
        NitroFs fs;
        try
        {
            fs = NitroFs.Open(stream, ownsStream: false);
        }
        catch (InvalidDataException e)
        {
            return $"Filesystem is unreadable: {e.Message}";
        }

        foreach (var required in game.RequiredFiles)
        {
            if (fs.Find(required) is not { } entry)
                return $"Expected file '{required}' is missing — this may be a ROM hack or a different game.";
            var bytes = fs.ReadFile(entry);
            if (!Narc.IsNarc(bytes))
                return $"'{required}' is not a valid archive — this may be a ROM hack or a bad dump.";
            try
            {
                Narc.Parse(bytes);
            }
            catch (InvalidDataException e)
            {
                return $"'{required}' is damaged: {e.Message}";
            }
        }
        return null;
    }

    static string ComputeSha1(Stream stream, IProgress<double>? progress, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[1 << 20];
        var total = Math.Max(1, stream.Length);
        long done = 0;
        stream.Position = 0;
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, read);
            done += read;
            progress?.Report((double)done / total);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
