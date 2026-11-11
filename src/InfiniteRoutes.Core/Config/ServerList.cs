using System.Text;
using Tomlyn.Model;

namespace InfiniteRoutes.Core.Config;

/// <summary>
/// A server the player can connect to: a name, the host and its accounts API port, and the certificate fingerprint
/// they trusted on first connecting (null until then).
/// </summary>
public sealed record ServerEntry(string Name, string Host, int ApiPort, string? Fingerprint = null)
{
    public const int DefaultApiPort = 7451;

    public string Address => $"{Host}:{ApiPort}";

    /// <summary>Reads "host", "host:port" or "[v6]:port"; null when it isn't an address.</summary>
    public static (string Host, int Port)? ParseAddress(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Contains(' '))
            return null;
        if (text.StartsWith('['))
        {
            var end = text.IndexOf(']');
            if (end < 0)
                return null;
            var host = text[1..end];
            var rest = text[(end + 1)..];
            return rest.Length == 0 ? (host, DefaultApiPort)
                : rest.StartsWith(':') && int.TryParse(rest[1..], out var p6) && p6 is > 0 and < 65536 ? (host, p6) : null;
        }
        var colon = text.LastIndexOf(':');
        if (colon < 0)
            return (text, DefaultApiPort);
        return int.TryParse(text[(colon + 1)..], out var port) && port is > 0 and < 65536 && colon > 0 ? (text[..colon], port) : null;
    }
}

/// <summary>
/// The player's server list, saved in <c>servers.pkir</c> (BUILD_SPEC §4.5) as <c>[[server]]</c> tables with name,
/// address and fingerprint. A missing or empty file gives the local test server.
/// </summary>
public static class ServerList
{
    public static readonly ServerEntry LocalTest = new("Local test server", "127.0.0.1", ServerEntry.DefaultApiPort);

    public static List<ServerEntry> Load(string path)
    {
        var root = Pkir.LoadOrEmpty(path);
        var list = new List<ServerEntry>();
        if (root.TryGetValue("server", out var value) && value is TomlTableArray servers)
            foreach (var t in servers)
                if (t.GetString("address") is { } address && ServerEntry.ParseAddress(address) is { } parsed)
                    list.Add(new ServerEntry(t.GetString("name") ?? address, parsed.Host, parsed.Port,
                        t.GetString("fingerprint") is { Length: > 0 } f ? f : null));
        if (list.Count == 0)
            list.Add(LocalTest);
        return list;
    }

    public static void Save(string path, IEnumerable<ServerEntry> servers)
    {
        var text = new StringBuilder("# Infinite Routes — your servers. The fingerprint is the server certificate you trusted.\n");
        foreach (var s in servers)
        {
            var host = s.Host.Contains(':') ? $"[{s.Host}]" : s.Host;
            text.Append("\n[[server]]\n")
                .Append("name = ").Append(Pkir.Quote(s.Name)).Append('\n')
                .Append("address = ").Append(Pkir.Quote($"{host}:{s.ApiPort}")).Append('\n');
            if (s.Fingerprint is not null)
                text.Append("fingerprint = ").Append(Pkir.Quote(s.Fingerprint)).Append('\n');
        }
        Pkir.WriteAtomic(path, text.ToString());
    }
}
