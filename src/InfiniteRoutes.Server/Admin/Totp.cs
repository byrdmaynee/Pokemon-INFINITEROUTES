using System.Security.Cryptography;
using System.Text;

namespace InfiniteRoutes.Server.Admin;

/// <summary>
/// Time-based one-time codes (RFC 6238: HMAC-SHA1, 30-second steps, 6 digits), as authenticator apps make them. A code
/// is accepted for the current step and one either side (clock drift), and never twice.
/// </summary>
public static class Totp
{
    public const int Step = 30, Digits = 6;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long StepAt(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds() / Step;

    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6");
    }

    /// <summary>The step <paramref name="code"/> is valid for (now ± one step, after <paramref name="lastUsed"/>), or null.</summary>
    public static long? Verify(byte[] secret, string code, DateTime utc, long lastUsed)
    {
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (code.Length != Digits)
            return null;
        var now = StepAt(utc);
        for (var step = now - 1; step <= now + 1; step++)
            if (step > lastUsed && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(code)))
                return step;
        return null;
    }

    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Base32 (RFC 4648, no padding): how authenticator apps take a secret typed in.</summary>
    public static string Base32(byte[] data)
    {
        var text = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    /// <summary>The otpauth:// link authenticator apps read (from a QR code or pasted).</summary>
    public static string Uri(byte[] secret, string account, string issuer) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}?secret={Base32(secret)}&issuer={System.Uri.EscapeDataString(issuer)}&digits={Digits}&period={Step}";
}
