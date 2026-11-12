using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using InfiniteRoutes.Extractor;
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
    public void A_bridge_on_the_upper_layer_crosses_the_river_below()
    {
        // A "GC" chunk: two collision layers. The ground has a river (0x56) with solid banks; the upper layer has a
        // bridge deck (0x80) over it at x = 5. Walls on both layers stay walls.
        static byte[] Layer(Action<byte[]> edit)
        {
            var layer = new byte[4 + 32 * 32 * 8];
            BinaryPrimitives.WriteUInt16LittleEndian(layer, 32);
            BinaryPrimitives.WriteUInt16LittleEndian(layer.AsSpan(2), 32);
            for (var i = 0; i < 32 * 32; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(layer.AsSpan(4 + i * 8 + 6), 0x81);
            edit(layer);
            return layer;
        }
        var ground = Layer(c =>
        {
            SetFlags(c, 5, 9, 0x80);
            SetFlags(c, 5, 10, 0xc3);
            SetFlags(c, 5, 11, 0x56);
            SetFlags(c, 4, 11, 0x56);
            SetFlags(c, 5, 12, 0xc3);
            SetFlags(c, 5, 13, 0x80);
        });
        var upper = Layer(c =>
        {
            foreach (var y in new[] { 9, 10, 11, 12, 13 })
                SetFlags(c, 5, y, 0x80);
        });
        var model = new byte[16];
        "BMD0"u8.CopyTo(model);
        var buildings = new byte[4];
        var header = new byte[4 + 5 * 4];
        "GC"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 4);
        var o = header.Length;
        foreach (var (i, part) in new[] { model, ground, upper, buildings }.Index())
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4 + i * 4), (uint)o);
            o += part.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)o);
        var map = Gen5Map.Parse([.. header, .. model, .. ground, .. upper, .. buildings]);

        foreach (var y in new[] { 9, 10, 11, 12, 13 })
            Assert.False(map.IsBlocked(5, y), $"bridge tile {y}");
        Assert.True(map.IsWater(4, 11));   // the river beside the bridge is still water
        Assert.True(map.IsBlocked(6, 11));
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

    [Fact]
    public void Extraction_lock_is_exclusive_until_released()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ir-lock-" + Guid.NewGuid().ToString("N"));
        using (ExtractionCache.Lock(dir, out var waited))
        {
            Assert.False(waited);
            using var cts = new CancellationTokenSource(300);
            Assert.ThrowsAny<OperationCanceledException>(() => ExtractionCache.Lock(dir, out _, cts.Token));
        }
        using (ExtractionCache.Lock(dir, out var again))
            Assert.False(again);
        Assert.False(File.Exists(dir + ".lock"));
    }

    [Fact]
    public void Seasonal_areas_are_groups_of_four_outdoor_entries()
    {
        // Areas: 0 indoor (group 28), 1–4 outdoor seasons (32…35), 5 outdoor non-seasonal (32 alone).
        var areas = new byte[7 * Gen5Layout.AreaSize];
        byte[] groups = [28, 32, 33, 34, 35, 32, 40];
        for (var i = 0; i < groups.Length; i++)
        {
            areas[i * Gen5Layout.AreaSize + Gen5Layout.AreaSeasonGroup] = groups[i];
            areas[i * Gen5Layout.AreaSize + Gen5Layout.AreaOutdoorFlag] = (byte)(i == 0 ? 0 : 1);
        }

        Assert.False(Gen5WorldExtractor.IsSeasonal(areas, 0));
        Assert.True(Gen5WorldExtractor.IsSeasonal(areas, 1));
        Assert.False(Gen5WorldExtractor.IsSeasonal(areas, 2));
        Assert.False(Gen5WorldExtractor.IsSeasonal(areas, 5));
        var cell = new WorldCell(0, 0, 7, 3, "", 1, Seasons: 4);
        Assert.Equal(4, cell.AreaFor((int)Season.Winter));
        Assert.Equal(5, new WorldCell(0, 0, 7, 3, "", 5).AreaFor((int)Season.Winter));
    }

    /// <summary>A one-entry Nitro dictionary: 8-byte header, info block (entry size, names offset, entry), one 16-byte name.</summary>
    static byte[] Dict(string name, byte[] entry)
    {
        var d = new byte[8 + 4 + entry.Length + 16];
        d[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(2), (ushort)d.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(6), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(8), (ushort)entry.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(10), (ushort)(4 + entry.Length));
        entry.CopyTo(d, 12);
        Encoding.ASCII.GetBytes(name).CopyTo(d, 12 + entry.Length);
        return d;
    }

    [Fact]
    public void Srt_animation_reads_constant_and_sampled_tracks()
    {
        // One material over 4 frames: scale 1, no rotation, S scrolls 0, .25, .5, .75 (16-bit), T constant -0.5 (16-bit).
        static byte[] Track(int flags, uint value) => [4, 0, 0, (byte)flags, (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];
        byte[] Anim(uint dataOffset) =>
        [
            .. "M AT"u8, 4, 0, 0, 0,
            .. Dict("sea_mizu1", [.. Track(0x30, 0x1000), .. Track(0x30, 0x1000), .. Track(0x20, 0x1000_0000),
                .. Track(0x10, dataOffset), .. Track(0x30, 0xF800)]),
        ];
        var head = Anim(0).Length;
        byte[] samples = [0x00, 0x00, 0x00, 0x04, 0x00, 0x08, 0x00, 0x0C];
        byte[] anim = [.. Anim((uint)head), .. samples];

        var srtDict = Dict("out21", new byte[4]);
        var srt = new byte[8 + srtDict.Length];
        "SRT0"u8.CopyTo(srt);
        srtDict.CopyTo(srt, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(srt.AsSpan(8 + 12), (uint)srt.Length);   // animation follows the SRT0 header + dictionary

        var header = new byte[0x14];
        "BTA0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x0E), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x10), 0x14);
        byte[] file = [.. header, .. srt, .. anim];

        var a = Assert.Single(Nsbta.Parse(file));
        Assert.Equal(("out21", 4), (a.Name, a.Frames));
        var t = Assert.Single(a.Tracks);
        Assert.Equal("sea_mizu1", t.Material);
        Assert.Equal([1f], t.ScaleS);
        Assert.Equal([0f], t.RotationDegrees);
        Assert.Equal([0f, 0.25f, 0.5f, 0.75f], t.TranslateS);
        Assert.Equal([-0.5f], t.TranslateT);
    }

    [Fact]
    public void Texture_pattern_splits_frames_into_tracks_and_saves_each_frame()
    {
        // TEX0 with one 8×8 4-colour texture "hana01.1" and one palette.
        var texDict = Dict("hana01.1", [0, 0, 0, 2 << 2, 0, 0, 0, 0]);   // params: 8×8, format 2, data offset 0
        var plttDict = Dict("hana01.1_pl", new byte[4]);
        var tex0 = new byte[0x3C];
        "TEX0"u8.CopyTo(tex0);
        var texData = 0x3C + texDict.Length + plttDict.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(tex0.AsSpan(0x0E), 0x3C);
        BinaryPrimitives.WriteUInt32LittleEndian(tex0.AsSpan(0x14), (uint)texData);
        BinaryPrimitives.WriteUInt32LittleEndian(tex0.AsSpan(0x34), (uint)(0x3C + texDict.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(tex0.AsSpan(0x38), (uint)(texData + 16));
        byte[] tex = [.. tex0, .. texDict, .. plttDict, .. new byte[16], 0xFF, 0x7F, 0x1F, 0x00, 0xE0, 0x03, 0x00, 0x7C];
        var btxHeader = new byte[0x14];
        "BTX0"u8.CopyTo(btxHeader);
        BinaryPrimitives.WriteUInt16LittleEndian(btxHeader.AsSpan(0x0E), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(btxHeader.AsSpan(0x10), 0x14);
        byte[] btx = [.. btxHeader, .. tex];

        // Three frames in two tracks (frames 0–1 and 2), 8 ticks long.
        byte[] table = [3, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 2, 0, 0, 8, 0, 0, 0];
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)(16 + table.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)(16 + table.Length + btx.Length));
        byte[] file = [.. header, .. table, .. btx];

        var dir = Path.Combine(Path.GetTempPath(), $"ir-pattern-{Guid.NewGuid():N}");
        try
        {
            var patterns = Gen5TexturePatterns.Parse(file, dir);
            Assert.Equal(2, patterns.Count);
            Assert.All(patterns, p => Assert.Equal(("hana01", 8), (p.Texture, p.Length)));
            Assert.Equal([0, 4], patterns[0].Frames.Select(f => f.Start));
            Assert.Equal([0], patterns[1].Frames.Select(f => f.Start));
            Assert.Equal("hana01.1__hana01.1_pl.png", patterns[0].Frames[0].Image);
            Assert.True(File.Exists(Path.Combine(dir, "hana01.1__hana01.1_pl.png")));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        Assert.Equal("hana01", Gen5TexturePatterns.BaseName("hana01.12"));
        Assert.Equal("sea_mizu1", Gen5TexturePatterns.BaseName("sea_mizu1"));
    }
}
