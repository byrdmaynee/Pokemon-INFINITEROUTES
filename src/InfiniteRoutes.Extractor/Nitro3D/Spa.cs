using InfiniteRoutes.Extractor.Graphics;
using static InfiniteRoutes.Extractor.Nitro3D.NitroDict;

namespace InfiniteRoutes.Extractor.Nitro3D;

/// <summary>Scale over a particle's life: start → mid until <see cref="In"/>, mid until <see cref="Out"/>, then → end (0–255 of the life).</summary>
public sealed record SpaScaleAnim(float Start, float Mid, float End, int In, int Out, bool Loop);

/// <summary>Colour over life: start before <see cref="In"/>, the emitter colour at <see cref="Peak"/>, end from <see cref="Out"/> (BGR555).</summary>
public sealed record SpaColorAnim(int Start, int End, int In, int Peak, int Out, bool RandomStart, bool Loop, bool Interpolate);

/// <summary>Alpha (0–31) over life, shaped like the scale animation; <see cref="RandomRange"/> scales it randomly per particle.</summary>
public sealed record SpaAlphaAnim(int Start, int Mid, int End, int In, int Out, int RandomRange, bool Loop);

/// <summary>Texture frames over life: frame i shows until <see cref="Step"/> × (i + 1) of 255.</summary>
public sealed record SpaTexAnim(IReadOnlyList<int> Frames, int Step, bool RandomStart, bool Loop);

/// <summary>Particles each parent particle gives off during its life.</summary>
public sealed record SpaChild(bool UsesBehaviors, bool ScaleToEnd, bool FadeOut, int RotationType, bool FollowEmitter, bool UseChildColor,
    int DrawType, int PolygonRotAxis, int PolygonPlane, float RandomVelocity, float EndScale, int Life, int VelocityRatio, int ScaleRatio,
    int Color, int Count, int Delay, int Interval, int Texture, int TileS, int TileT, bool FlipS, bool FlipT, bool DpolFaceEmitter);

/// <summary>A pull toward (magnet) or a slide toward (convergence) a point, relative to the emitter.</summary>
public sealed record SpaTarget(float[] Point, float Force);

/// <summary>A random kick of up to <see cref="Magnitude"/> per axis every <see cref="Interval"/> frames.</summary>
public sealed record SpaRandom(float[] Magnitude, int Interval);

/// <summary>Spinning the particle positions around an emitter axis (0 X, 1 Y, 2 Z) by <see cref="Angle"/> (65536 = a turn) a frame.</summary>
public sealed record SpaSpin(int Angle, int Axis);

/// <summary>A floor at height <see cref="Y"/>: particles crossing it die (type 0) or bounce (1).</summary>
public sealed record SpaPlane(float Y, float Elasticity, int Type);

/// <summary>
/// One emitter of a Nitro SPL particle file: where and how it spawns particles, how they move, and how they look over
/// their life. Distances are in model units (fx32 1.0); times in frames; angles in 1/65536 turns; colours BGR555.
/// </summary>
public sealed record SpaEmitter(
    int EmissionType, int DrawType, int CircleAxis, bool HasRotation, bool RandomInitAngle, bool SelfMaintaining, bool FollowEmitter,
    int PolygonRotAxis, int PolygonPlane, bool RandomizeLoopedAnim, bool DrawChildrenFirst, bool HideParent,
    float[] Position, float EmissionCount, float Radius, float Length, float[] Axis, int Color,
    float VelocityFromPosition, float VelocityFromAxis, float BaseScale, float AspectRatio, int StartDelay,
    int MinRotation, int MaxRotation, int InitAngle, int EmitterLife, int ParticleLife,
    int RandomScale, int RandomLife, int RandomVelocity, int Interval, int BaseAlpha, int AirResistance, int Texture,
    int LoopFrames, float DbbScale, int TileS, int TileT, int ScaleAnimDir, bool DpolFaceEmitter, bool FlipS, bool FlipT,
    float PolygonX, float PolygonY,
    SpaScaleAnim? ScaleAnim, SpaColorAnim? ColorAnim, SpaAlphaAnim? AlphaAnim, SpaTexAnim? TexAnim, SpaChild? Child,
    float[]? Gravity, SpaRandom? Random, SpaTarget? Magnet, SpaSpin? Spin, SpaPlane? Plane, SpaTarget? Convergence);

