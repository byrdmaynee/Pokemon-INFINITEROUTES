using static InfiniteRoutes.Extractor.Nitro3D.NitroDict;

namespace InfiniteRoutes.Extractor.Nitro3D;

/// <summary>
/// Texture SRT animation for one material, sampled per frame. Translation is in texture repeats
/// (1 = one whole texture), so a track ending on a whole number loops seamlessly. Constant tracks hold one value.
/// </summary>
public sealed record SrtTrack(string Material, float[] ScaleS, float[] ScaleT, float[] RotationDegrees, float[] TranslateS, float[] TranslateT);

/// <summary>An SRT animation: scrolling/scaling textures (water, clouds, conveyor belts) on named materials.</summary>
public sealed record SrtAnimation(string Name, int Frames, IReadOnlyList<SrtTrack> Tracks);

/// <summary>
/// NSBTA ("BTA0") texture SRT animations. Layout of the SRT0 block: a dictionary of animations, each
/// "M\0AT", u16 frame count, u16 ?, then a dictionary of materials whose 40-byte entries are five
/// 8-byte tracks — scale S, scale T, rotation, translate S, translate T. A track is u16 frames, u8 frame
/// step (0 = every frame), u8 flags (0x20 constant: value inline; 0x10 16-bit samples, else 32-bit),
/// u32 value or offset (from the animation start) to the samples. Values are fixed point, 4096 = 1;
/// rotation is a (sin, cos) pair.
/// </summary>
public static class Nsbta
{
    const int Constant = 0x20;
    const int Half = 0x10;
    const float One = 4096f;

    public static List<SrtAnimation> Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < 0x14 || !file[..4].SequenceEqual("BTA0"u8))
            throw new InvalidDataException("Not a BTA0 file.");
        var srt = Section(file, "SRT0");
        if (srt < 0)
            throw new InvalidDataException("BTA0 file has no SRT0 block.");

        var result = new List<SrtAnimation>();
        foreach (var anim in Read(file, srt + 8))
        {
            var a = srt + (int)U32(file, anim.EntryOffset);
            if (!file.Slice(a, 4).SequenceEqual("M\0AT"u8))
                throw new InvalidDataException($"SRT animation {anim.Name} has no M.AT header.");
            int frames = U16(file, a + 4);
            var tracks = new List<SrtTrack>();
            foreach (var mat in Read(file, a + 8))
            {
                var t = mat.EntryOffset;
                tracks.Add(new SrtTrack(mat.Name,
                    Samples(file, a, t, frames, rotation: false), Samples(file, a, t + 8, frames, rotation: false),
                    Samples(file, a, t + 16, frames, rotation: true),
                    Samples(file, a, t + 24, frames, rotation: false), Samples(file, a, t + 32, frames, rotation: false)));
            }
            result.Add(new SrtAnimation(anim.Name, frames, tracks));
        }
        return result;
    }

    static float[] Samples(ReadOnlySpan<byte> file, int anim, int track, int frames, bool rotation)
    {
        int step = file[track + 2], flags = file[track + 3];
        var value = U32(file, track + 4);
        if ((flags & Constant) != 0)
            return [rotation ? Rotation(value) : (flags & Half) != 0 ? (short)value / One : (int)value / One];

        // With a frame step of n, one sample covers 2^n frames.
        var count = Math.Max(1, (frames + (1 << step) - 1) >> step);
        var data = anim + (int)value;
        var size = rotation ? 4 : (flags & Half) != 0 ? 2 : 4;
        if (data < 0 || data + count * size > file.Length)
            throw new InvalidDataException("SRT track data runs past the end of the file.");
        var samples = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var o = data + Math.Min(f >> step, count - 1) * size;
            samples[f] = rotation ? Rotation(U32(file, o)) : size == 2 ? S16(file, o) / One : (int)U32(file, o) / One;
        }
        return samples;
    }

    /// <summary>Rotation as (sin, cos) fx16 halves → degrees.</summary>
    static float Rotation(uint value) => MathF.Atan2((short)value / One, (short)(value >> 16) / One) * 180 / MathF.PI;
}
