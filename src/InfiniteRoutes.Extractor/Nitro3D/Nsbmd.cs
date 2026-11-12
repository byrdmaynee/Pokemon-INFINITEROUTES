using System.Numerics;
using static InfiniteRoutes.Extractor.Nitro3D.NitroDict;

namespace InfiniteRoutes.Extractor.Nitro3D;

public sealed record NitroMaterial(
    string Name,
    string? Texture,
    string? Palette,
    bool RepeatS, bool RepeatT, bool FlipS, bool FlipT,
    int Alpha,          // 0..31 from the polygon attributes
    bool BackFace, bool FrontFace,
    Vector3 Diffuse);

public struct NitroVertex
{
    public Vector3 Position;
    public Vector2 Uv;          // in texels; divide by texture size
    public Vector4 Color;
}

/// <summary>Triangles drawn with one material.</summary>
public sealed class NitroMesh(int material)
{
    public int Material { get; } = material;
    public List<NitroVertex> Vertices { get; } = [];
}

public sealed class NitroModel
{
    public required string Name { get; init; }
    public required List<NitroMaterial> Materials { get; init; }
    public required List<NitroMesh> Meshes { get; init; }
    public Tex0? Textures { get; init; }
}

/// <summary>
/// Decodes the first model in a BMD0 (NSBMD) file into triangle lists, running the model's
/// render commands (SBC) and the geometry-engine display lists the way the DS would, then
/// applying node transforms and the model's position scale.
/// </summary>
public static class Nsbmd
{
    public static NitroModel Decode(byte[] file)
    {
        var b = file;
        var mdl0 = Section(b, "MDL0");
        if (mdl0 < 0)
            throw new InvalidDataException("BMD0 file has no MDL0 section.");

        var models = Read(b, mdl0 + 8);
        if (models.Count == 0)
            throw new InvalidDataException("BMD0 file contains no models.");
        var m = mdl0 + (int)U32(b, models[0].EntryOffset);

        var sbc = m + (int)U32(b, m + 4);
        var mat = m + (int)U32(b, m + 8);
        var shp = m + (int)U32(b, m + 12);
        var posScale = Fx32(b, m + 0x1C);
        var nodeDict = m + 0x40;

        var materials = ReadMaterials(b, mat);
        var shapes = Read(b, shp + 0).Select(e => shp + (int)U32(b, e.EntryOffset)).ToList();
        var nodes = Read(b, nodeDict).Select(e => NodeMatrix(b, nodeDict + (int)U32(b, e.EntryOffset))).ToList();

        var meshes = new Dictionary<int, NitroMesh>();
        var stack = new Matrix4x4[32];
        Array.Fill(stack, Matrix4x4.Identity);
        var current = Matrix4x4.Identity;
        var currentMaterial = 0;
        var scale = Matrix4x4.CreateScale(posScale);

        var pc = sbc;
        while (pc < b.Length)
        {
            var op = b[pc];
            switch (op & 0x1F)
            {
                case 0x00: pc += 1; break;                          // NOP
                case 0x01: pc = b.Length; break;                    // RET
                case 0x02: pc += 3; break;                          // NODE visibility
                case 0x03: current = stack[b[pc + 1] & 31]; pc += 2; break; // MTX restore
                case 0x04: currentMaterial = b[pc + 1]; pc += 2; break;     // MAT
                case 0x05:                                          // SHP
                {
                    var shape = b[pc + 1];
                    if (shape < shapes.Count)
                    {
                        if (!meshes.TryGetValue(currentMaterial, out var mesh))
                            meshes[currentMaterial] = mesh = new NitroMesh(currentMaterial);
                        RunDisplayList(b, shapes[shape], scale * current, stack, scale, mesh);   // vertices are scaled, then placed
                    }
                    pc += 2;
                    break;
                }
                case 0x06:                                          // NODEDESC node, parent, flags [, dest] [, src]
                {
                    var node = b[pc + 1];
                    var args = 4;
                    int? dest = null;
                    if ((op & 0x20) != 0) dest = b[pc + args++];
                    if ((op & 0x40) != 0) current = stack[b[pc + args++] & 31];
                    if (node < nodes.Count)
                        current = nodes[node] * current;
                    if (dest is { } d)
                        stack[d & 31] = current;
                    pc += args;
                    break;
                }
                case 0x07: case 0x08: pc += 2 + ((op & 0x20) != 0 ? 1 : 0) + ((op & 0x40) != 0 ? 1 : 0); break; // billboards
                case 0x09: pc += 3 + b[pc + 2] * 3; break;          // NODEMIX (skinning; maps don't use it)
                case 0x0A: pc += 9; break;                          // CALLDL
                case 0x0B: pc += 1; break;                          // POSSCALE (applied globally)
                case 0x0C: case 0x0D: pc += 3; break;               // ENVMAP / PRJMAP
                default: throw new InvalidDataException($"Unknown render command 0x{op:X2} at 0x{pc - sbc:X}.");
            }
        }

        return new NitroModel
        {
            Name = models[0].Name,
            Materials = materials,
            Meshes = meshes.Values.Where(x => x.Vertices.Count > 0).ToList(),
            Textures = Tex0.FromFile(file),
        };
    }

