using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace InfiniteRoutes.Core.Config;

/// <summary>A login kept on this computer for one server: its account and session token (never the password).</summary>
public sealed record RememberedLogin(string Address, string? Fingerprint, string Account, string Token, DateTime Expires);

/// <summary>
/// "Remember me": the session token a server gave a "remember" login, per server (address and trusted certificate),
/// in <c>logins.json</c> beside servers.pkir. Tokens are encrypted for the signed-in Windows user (DPAPI); elsewhere
/// they're kept as they are, in the user's own config folder. A server that changed its certificate, or an expired
/// login, is never offered.
/// </summary>
public sealed class RememberedLogins(string path, RememberedLogins.Protector? protector = null)
{
    /// <summary>Turns a token into stored text and back (null when it can't be read back: another user or machine).</summary>
    public sealed record Protector(Func<string, string> Protect, Func<string, string?> Unprotect);

    readonly Protector _protector = protector ?? Default;

    public static RememberedLogins ForUser() => new(Path.Combine(AppPaths.ConfigDir, "logins.json"));

    /// <summary>The live login kept for <paramref name="server"/>, if any.</summary>
    public RememberedLogin? Find(ServerEntry server, DateTime? now = null) =>
        Load().FirstOrDefault(l => l.Address == server.Address) is { } login
        && login.Fingerprint == server.Fingerprint && login.Expires > (now ?? DateTime.UtcNow)
            ? login : null;

    public void Remember(ServerEntry server, string account, string token, DateTime expires) =>
        Save(Load().Where(l => l.Address != server.Address).Append(new RememberedLogin(server.Address, server.Fingerprint, account, token, expires)));

    public void Forget(ServerEntry server)
    {
        var all = Load();
        if (all.Any(l => l.Address == server.Address))
            Save(all.Where(l => l.Address != server.Address));
    }

    List<RememberedLogin> Load()
    {
        var result = new List<RememberedLogin>();
        try
        {
            if (!File.Exists(path))
                return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.GetProperty("logins").EnumerateArray())
            {
                var token = _protector.Unprotect(e.GetProperty("token").GetString() ?? "");
                if (token is null)
                    continue;
                result.Add(new RememberedLogin(e.GetProperty("address").GetString() ?? "",
                    e.TryGetProperty("fingerprint", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null,
                    e.GetProperty("account").GetString() ?? "", token, e.GetProperty("expires").GetDateTime()));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // Unreadable: as if nothing were remembered (the next "remember" rewrites it).
        }
        return result;
    }

    void Save(IEnumerable<RememberedLogin> logins)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartArray("logins");
            foreach (var l in logins)
            {
                w.WriteStartObject();
                w.WriteString("address", l.Address);
                if (l.Fingerprint is null)
                    w.WriteNull("fingerprint");
                else
                    w.WriteString("fingerprint", l.Fingerprint);
                w.WriteString("account", l.Account);
                w.WriteString("token", _protector.Protect(l.Token));
                w.WriteString("expires", l.Expires.ToUniversalTime());
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, stream.ToArray());
        File.Move(temp, path, overwrite: true);
    }

    // ---- Protection ----

    /// <summary>DPAPI on Windows ("dpapi:" + base64); elsewhere the token as it is ("plain:").</summary>
    public static readonly Protector Default = new(
        token => OperatingSystem.IsWindows() && Dpapi.Protect(Encoding.UTF8.GetBytes(token)) is { } sealedBytes
            ? "dpapi:" + Convert.ToBase64String(sealedBytes)
            : "plain:" + token,
        text =>
        {
            if (text.StartsWith("plain:", StringComparison.Ordinal))
                return text["plain:".Length..];
            if (text.StartsWith("dpapi:", StringComparison.Ordinal) && OperatingSystem.IsWindows())
                try
                {
                    return Dpapi.Unprotect(Convert.FromBase64String(text["dpapi:".Length..])) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;
                }
                catch (FormatException)
                {
                    return null;
                }
            return null;
        });

    /// <summary>Windows' per-user data protection (crypt32), without a package for it.</summary>
    static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Blob
        {
            public int Size;
            public IntPtr Data;
        }

        const int UiForbidden = 0x1;

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(ref Blob dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr handle);

        public static byte[]? Protect(byte[] data) => Run(data, protect: true);

        public static byte[]? Unprotect(byte[] data) => Run(data, protect: false);

        static byte[]? Run(byte[] data, bool protect)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
                var ok = protect
                    ? CryptProtectData(ref input, "Infinite Routes login", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
                if (!ok)
                    return null;
                try
                {
                    var result = new byte[output.Size];
                    Marshal.Copy(output.Data, result, 0, output.Size);
                    return result;
                }
                finally
                {
                    LocalFree(output.Data);
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
            finally
            {
                handle.Free();
            }
        }
    }
}