/// <summary>A particle texture, saved as <see cref="Image"/>; <see cref="RepeatS"/>/<see cref="RepeatT"/> and flips are its wrap modes.</summary>
public sealed record SpaTexture(string Image, int Width, int Height, bool RepeatS, bool RepeatT, bool FlipS, bool FlipT);

/// <summary>A decoded particle file: its emitters (the effects a script can start by index) and its textures.</summary>
public sealed record SpaFile(IReadOnlyList<SpaEmitter> Emitters, IReadOnlyList<SpaTexture> Textures);

/// <summary>
/// Reads the Nitro SDK's SPL particle files (" APS" "12_1"): a 32-byte header (u16 emitter count @8, u16 texture
/// count @10), then each emitter — an 88-byte record whose flags pick the optional blocks after it (scale, colour,
/// alpha and texture animations, a child emitter, then gravity, random, magnet, spin, collision-plane and convergence
/// behaviours) — then the textures, each a 32-byte header and its texel and palette data.
/// </summary>
public static class Spa
{
    public static bool IsSpa(ReadOnlySpan<byte> b) => b.Length >= 0x20 && b[..4].SequenceEqual(" APS"u8);

    /// <summary>Decodes <paramref name="file"/>, saving its textures as <c>{prefix}_{i}.png</c> in <paramref name="imageDir"/>.</summary>
    public static SpaFile Decode(byte[] file, string imageDir, string prefix)
    {
        if (!IsSpa(file))
            throw new InvalidDataException("Not an SPA particle file.");
        var count = U16(file, 8);
        var texCount = U16(file, 10);
        var p = 0x20;
        var emitters = new List<SpaEmitter>();
        for (var i = 0; i < count; i++)
            emitters.Add(ReadEmitter(file, ref p));

        var textures = new List<SpaTexture>();
        for (var i = 0; i < texCount && p + 0x20 <= file.Length; i++)
        {
            var param = U32(file, p + 4);
            var size = (int)U32(file, p + 0x1C);
            var (w, h) = (8 << (int)((param >> 4) & 0xF), 8 << (int)((param >> 8) & 0xF));
            var name = $"{prefix}_{i}.png";
            Png.Save(DecodeTexture(file, p, param, w, h), Path.Combine(imageDir, name));
            textures.Add(new SpaTexture(name, w, h, (param & 0x1000) != 0, (param & 0x2000) != 0, (param & 0x4000) != 0, (param & 0x8000) != 0));
            if (size <= 0)
                break;
            p += size;
        }
        return new SpaFile(emitters, textures);
    }

    static float[] Vec32(byte[] b, int o) => [Fx32(b, o), Fx32(b, o + 4), Fx32(b, o + 8)];
    static float[] Vec16(byte[] b, int o) => [Fx16(b, o), Fx16(b, o + 2), Fx16(b, o + 4)];

