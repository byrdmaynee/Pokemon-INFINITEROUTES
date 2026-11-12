namespace InfiniteRoutes.Extractor.Nds;

/// <summary>
/// Read-only view of a ROM's NitroFS filesystem. Not thread-safe: reads share one stream.
/// </summary>
public sealed class NitroFs : IDisposable
{
    readonly Stream _stream;
    readonly bool _ownsStream;
    readonly Dictionary<string, NitroFileEntry> _byPath;

    public NdsHeader Header { get; }

    /// <summary>Named files only. Overlays and other unnamed allocation entries are not listed.</summary>
    public IReadOnlyList<NitroFileEntry> Files { get; }

    NitroFs(Stream stream, bool ownsStream, NdsHeader header, List<NitroFileEntry> files)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        Header = header;
        Files = files;
        _byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
    }

    /// <exception cref="InvalidDataException">The header or file tables are malformed.</exception>
    public static NitroFs Open(Stream stream, bool ownsStream = true)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("NitroFS needs a seekable stream.", nameof(stream));

        var header = NdsHeader.Read(stream);
        var length = stream.Length;
        CheckRange("file name table", header.FntOffset, header.FntSize, length);
        CheckRange("file allocation table", header.FatOffset, header.FatSize, length);

        var fat = NitroFileTable.DecodeAllocationTable(ReadAt(stream, header.FatOffset, header.FatSize));
        var files = NitroFileTable.Decode(ReadAt(stream, header.FntOffset, header.FntSize), fat);
        foreach (var f in files)
            CheckRange($"file '{f.Path}'", f.Start, f.Length, length);

        return new NitroFs(stream, ownsStream, header, files);
    }

    public bool Exists(string path) => _byPath.ContainsKey(Normalize(path));

    public NitroFileEntry? Find(string path) => _byPath.GetValueOrDefault(Normalize(path));

    public byte[] ReadFile(string path) =>
        ReadFile(Find(path) ?? throw new FileNotFoundException($"'{path}' is not in the ROM filesystem."));

    public byte[] ReadFile(NitroFileEntry entry) => ReadAt(_stream, entry.Start, entry.Length);

    /// <summary>The ARM9 program, decompressed (header 0x20: ROM offset, 0x2C: size).</summary>
    public byte[] ReadArm9()
    {
        var header = ReadAt(_stream, 0, NdsHeader.Size);
        var raw = ReadAt(_stream, BitConverter.ToUInt32(header, 0x20), BitConverter.ToUInt32(header, 0x2C));
        return Blz.Decompress(raw);
    }

    /// <summary>
    /// The ARM9 overlays, decompressed. Header 0x50/0x54: the overlay table (32-byte entries: id, RAM address, sizes,
    /// static initialisers, file id, then the compressed size in the low 24 bits with bit 24 set when compressed).
    /// </summary>
    public IEnumerable<byte[]> ReadOverlays()
    {
        var header = ReadAt(_stream, 0, NdsHeader.Size);
        uint tableOffset = BitConverter.ToUInt32(header, 0x50), tableSize = BitConverter.ToUInt32(header, 0x54);
        uint fatOffset = BitConverter.ToUInt32(header, 0x48);
        if (tableSize == 0 || tableOffset + tableSize > _stream.Length)
            yield break;
        var table = ReadAt(_stream, tableOffset, tableSize);
        for (var e = 0; e + 32 <= table.Length; e += 32)
        {
            var fileId = BitConverter.ToUInt32(table, e + 24);
            var flags = BitConverter.ToUInt32(table, e + 28);
            var fat = ReadAt(_stream, fatOffset + fileId * 8, 8);
            uint start = BitConverter.ToUInt32(fat, 0), end = BitConverter.ToUInt32(fat, 4);
            if (end <= start || end > _stream.Length)
                continue;
            var data = ReadAt(_stream, start, end - start);
            yield return (flags & 0x01000000) != 0 ? Blz.Decompress(data) : data;
        }
    }

    /// <summary>Immediate children of a directory ("" = root). Directories end with '/'.</summary>
    public IEnumerable<string> List(string directory = "")
    {
        var prefix = Normalize(directory);
        if (prefix.Length > 0 && !prefix.EndsWith('/'))
            prefix += "/";

        return Files
            .Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(f =>
            {
                var rest = f.Path[prefix.Length..];
                var slash = rest.IndexOf('/');
                return slash < 0 ? rest : rest[..(slash + 1)];
            })
            .Distinct()
            .Order(StringComparer.Ordinal);
    }

    public void Dispose()
    {
        if (_ownsStream)
            _stream.Dispose();
    }

    static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    static byte[] ReadAt(Stream s, uint offset, uint size)
    {
        var buf = new byte[size];
        s.Position = offset;
        s.ReadExactly(buf);
        return buf;
    }

    static void CheckRange(string what, uint offset, uint size, long length)
    {
        if ((long)offset + size > length)
            throw new InvalidDataException($"The {what} points past the end of the ROM (truncated or corrupt file).");
    }
}
