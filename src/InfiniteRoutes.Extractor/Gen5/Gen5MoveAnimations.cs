using InfiniteRoutes.Extractor.Extraction;
using InfiniteRoutes.Extractor.Nds;
using InfiniteRoutes.Extractor.Nitro3D;
using static InfiniteRoutes.Extractor.Nitro3D.NitroDict;

namespace InfiniteRoutes.Extractor.Gen5;

/// <summary>
/// The battle effect scripts (<see cref="Gen5Layout.MoveAnimations"/>; file n is move n's animation) and the
/// particle files they load (<see cref="Gen5Layout.MoveParticles"/>).
/// </summary>
/// <remarks>
/// A script file is u32 1, then 14 u32 entry offsets (all the same in practice), then commands: a u16 opcode and that
/// opcode's fixed number of u32 arguments, packed with no padding, ending with <see cref="End"/>. The interpreter is in
/// overlay 94 (a 78-entry handler table at 0x02209EA8); <see cref="ArgCounts"/> is how many arguments each handler reads.
/// The commands the client plays (see BattleEffects there):
/// <list type="bullet">
/// <item>0x00 camera move, 0x38 wait for the camera and effects, 0x39 wait n frames;</item>
/// <item>0x06 load particle file, 0x07 start one of its emitters at a place, 0x0C start one aimed from one place to another;</item>
/// <item>0x12/0x15 move or shake a Pokémon, 0x1B tint one, 0x2A fade the background.</item>
/// </list>
/// Places: 0–7 a battle position, 8 "the same as the start", 9/10 the attacker, 11/12 the defender (12: every target).
/// Battlers: 14 the attacker, 15 its partner, 16 the defender. Sound commands (0x34, 0x36, 0x37, 0x3A) are kept but not played.
/// </remarks>
public static class Gen5MoveAnimations
{
    public const int End = 0x4D;

    public static readonly int[] ArgCounts =
    [
        5, 10, 6, 6, 2, 0, 1, 11, 15, 11, 10, 1, 11, 13, 11, 13, 0, 0, 7, 9, 6, 7, 6, 6, 6, 4, 2, 5, 2, 2, 7, 1,
        5, 8, 2, 1, 1, 6, 4, 5, 1, 6, 5, 2, 7, 7, 7, 7, 2, 5, 1, 2, 9, 1, 8, 9, 1, 1, 1, 4, 4, 3, 1, 1,
        2, 3, 0, 7, 1, 6, 3, 0, 1, 0, 1, 1, 1, 0,
    ];

    /// <summary>
    /// One script as commands, each <c>[opcode, args…]</c>. Reading stops at the end command or anything it can't
    /// read (a few scripts jump over data with 0x4A; those end there).
    /// </summary>
    public static List<int[]> ParseScript(ReadOnlySpan<byte> file)
    {
        var commands = new List<int[]>();
        if (file.Length < 0x3C)
            return commands;
        var p = (int)U32(file, 4);
        while (p + 2 <= file.Length)
        {
            var op = U16(file, p);
            if (op >= ArgCounts.Length || p + 2 + 4 * ArgCounts[op] > file.Length)
                break;
            var command = new int[1 + ArgCounts[op]];
            command[0] = op;
            for (var i = 0; i < ArgCounts[op]; i++)
                command[1 + i] = S32(file, p + 2 + 4 * i);
            commands.Add(command);
            if (op == End)
                break;
            p += 2 + 4 * ArgCounts[op];
        }
        return commands;
    }

    /// <summary>Writes <c>moves.json</c> (every script) and <c>particles/{n}.json</c> + textures to <paramref name="outDir"/>.</summary>
    public static void Export(NitroFs fs, string outDir)
    {
        var scripts = Narc.Parse(fs.ReadFile(Gen5Layout.MoveAnimations));
        var all = new List<List<int[]>>();
        for (var i = 0; i < scripts.Count; i++)
            all.Add(ParseScript(scripts.Read(i)));
        File.WriteAllText(Path.Combine(outDir, "moves.json"), System.Text.Json.JsonSerializer.Serialize(all, Json.Compact));

        var particles = Narc.Parse(fs.ReadFile(Gen5Layout.MoveParticles));
        var dir = Directory.CreateDirectory(Path.Combine(outDir, "particles")).FullName;
        for (var i = 0; i < particles.Count; i++)
        {
            var bytes = Lz.DecompressIfNeeded(particles.Read(i));
            if (!Spa.IsSpa(bytes))
                continue;
            try
            {
                var spa = Spa.Decode(bytes, dir, i.ToString());
                File.WriteAllText(Path.Combine(dir, $"{i}.json"), System.Text.Json.JsonSerializer.Serialize(spa, Json.Compact));
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // A file this reader can't follow just has no effect.
            }
        }
    }
}
