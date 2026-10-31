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

    [Fact]
    public void Zone_events_yield_warps_after_furniture_and_npcs()
    {
        // 1 furniture, 1 NPC, 2 warps: the Nuvema door and the house's 3-wide exit mat.
        var file = new byte[8 + Gen5Events.FurnitureSize + Gen5Events.NpcSize + 2 * Gen5Events.WarpSize];
        file[4] = 1; file[5] = 1; file[6] = 2;
        var o = 8 + Gen5Events.FurnitureSize + Gen5Events.NpcSize;
        void Warp(int at, int zone, int dir, int trans, int x, int z, int w, int h)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at), (ushort)zone);
            file[at + 4] = (byte)dir;
            file[at + 5] = (byte)trans;
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at + 8), (ushort)x);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at + 12), (ushort)z);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at + 14), (ushort)w);
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(at + 16), (ushort)h);
        }
        Warp(o, 390, 2, 3, 782 * 16 + 8, 748 * 16 + 8, 1, 1);
        Warp(o + Gen5Events.WarpSize, 389, 1, 1, 5 * 16 + 8, 10 * 16 + 8, 3, 1);

        var warps = Gen5Events.ParseWarps(file);

        Assert.Equal(2, warps.Count);
        Assert.Equal((390, 782, 748, 2, 3), (warps[0].TargetZone, warps[0].TileX, warps[0].TileY, warps[0].Direction, warps[0].Transition));
        Assert.True(warps[0].OnGrid);
        Assert.Equal((5, 10, 3, 1), (warps[1].TileX, warps[1].TileY, warps[1].Width, warps[1].Depth));
    }

    [Fact]
    public void Truncated_event_file_is_rejected()
    {
        var file = new byte[8];
        file[6] = 1;
        Assert.Throws<InvalidDataException>(() => Gen5Events.ParseWarps(file));
    }

    [Fact]
    public void Zone_events_yield_npcs_after_furniture()
    {
        // Nuvema's first townsperson: sprite 20 wandering 2 tiles east-west at (783, 760), facing down, half a unit up.
        var file = new byte[8 + Gen5Events.FurnitureSize + Gen5Events.NpcSize];
        file[4] = 1; file[5] = 1;
        var o = 8 + Gen5Events.FurnitureSize;
        foreach (var (at, v) in new[] { (2, 20), (4, 3), (10, 1), (12, 1), (20, 2), (28, 783), (30, 760), (32, 0x8000), (34, 3) })
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(o + at), (ushort)v);

        var npc = Assert.Single(Gen5Events.ParseNpcs(file));

        Assert.Equal((20, 3, 1, 1, 2, 0), (npc.Sprite, npc.Movement, npc.Script, npc.Direction, npc.RangeX, npc.RangeY));
        Assert.Equal((783, 760), (npc.X, npc.Y));
        Assert.True(npc.OnGrid);
        Assert.Equal(3.5f / 16, npc.HeightTiles);
    }

    [Fact]
    public void Truncated_npc_list_is_rejected()
    {
        var file = new byte[8 + Gen5Events.NpcSize - 1];
        file[5] = 1;
        Assert.Throws<InvalidDataException>(() => Gen5Events.ParseNpcs(file));
    }

    [Fact]
    public void Overworld_table_maps_sprite_ids_to_archive_members()
    {
        var table = new byte[4 + 2 * Gen5OverworldTable.EntrySize];
        BinaryPrimitives.WriteUInt32LittleEndian(table, 2);
        foreach (var (i, id, member) in new[] { (0, 1, 6), (1, 147, 144) })
        {
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(4 + i * Gen5OverworldTable.EntrySize), (ushort)id);
            BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(4 + i * Gen5OverworldTable.EntrySize + 16), (ushort)member);
        }

        var map = Gen5OverworldTable.Parse(table);

        Assert.Equal(6, map[1]);
        Assert.Equal(144, map[147]);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void FlipX_mirrors_each_row()
    {
        var img = new Rgba(3, 1);
        img.Set(0, 0, 0x01);
        img.Set(2, 0, 0x03);
        var flipped = img.FlipX();
        Assert.Equal(0x03, flipped.Pixels[3]);
        Assert.Equal(0x01, flipped.Pixels[2 * 4 + 3]);
    }

    [Fact]
    public void Warp_rectangle_covers_its_width_and_height()
    {
        var mat = new WarpInfo(5, 10, 3, 1, 389, 0, Facing.Up, WarpKind.Mat);
        Assert.True(mat.Contains(5, 10));
        Assert.True(mat.Contains(7, 10));
        Assert.False(mat.Contains(8, 10));
        Assert.False(mat.Contains(6, 11));
        Assert.True(new WarpInfo(1, 1, 0, 0, 2, 0, null, WarpKind.Door).Contains(1, 1));
    }

    [Fact]
    public void Gltf_keeps_winding_under_a_mirroring_transform()
    {
        var mesh = new NitroMesh(0);
        foreach (var p in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitZ })
            mesh.Vertices.Add(new NitroVertex { Position = p, Color = Vector4.One });
        var model = new NitroModel
        {
            Name = "t",
            Materials = [new NitroMaterial("m", null, null, false, false, false, false, 31, true, true, Vector3.One)],
            Meshes = [mesh],
        };
        var gltf = Gltf.Build("t", [new GltfPart(model, Matrix4x4.CreateScale(1, 1, -1))], _ => null, _ => (1, 1), _ => false);

        var buffer = Convert.FromBase64String(((string)gltf["buffers"]![0]!["uri"]!).Split(',')[1]);
        var pos = Enumerable.Range(0, 3).Select(i => new Vector3(
            BitConverter.ToSingle(buffer, i * 12), BitConverter.ToSingle(buffer, i * 12 + 4), BitConverter.ToSingle(buffer, i * 12 + 8))).ToArray();
        // Corners 1 and 2 swap, so the mirrored triangle's normal points the same way (down) as the original's.
        Assert.Equal(new Vector3(0, 0, 0), pos[0]);
        Assert.Equal(new Vector3(0, 0, -1), pos[1]);
        Assert.Equal(new Vector3(1, 0, 0), pos[2]);
    }
}