    static List<NitroMaterial> ReadMaterials(byte[] b, int mat)
    {
        var texDict = mat + U16(b, mat);
        var plttDict = mat + U16(b, mat + 2);
        var entries = Read(b, mat + 4);

        var texFor = new string?[entries.Count];
        var plttFor = new string?[entries.Count];
        void Bind(int dict, string?[] target)
        {
            foreach (var e in Read(b, dict))
            {
                var list = mat + U16(b, e.EntryOffset);
                int count = b[e.EntryOffset + 2];
                for (var i = 0; i < count; i++)
                    if (b[list + i] < target.Length)
                        target[b[list + i]] = e.Name;
            }
        }
        Bind(texDict, texFor);
        Bind(plttDict, plttFor);

        var result = new List<NitroMaterial>();
        for (var i = 0; i < entries.Count; i++)
        {
            var o = mat + (int)U32(b, entries[i].EntryOffset);
            var difAmb = U32(b, o + 4);
            var polyAttr = U32(b, o + 12);
            var texParam = U32(b, o + 20);
            var diffuse = Graphics.Nclr.Bgr555ToRgba((ushort)(difAmb & 0x7FFF));
            result.Add(new NitroMaterial(entries[i].Name, texFor[i], plttFor[i],
                RepeatS: (texParam & (1 << 16)) != 0, RepeatT: (texParam & (1 << 17)) != 0,
                FlipS: (texParam & (1 << 18)) != 0, FlipT: (texParam & (1 << 19)) != 0,
                Alpha: (int)((polyAttr >> 16) & 31),
                BackFace: (polyAttr & (1 << 6)) != 0, FrontFace: (polyAttr & (1 << 7)) != 0,
                Diffuse: new Vector3((diffuse >> 24) / 255f, ((diffuse >> 16) & 0xFF) / 255f, ((diffuse >> 8) & 0xFF) / 255f)));
        }
        return result;
    }

    /// <summary>Local transform of a node, as a row-vector matrix (scale × rotation × translation).</summary>
    static Matrix4x4 NodeMatrix(byte[] b, int o)
    {
        int flag = U16(b, o);
        var m00 = Fx16(b, o + 2);
        var p = o + 4;
        var t = Vector3.Zero;
        var r = Matrix4x4.Identity;
        var s = Vector3.One;

        if ((flag & 1) == 0)
        {
            t = new Vector3(Fx32(b, p), Fx32(b, p + 4), Fx32(b, p + 8));
            p += 12;
        }
        if ((flag & 2) == 0)
        {
            if ((flag & 8) != 0)
            {
                r = Pivot((flag >> 4) & 0xF, (flag >> 8) & 0xF, Fx16(b, p), Fx16(b, p + 2));
                p += 4;
            }
            else
            {
                var v = new float[9];
                v[0] = m00;
                for (var i = 1; i < 9; i++)
                    v[i] = Fx16(b, p + (i - 1) * 2);
                p += 16;
                r = new Matrix4x4(v[0], v[1], v[2], 0, v[3], v[4], v[5], 0, v[6], v[7], v[8], 0, 0, 0, 0, 1);
            }
        }
        if ((flag & 4) == 0)
            s = new Vector3(Fx32(b, p), Fx32(b, p + 4), Fx32(b, p + 8));

        return Matrix4x4.CreateScale(s) * r * Matrix4x4.CreateTranslation(t);
    }

    static Matrix4x4 Pivot(int select, int neg, float a, float b)
    {
        float o = (neg & 1) != 0 ? -1 : 1;
        var c = (neg & 2) != 0 ? -b : b;
        var d = (neg & 4) != 0 ? -a : a;
        float[,] m = select switch
        {
            0 => new[,] { { o, 0, 0 }, { 0, a, b }, { 0, c, d } },
            1 => new[,] { { 0, o, 0 }, { a, 0, b }, { c, 0, d } },
            2 => new[,] { { 0, 0, o }, { a, b, 0 }, { c, d, 0 } },
            3 => new[,] { { 0, a, b }, { o, 0, 0 }, { 0, c, d } },
            4 => new[,] { { a, 0, b }, { 0, o, 0 }, { c, 0, d } },
            5 => new[,] { { a, b, 0 }, { 0, 0, o }, { c, d, 0 } },
            6 => new[,] { { 0, a, b }, { 0, c, d }, { o, 0, 0 } },
            7 => new[,] { { a, 0, b }, { c, 0, d }, { 0, o, 0 } },
            _ => new[,] { { a, b, 0 }, { c, d, 0 }, { 0, 0, o } },
        };
        return new Matrix4x4(m[0, 0], m[0, 1], m[0, 2], 0, m[1, 0], m[1, 1], m[1, 2], 0, m[2, 0], m[2, 1], m[2, 2], 0, 0, 0, 0, 1);
    }

