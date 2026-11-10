using System.Buffers.Binary;
using InfiniteRoutes.Extractor.Gen5;
using InfiniteRoutes.Extractor.Nitro3D;

namespace InfiniteRoutes.Tests;

public class MoveAnimationTests
{
    [Fact]
    public void Script_reads_packed_commands_until_the_end()
    {
        // Header: u32 1 and 14 entry offsets (0x3C), then op 0x06 (1 arg), op 0x39 (1 arg), op 0x4D.
        var file = new byte[0x3C + 6 + 6 + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 1);
        for (var i = 0; i < 14; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4 + i * 4), 0x3C);
        var p = 0x3C;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(p), 0x06);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(p + 2), 161);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(p + 6), 0x39);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(p + 8), -5);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(p + 12), Gen5MoveAnimations.End);

        var script = Gen5MoveAnimations.ParseScript(file);

        Assert.Equal([[0x06, 161], [0x39, -5], [Gen5MoveAnimations.End]], script);
        Assert.Equal(0x4E, Gen5MoveAnimations.ArgCounts.Length);
        Assert.Empty(Gen5MoveAnimations.ParseScript(new byte[8]));
    }

    [Fact]
    public void Particle_file_decodes_emitters_blocks_and_textures()
    {
        // One emitter with a scale animation and gravity, then one 8×8 A5I3 texture with a 2-colour palette.
        var file = new byte[0x20 + 0x58 + 12 + 8 + 0x20 + 64 + 4];
        " APS12_1"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(10), 1);
        var e = 0x20;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(e), 1 | (1 << 8) | (1 << 14) | (1 << 24));   // sphere, scale anim, self-maintaining, gravity
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(e + 0x10), 0x2000);   // 2 a frame
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(e + 0x1E), 0x1000);   // axis (0, 1, 0)
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(e + 0x22), 0x001F);  // red
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(e + 0x3E), 20);      // particle life
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(e + 0x44), 1 | (31 << 8) | (128 << 16));
        var s = e + 0x58;
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(s), 0x800);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(s + 2), 0x1000);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(s + 4), 0x2000);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(s + 6), 64 | (192 << 8));
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(s + 12 + 2), -0x10);  // gravity y
        var t = s + 12 + 8;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(t + 4), 6);           // A5I3, 8×8
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(t + 0x0C), 0x20 + 64);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(t + 0x10), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(t + 0x1C), 0x20 + 64 + 4);
        file[t + 0x20] = (31 << 3) | 1;                                             // opaque palette colour 1
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(t + 0x20 + 64 + 2), 0x7C00);   // blue

        var dir = Directory.CreateTempSubdirectory("spa").FullName;
        try
        {
            var spa = Spa.Decode(file, dir, "x");

            var em = Assert.Single(spa.Emitters);
            Assert.Equal((1, 2f, 20, 31, 128, 0x1F), (em.EmissionType, em.EmissionCount, em.ParticleLife, em.BaseAlpha, em.AirResistance, em.Color));
            Assert.True(em.SelfMaintaining);
            Assert.Equal([0f, 1f, 0f], em.Axis);
            Assert.Equal(new SpaScaleAnim(0.5f, 1f, 2f, 64, 192, false), em.ScaleAnim);
            Assert.Equal(-0x10 / 4096f, em.Gravity![1]);
            Assert.Null(em.ColorAnim);
            var tex = Assert.Single(spa.Textures);
            Assert.Equal(("x_0.png", 8, 8), (tex.Image, tex.Width, tex.Height));
            Assert.True(File.Exists(Path.Combine(dir, "x_0.png")));
            Assert.Throws<InvalidDataException>(() => Spa.Decode(new byte[0x40], dir, "y"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
