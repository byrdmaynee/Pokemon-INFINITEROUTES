namespace InfiniteRoutes.Extractor.Roms;

public enum RomStatus
{
    /// <summary>No file assigned.</summary>
    Missing,
    /// <summary>A path is assigned but hasn't been validated since it last changed.</summary>
    Unchecked,
    /// <summary>Passed every check. Needs extraction before it can be played (M1).</summary>
    Valid,
    /// <summary>The right game, but not a known clean dump (bad dump, hack, or trimmed).</summary>
    UnknownDump,
    /// <summary>The right game, but a region/language the extractor doesn't support yet.</summary>
    UnsupportedRegion,
    /// <summary>A DS ROM, but not one of the supported games — or not a DS ROM at all.</summary>
    NotSupportedGame,
    /// <summary>Header checksum or filesystem is broken.</summary>
    Corrupt,
    /// <summary>The file couldn't be opened or read.</summary>
    Unreadable,
}

public static class RomStatusText
{
    public static string Label(RomStatus status, bool hashVerified = true) => status switch
    {
        RomStatus.Missing => "Missing",
        RomStatus.Unchecked => "Not checked yet",
        RomStatus.Valid => hashVerified ? "Valid" : "Valid (dump not yet verified)",
        RomStatus.UnknownDump => "Recognised game, unknown dump",
        RomStatus.UnsupportedRegion => "Unsupported region/language",
        RomStatus.NotSupportedGame => "Not a supported game",
        RomStatus.Corrupt => "Corrupt or modified ROM",
        RomStatus.Unreadable => "Can't read file",
        _ => status.ToString(),
    };

    public static bool IsUsable(RomStatus status) => status == RomStatus.Valid;
}