    /// <summary>Runs a shape's display list: packed command bytes followed by their parameter words.</summary>
    static void RunDisplayList(byte[] b, int shape, Matrix4x4 transform, Matrix4x4[] stack, Matrix4x4 scale, NitroMesh mesh)
    {
        var start = shape + (int)U32(b, shape + 8);
        var end = start + (int)U32(b, shape + 12);
        var pos = start;

        var vtx = Vector3.Zero;
        var uv = Vector2.Zero;
        var color = Vector4.One;
        var primitive = 0;
        var strip = new List<NitroVertex>();

        void Emit()
        {
            strip.Add(new NitroVertex { Position = Vector3.Transform(vtx, transform), Uv = uv, Color = color });
            switch (primitive)
            {
                case 0 when strip.Count == 3: Tri(0, 1, 2); strip.Clear(); break;
                case 1 when strip.Count == 4: Tri(0, 1, 2); Tri(0, 2, 3); strip.Clear(); break;
                case 2 when strip.Count >= 3:
                    // Alternate winding so every triangle in the strip faces the same way.
                    if (strip.Count % 2 == 1) Tri(strip.Count - 3, strip.Count - 2, strip.Count - 1);
                    else Tri(strip.Count - 2, strip.Count - 3, strip.Count - 1);
                    break;
                case 3 when strip.Count >= 4 && strip.Count % 2 == 0:
                    var n = strip.Count;
                    Tri(n - 4, n - 3, n - 1);
                    Tri(n - 4, n - 1, n - 2);
                    break;
            }
        }
        void Tri(int i0, int i1, int i2)
        {
            mesh.Vertices.Add(strip[i0]);
            mesh.Vertices.Add(strip[i1]);
            mesh.Vertices.Add(strip[i2]);
        }

        while (pos + 4 <= end)
        {
            var cmds = b.AsSpan(pos, 4).ToArray();
            pos += 4;
            foreach (var cmd in cmds)
            {
                int Param() { var v = (int)U32(b, pos); pos += 4; return v; }
                switch (cmd)
                {
                    case 0x00: break;
                    case 0x14: transform = scale * stack[Param() & 31]; break;  // MTX_RESTORE
                    case 0x1B: pos += 12; break;                                // MTX_SCALE
                    case 0x20: color = Bgr(Param()); break;                     // COLOR
                    case 0x21: Param(); break;                                  // NORMAL (unlit for now)
                    case 0x22:                                                  // TEXCOORD (12.4 fixed)
                    {
                        var p = Param();
                        uv = new Vector2((short)(p & 0xFFFF) / 16f, (short)(p >> 16) / 16f);
                        break;
                    }
                    case 0x23:                                                  // VTX_16
                    {
                        var a = Param(); var c = Param();
                        vtx = new Vector3((short)(a & 0xFFFF), (short)(a >> 16), (short)(c & 0xFFFF)) / 4096f;
                        Emit();
                        break;
                    }
                    case 0x24:                                                  // VTX_10
                    {
                        var a = Param();
                        vtx = new Vector3(S10(a), S10(a >> 10), S10(a >> 20)) / 64f;
                        Emit();
                        break;
                    }
                    case 0x25: { var a = Param(); vtx = new Vector3((short)(a & 0xFFFF) / 4096f, (short)(a >> 16) / 4096f, vtx.Z); Emit(); break; }
                    case 0x26: { var a = Param(); vtx = new Vector3((short)(a & 0xFFFF) / 4096f, vtx.Y, (short)(a >> 16) / 4096f); Emit(); break; }
                    case 0x27: { var a = Param(); vtx = new Vector3(vtx.X, (short)(a & 0xFFFF) / 4096f, (short)(a >> 16) / 4096f); Emit(); break; }
                    case 0x28:                                                  // VTX_DIFF
                    {
                        var a = Param();
                        vtx += new Vector3(S10(a), S10(a >> 10), S10(a >> 20)) / 4096f;
                        Emit();
                        break;
                    }
                    case 0x29: case 0x2A: case 0x2B: case 0x30: case 0x31: case 0x32: case 0x33:
                    case 0x10: case 0x12: case 0x13: case 0x50: case 0x60: case 0x72:
                        Param(); break;
                    case 0x11: case 0x15: break;
                    case 0x16: case 0x18: pos += 64; break;
                    case 0x17: case 0x19: pos += 48; break;
                    case 0x1A: pos += 36; break;
                    case 0x1C: pos += 12; break;
                    case 0x34: pos += 128; break;
                    case 0x70: pos += 12; break;
                    case 0x71: pos += 8; break;
                    case 0x40: primitive = Param() & 3; strip.Clear(); break;   // BEGIN_VTXS
                    case 0x41: strip.Clear(); break;                            // END_VTXS
                    default: return;   // unknown command: stop this list rather than misread the rest
                }
            }
        }
    }

    static float S10(int v) => ((v & 0x3FF) ^ 0x200) - 0x200;

    static Vector4 Bgr(int c) =>
        new((c & 0x1F) / 31f, ((c >> 5) & 0x1F) / 31f, ((c >> 10) & 0x1F) / 31f, 1);
}
