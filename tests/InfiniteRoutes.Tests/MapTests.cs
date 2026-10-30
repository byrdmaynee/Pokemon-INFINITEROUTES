using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using InfiniteRoutes.Extractor.Extraction;
using InfiniteRoutes.Extractor.Gen5;
using InfiniteRoutes.Extractor.Graphics;
using InfiniteRoutes.Extractor.Nitro3D;

namespace InfiniteRoutes.Tests;

public class MapTests
{
    /// <summary>A minimal "WB" chunk: fake BMD0 header, collision grid, one building record.</summary>
    static byte[] BuildChunk(Action<byte[]> editTiles)
    {
        var model = new byte[16];
        "BMD0"u8.CopyTo(model);

        var collision = new byte[4 + 32 * 32 * 8];
        BinaryPrimitives.WriteUInt16LittleEndian(collision, 32);
        BinaryPrimitives.WriteUInt16LittleEndian(collision.AsSpan(2), 32);
        for (var i = 0; i < 32 * 32; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(collision.AsSpan(4 + i * 8 + 6), 0x80);
        editTiles(collision);

        var buildings = new byte[4 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(buildings, 1);
        BinaryPrimitives.WriteInt32LittleEndian(buildings.AsSpan(4), -56 * 4096);
        BinaryPrimitives.WriteInt32LittleEndian(buildings.AsSpan(12), 80 * 4096);
        BinaryPrimitives.WriteUInt16LittleEndian(buildings.AsSpan(16), 0x8000);
        buildings[18] = 0x01;   // big-endian ID 0x0129
        buildings[19] = 0x29;

        var header = new byte[4 + 4 * 4];
        "WB"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 3);
        var o = header.Length;
        foreach (var (i, part) in new[] { model, collision, buildings }.Index())
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4 + i * 4), (uint)o);
            o += part.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)o);
        return [.. header, .. model, .. collision, .. buildings];
    }

    static void SetFlags(byte[] collision, int x, int y, ushort flags) =>
        BinaryPrimitives.WriteUInt16LittleEndian(collision.AsSpan(4 + (y * 32 + x) * 8 + 6), flags);

    [Fact]
    public void Chunk_container_splits_into_model_collision_and_buildings()
    {
        var map = Gen5Map.Parse(BuildChunk(_ => { }));

        Assert.Equal("WB", map.Magic);
        Assert.Equal("BMD0"u8.ToArray(), map.Model[..4]);
        Assert.NotNull(map.Collision);
        var b = Assert.Single(map.Buildings);
        Assert.Equal(new Gen5Building(-56, 0, 80, 0x8000, 0x129), b);
    }

    [Fact]
    public void Tile_flags_classify_solid_grass_and_water()
    {
        var map = Gen5Map.Parse(BuildChunk(c =>
        {
            SetFlags(c, 1, 0, 0x81);
            SetFlags(c, 2, 0, 0x24);
            SetFlags(c, 3, 0, 0x16);
        }));

        Assert.False(map.IsBlocked(0, 0));
        Assert.True(map.IsBlocked(1, 0));
        Assert.True(map.IsTallGrass(2, 0));
        Assert.False(map.IsBlocked(2, 0));
        Assert.True(map.IsWater(3, 0));
        Assert.True(map.IsBlocked(3, 0));
        Assert.Equal(0, map.TileFlags(0, 0));
        Assert.Equal(1, map.TileFlags(1, 0));
        Assert.Equal(2, map.TileFlags(2, 0));
        Assert.Equal(5, map.TileFlags(3, 0));
    }

    [Fact]
    public void Chunk_without_model_is_rejected()
    {
        var data = BuildChunk(_ => { });
        data[20] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => Gen5Map.Parse(data));
    }

    [Fact]
    public void Nitro_dictionary_reads_names_and_entries()
    {
        // Layout matching a real one-entry dictionary from a map model.
        var d = new byte[0x28];
        d[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(2), 0x28);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(6), 0x10);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x10), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(0x12), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x14), 0x30);
        Encoding.ASCII.GetBytes("map24_23").CopyTo(d, 0x18);

        var e = Assert.Single(NitroDict.Read(d, 0));
        Assert.Equal("map24_23", e.Name);
        Assert.Equal(0x30u, NitroDict.U32(d, e.EntryOffset));
    }

    [Fact]
    public void Gltf_bakes_transform_and_normalises_uvs()
    {
        var mesh = new NitroMesh(0);
        foreach (var (p, uv) in new[] { (Vector3.Zero, Vector2.Zero), (Vector3.UnitX * 16, new Vector2(32, 0)), (Vector3.UnitZ * 16, new Vector2(0, 16)) })
            mesh.Vertices.Add(new NitroVertex { Position = p, Uv = uv, Color = Vector4.One });
        var model = new NitroModel
        {
            Name = "test",
            Materials = [new NitroMaterial("m", "tex", "pal", true, true, false, true, 31, false, true, Vector3.One)],
            Meshes = [mesh],
        };

        var gltf = Gltf.Build("chunk", [new GltfPart(model, Matrix4x4.CreateScale(1f / 16) * Matrix4x4.CreateTranslation(10, 0, 0))],
            _ => "textures/tex.png", _ => (32, 16), _ => false);

        var position = gltf["accessors"]![0]!;
        Assert.Equal(3, (int)position["count"]!);
        Assert.Equal(10f, (float)position["min"]![0]!);
        Assert.Equal(11f, (float)position["max"]![0]!);
        var sampler = gltf["samplers"]![0]!;
        Assert.Equal(10497, (int)sampler["wrapS"]!);   // repeat
        Assert.Equal(33648, (int)sampler["wrapT"]!);   // mirrored repeat
        Assert.Equal("textures/tex.png", (string)gltf["images"]![0]!["uri"]!);
        Assert.False((bool)gltf["materials"]![0]!["doubleSided"]!);
    }

    [Fact]
    public void Blit_copies_and_clips_to_the_destination()
    {
        var src = new Rgba(2, 2);
        for (var i = 0; i < 4; i++)
            src.Set(i % 2, i / 2, 0x11223344u + (uint)i);
        var dst = new Rgba(3, 3);
        dst.Blit(src, 2, 1);

        Assert.Equal(0x44, dst.Pixels[(1 * 3 + 2) * 4 + 3]);   // (0,0) of src
        Assert.Equal(0x46, dst.Pixels[(2 * 3 + 2) * 4 + 3]);   // (0,1) of src; column 1 clipped
        Assert.Equal(0, dst.Pixels[0]);
    }

    [Fact]
    public void Character_sheet_puts_walk_then_run_columns()
    {
        Assert.Equal(0, CharacterSheet.Column(run: false, step: 0));
        Assert.Equal(2, CharacterSheet.Column(run: false, step: 2));
        Assert.Equal(3, CharacterSheet.Column(run: true, step: 0));
        Assert.Equal(CharacterSheet.Columns - 1, CharacterSheet.Column(run: true, step: 2));
    }
}
