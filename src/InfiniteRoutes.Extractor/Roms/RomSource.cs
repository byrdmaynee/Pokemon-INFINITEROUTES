using System.IO.Compression;

namespace InfiniteRoutes.Extractor.Roms;

/// <summary>Opens a ROM file the player picked as a seekable stream, reading inside .zip archives in memory.</summary>
public static class RomSource
{
    public static readonly IReadOnlyList<string> SupportedExtensions = [".nds", ".zip"];

    public static bool HasSupportedExtension(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="InvalidDataException">The archive has no .nds file in it.</exception>
    public static Stream OpenSeekable(string path)
    {
        if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16);

        using var zip = ZipFile.OpenRead(path);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nds", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The archive does not contain a .nds file.");

        // Zip entries aren't seekable, and the spec forbids unpacking to disk unasked, so read into memory.
        var memory = new MemoryStream(checked((int)entry.Length));
        using (var s = entry.Open())
            s.CopyTo(memory);
        memory.Position = 0;
        return memory;
    }
}