    static SpaEmitter ReadEmitter(byte[] b, ref int p)
    {
        var o = p;
        var f = U32(b, o);
        bool Bit(int n) => ((f >> n) & 1) != 0;
        var randomAtt = U32(b, o + 0x40);
        var m0 = U32(b, o + 0x44);
        var m1 = U32(b, o + 0x48);
        var m2 = U32(b, o + 0x4C);
        p += 0x58;

        SpaScaleAnim? scale = null;
        if (Bit(8))
        {
            var curve = U16(b, p + 6);
            scale = new SpaScaleAnim(Fx16(b, p), Fx16(b, p + 2), Fx16(b, p + 4), curve & 0xFF, curve >> 8, (U16(b, p + 8) & 1) != 0);
            p += 12;
        }
        SpaColorAnim? color = null;
        if (Bit(9))
        {
            var curve = U32(b, p + 4);
            var flags = U16(b, p + 8);
            color = new SpaColorAnim(U16(b, p), U16(b, p + 2), (int)(curve & 0xFF), (int)((curve >> 8) & 0xFF), (int)((curve >> 16) & 0xFF),
                (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0);
            p += 12;
        }
        SpaAlphaAnim? alpha = null;
        if (Bit(10))
        {
            var a = U16(b, p);
            var flags = U16(b, p + 2);
            var curve = U16(b, p + 4);
            alpha = new SpaAlphaAnim(a & 0x1F, (a >> 5) & 0x1F, (a >> 10) & 0x1F, curve & 0xFF, curve >> 8, flags & 0xFF, (flags & 0x100) != 0);
            p += 8;
        }
        SpaTexAnim? tex = null;
        if (Bit(11))
        {
            var param = U32(b, p + 8);
            var frames = (int)Math.Clamp(param & 0xFF, 0, 8);
            tex = new SpaTexAnim(b.AsSpan(p, frames).ToArray().Select(x => (int)x).ToList(), (int)((param >> 8) & 0xFF),
                (param & 0x10000) != 0, (param & 0x20000) != 0);
            p += 12;
        }
        SpaChild? child = null;
        if (Bit(16))
        {
            var cf = U16(b, p);
            var c0 = U32(b, p + 12);
            var c1 = U32(b, p + 16);
            child = new SpaChild((cf & 1) != 0, (cf & 2) != 0, (cf & 4) != 0, (cf >> 3) & 3, (cf & 0x20) != 0, (cf & 0x40) != 0,
                (cf >> 7) & 3, (cf >> 9) & 3, (cf >> 11) & 1, Fx16(b, p + 2), Fx16(b, p + 4), U16(b, p + 6), b[p + 8], b[p + 9],
                U16(b, p + 10), (int)(c0 & 0xFF), (int)((c0 >> 8) & 0xFF), (int)((c0 >> 16) & 0xFF), (int)(c0 >> 24),
                1 << (int)(c1 & 3), 1 << (int)((c1 >> 2) & 3), (c1 & 0x10) != 0, (c1 & 0x20) != 0, (c1 & 0x40) != 0);
            p += 20;
        }
        float[]? gravity = null;
        if (Bit(24))
        {
            gravity = Vec16(b, p);
            p += 8;
        }
        SpaRandom? random = null;
        if (Bit(25))
        {
            random = new SpaRandom(Vec16(b, p), U16(b, p + 6));
            p += 8;
        }
        SpaTarget? magnet = null;
        if (Bit(26))
        {
            magnet = new SpaTarget(Vec32(b, p), Fx16(b, p + 12));
            p += 16;
        }
        SpaSpin? spin = null;
        if (Bit(27))
        {
            spin = new SpaSpin(U16(b, p), U16(b, p + 2));
            p += 4;
        }
        SpaPlane? plane = null;
        if (Bit(28))
        {
            plane = new SpaPlane(Fx32(b, p), Fx16(b, p + 4), U16(b, p + 6) & 3);
            p += 8;
        }
        SpaTarget? convergence = null;
        if (Bit(29))
        {
            convergence = new SpaTarget(Vec32(b, p), Fx16(b, p + 12));
            p += 16;
        }

        return new SpaEmitter(
            EmissionType: (int)(f & 0xF), DrawType: (int)((f >> 4) & 3), CircleAxis: (int)((f >> 6) & 3), HasRotation: Bit(12),
            RandomInitAngle: Bit(13), SelfMaintaining: Bit(14), FollowEmitter: Bit(15), PolygonRotAxis: (int)((f >> 17) & 3),
            PolygonPlane: (int)((f >> 19) & 1), RandomizeLoopedAnim: Bit(20), DrawChildrenFirst: Bit(21), HideParent: Bit(22),
            Position: Vec32(b, o + 4), EmissionCount: Fx32(b, o + 0x10), Radius: Fx32(b, o + 0x14), Length: Fx32(b, o + 0x18),
            Axis: Vec16(b, o + 0x1C), Color: U16(b, o + 0x22), VelocityFromPosition: Fx32(b, o + 0x24), VelocityFromAxis: Fx32(b, o + 0x28),
            BaseScale: Fx32(b, o + 0x2C), AspectRatio: Fx16(b, o + 0x30), StartDelay: U16(b, o + 0x32),
            MinRotation: S16(b, o + 0x34), MaxRotation: S16(b, o + 0x36), InitAngle: U16(b, o + 0x38),
            EmitterLife: U16(b, o + 0x3C), ParticleLife: U16(b, o + 0x3E),
            RandomScale: (int)(randomAtt & 0xFF), RandomLife: (int)((randomAtt >> 8) & 0xFF), RandomVelocity: (int)((randomAtt >> 16) & 0xFF),
            Interval: (int)(m0 & 0xFF), BaseAlpha: (int)((m0 >> 8) & 0xFF), AirResistance: (int)((m0 >> 16) & 0xFF), Texture: (int)(m0 >> 24),
            LoopFrames: (int)(m1 & 0xFF), DbbScale: (short)((m1 >> 8) & 0xFFFF) / 4096f, TileS: 1 << (int)((m1 >> 24) & 3),
            TileT: 1 << (int)((m1 >> 26) & 3), ScaleAnimDir: (int)((m1 >> 28) & 7), DpolFaceEmitter: (m1 >> 31) != 0,
            FlipS: (m2 & 1) != 0, FlipT: (m2 & 2) != 0, PolygonX: Fx16(b, o + 0x50), PolygonY: Fx16(b, o + 0x52),
            ScaleAnim: scale, ColorAnim: color, AlphaAnim: alpha, TexAnim: tex, Child: child,
            Gravity: gravity, Random: random, Magnet: magnet, Spin: spin, Plane: plane, Convergence: convergence);
    }

    /// <summary>
    /// A particle texture: its header's u32 @4 has the format (bits 0–3), size and wrap; texels follow the 32-byte
    /// header, the palette is at u32 @0xC from it, and bit 16 makes palette colour 0 transparent.
    /// </summary>
    static Rgba DecodeTexture(byte[] b, int header, uint param, int w, int h)
    {
        var img = new Rgba(w, h);
        var format = (int)(param & 0xF);
        var color0Transparent = (param & 0x10000) != 0;
        var data = header + 0x20;
        var pal = header + (int)U32(b, header + 0x0C);
        var palSize = (int)U32(b, header + 0x10);
        uint Color(int i) => i * 2 + 1 < palSize && pal + i * 2 + 1 < b.Length ? Nclr.Bgr555ToRgba(U16(b, pal + i * 2)) : 0xFFFFFFFF;
        var n = w * h;
        switch (format)
        {
            case 1: // A3I5
            case 6: // A5I3
            {
                var (indexBits, alphaMax) = format == 1 ? (5, 7) : (3, 31);
                for (var i = 0; i < n && data + i < b.Length; i++)
                {
                    var v = b[data + i];
                    var a = (v >> indexBits) * 255 / alphaMax;
                    img.Set(i % w, i / w, (Color(v & ((1 << indexBits) - 1)) & 0xFFFFFF00) | (uint)a);
                }
                break;
            }
            case 2:
            case 3:
            case 4:
            {
                var bpp = format == 2 ? 2 : format == 3 ? 4 : 8;
                var mask = (1 << bpp) - 1;
                for (var i = 0; i < n; i++)
                {
                    var bit = i * bpp;
                    if (data + bit / 8 >= b.Length)
                        break;
                    var index = (b[data + bit / 8] >> (bit % 8)) & mask;
                    if (index == 0 && color0Transparent)
                        continue;
                    img.Set(i % w, i / w, Color(index));
                }
                break;
            }
            case 7:
                for (var i = 0; i < n && data + i * 2 + 1 < b.Length; i++)
                {
                    var c = U16(b, data + i * 2);
                    if ((c & 0x8000) != 0)
                        img.Set(i % w, i / w, Nclr.Bgr555ToRgba(c));
                }
                break;
        }
        return img;
    }
}
