using InfiniteRoutes.Extractor.Gen5;
using InfiniteRoutes.Extractor.Graphics;
using InfiniteRoutes.Extractor.Nitro3D;

namespace InfiniteRoutes.Extractor.Extraction;

public enum Facing { Down, Up, Left, Right }

/// <summary>
/// Layout of an exported overworld character sheet: one row per <see cref="Facing"/>, columns
/// walk (stand, left foot, right foot) then run (stand, left foot, right foot); 32×32 frames.
/// </summary>
public static class CharacterSheet
{
    public const int FrameSize = 32;
    public const int Columns = 6;
    public const int Rows = 4;

    public static int Column(bool run, int step) => (run ? 3 : 0) + step;

    /// <summary>Builds a sheet from a Gen 5 player BTX0 (frames named "*.1" … "*.24").</summary>
    public static Rgba FromGen5Player(Tex0 tex)
    {
        var frames = tex.Textures.Values.ToDictionary(t => int.Parse(t.Name[(t.Name.LastIndexOf('.') + 1)..]));
        var palette = tex.PaletteNames.FirstOrDefault();
        var sheet = new Rgba(Columns * FrameSize, Rows * FrameSize);
        // ROM direction order is up, down, left, right.
        Facing[] romOrder = [Facing.Up, Facing.Down, Facing.Left, Facing.Right];
        for (var d = 0; d < 4; d++)
        for (var step = 0; step < 3; step++)
        for (var run = 0; run < 2; run++)
        {
            var frame = (run == 1 ? Gen5Layout.Player.RunFirst : Gen5Layout.Player.WalkFirst) + d * 3 + step;
            if (frames.TryGetValue(frame, out var t))
                sheet.Blit(tex.Decode(t, palette), Column(run == 1, step) * FrameSize, (int)romOrder[d] * FrameSize);
        }
        return sheet;
    }
}
