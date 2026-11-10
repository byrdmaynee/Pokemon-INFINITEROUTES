using InfiniteRoutes.Extractor.Gen5;
using InfiniteRoutes.Extractor.Graphics;
using InfiniteRoutes.Extractor.Nitro3D;

namespace InfiniteRoutes.Extractor.Extraction;

public enum Facing { Down, Up, Left, Right }

/// <summary>
/// Layout of an exported overworld character sheet: one row per <see cref="Facing"/>, columns
/// walk (stand, left foot, right foot) then run (stand, left foot, right foot). The player's frames are
/// <see cref="FrameSize"/> square; NPC frames are as large as their sprite (frame = sheet width / <see cref="Columns"/>).
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

    /// <summary>The player surfing: one sitting frame per direction (up, down, left, right), repeated across the columns.</summary>
    public static Rgba FromGen5Surf(Tex0 tex)
    {
        var frames = tex.Textures.Values.ToDictionary(t => int.TryParse(t.Name[(t.Name.LastIndexOf('.') + 1)..], out var n) ? n : 0);
        var palette = tex.PaletteNames.FirstOrDefault();
        var sheet = new Rgba(Columns * FrameSize, Rows * FrameSize);
        Facing[] romOrder = [Facing.Up, Facing.Down, Facing.Left, Facing.Right];
        for (var d = 0; d < 4; d++)
            if (frames.TryGetValue(d + 1, out var t))
            {
                var image = tex.Decode(t, palette);
                for (var column = 0; column < Columns; column++)
                    sheet.Blit(image, column * FrameSize, (int)romOrder[d] * FrameSize);
            }
        return sheet;
    }

    /// <summary>
    /// Builds a sheet from a Gen 5 NPC BTX0. Three layouts occur: 12 or more frames (three per direction,
    /// up, down, left, right), 7 frames whose missing poses are mirrors (up stand/walk, down stand/walk,
    /// left stand/walk/walk; right mirrors left), and single-frame objects. Sheets of 8–11 frames are the
    /// 7-frame layout plus extra poses (the Pokémon Center nurse's 9 add a bow), which are left out. NPCs don't
    /// run, so the run columns repeat the walk ones. Null when the sprite has no frames.
    /// </summary>
    public static Rgba? FromGen5Npc(Tex0 tex)
    {
        var palette = tex.PaletteNames.FirstOrDefault();
        var frames = tex.Textures.Values
            .Select((t, i) => (Order: int.TryParse(t.Name[(t.Name.LastIndexOf('.') + 1)..], out var n) ? n : i, Texture: t))
            .OrderBy(f => f.Order)
            .Select(f => tex.Decode(f.Texture, palette))
            .ToList();
        if (frames.Count == 0)
            return null;

        // poses[facing][step]
        var poses = new Rgba[Rows][];
        if (frames.Count >= 12)
        {
            Facing[] romOrder = [Facing.Up, Facing.Down, Facing.Left, Facing.Right];
            for (var d = 0; d < 4; d++)
                poses[(int)romOrder[d]] = [frames[d * 3], frames[d * 3 + 1], frames[d * 3 + 2]];
        }
        else if (frames.Count >= 7)
        {
            poses[(int)Facing.Up] = [frames[0], frames[1], frames[1].FlipX()];
            poses[(int)Facing.Down] = [frames[2], frames[3], frames[3].FlipX()];
            poses[(int)Facing.Left] = [frames[4], frames[5], frames[6]];
            poses[(int)Facing.Right] = [frames[4].FlipX(), frames[5].FlipX(), frames[6].FlipX()];
        }
        else
            for (var d = 0; d < Rows; d++)
                poses[d] = [frames[0], frames[0], frames[0]];

        var size = frames.Max(f => Math.Max(f.Width, f.Height));
        var sheet = new Rgba(Columns * size, Rows * size);
        for (var d = 0; d < Rows; d++)
        for (var step = 0; step < 3; step++)
        {
            var f = poses[d][step];
            // Bottom-centre each frame in its cell so feet stay on the ground.
            var (x, y) = ((size - f.Width) / 2, size - f.Height);
            sheet.Blit(f, Column(false, step) * size + x, d * size + y);
            sheet.Blit(f, Column(true, step) * size + x, d * size + y);
        }
        return sheet;
    }
}
