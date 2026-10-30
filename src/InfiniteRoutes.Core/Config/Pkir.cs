using System.Globalization;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace InfiniteRoutes.Core.Config;

/// <summary>
/// Reads and writes <c>.pkir</c> files: Infinite Routes config files using TOML syntax.
/// </summary>
public static class Pkir
{
    public const string Extension = ".pkir";

    public static TomlTable Parse(string text, string sourceName = "<pkir>")
    {
        try
        {
            return TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
        }
        catch (TomlException e)
        {
            throw new PkirException($"{sourceName}: {e.Message}", e);
        }
    }

    /// <summary>Loads a file, or returns an empty table if it does not exist.</summary>
    public static TomlTable LoadOrEmpty(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path, Encoding.UTF8), path) : new TomlTable();

    /// <summary>Writes atomically: temp file + replace, so a crash never leaves a half-written config.</summary>
    public static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Formats a string as a TOML value. Uses a literal string ('...') when possible so Windows
    /// paths stay readable for people editing the file by hand.
    /// </summary>
    public static string Quote(string value)
    {
        if (!value.Contains('\'') && !value.Any(char.IsControl))
            return "'" + value + "'";

        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    public static TomlTable? GetTable(this TomlTable table, string key) =>
        table.TryGetValue(key, out var v) ? v as TomlTable : null;

    public static string? GetString(this TomlTable table, string key) =>
        table.TryGetValue(key, out var v) ? v as string : null;

    public static long? GetInt(this TomlTable table, string key) =>
        table.TryGetValue(key, out var v) && v is long l ? l : null;

    public static bool? GetBool(this TomlTable table, string key) =>
        table.TryGetValue(key, out var v) && v is bool b ? b : null;
}

public sealed class PkirException(string message, Exception? inner = null) : Exception(message, inner);
