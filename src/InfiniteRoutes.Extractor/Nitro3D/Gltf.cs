using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfiniteRoutes.Extractor.Graphics;

namespace InfiniteRoutes.Extractor.Nitro3D;

/// <summary>A model placed in the output with a transform (row-vector convention, includes unit scale).</summary>
public sealed record GltfPart(NitroModel Model, Matrix4x4 Transform);

/// <summary>
/// Writes decoded models as glTF 2.0: one mesh with a primitive per material, non-indexed
/// triangles with position, UV and vertex colour. Geometry goes in an embedded base64 buffer;
/// textures are referenced as PNG files decoded once per texture set.
/// </summary>
public static class Gltf
{
    /// <param name="textureUri">Maps (texture, palette) to a PNG URI relative to the .gltf file, or null if missing.</param>
    /// <param name="textureSize">Texture size in texels, for normalising UVs.</param>
    /// <param name="hasAlpha">Whether the texture needs alpha testing.</param>
    public static JsonObject Build(NitroModel model, float unitScale, Vector3 offset,
        Func<NitroMaterial, string?> textureUri, Func<NitroMaterial, (int W, int H)> textureSize, Func<NitroMaterial, bool> hasAlpha) =>
        Build(model.Name, [new GltfPart(model, Matrix4x4.CreateScale(unitScale) * Matrix4x4.CreateTranslation(offset))],
            textureUri, textureSize, hasAlpha);

    public static JsonObject Build(string name, IReadOnlyList<GltfPart> parts,
        Func<NitroMaterial, string?> textureUri, Func<NitroMaterial, (int W, int H)> textureSize, Func<NitroMaterial, bool> hasAlpha)
    {
        var buffer = new MemoryStream();
        var accessors = new JsonArray();
        var views = new JsonArray();
        var primitives = new JsonArray();
        var materials = new JsonArray();
        var textures = new JsonArray();
        var images = new JsonArray();
        var samplers = new JsonArray();
        var imageIndex = new Dictionary<string, int>();
        var materialIndex = new Dictionary<(NitroModel, int), int>();

        int AddView(byte[] data, int stride)
        {
            while (buffer.Length % 4 != 0) buffer.WriteByte(0);
            var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = buffer.Length, ["byteLength"] = data.Length, ["byteStride"] = stride, ["target"] = 34962 };
            buffer.Write(data);
            views.Add(view);
            return views.Count - 1;
        }
        int AddAccessor(int view, int count, string type, Vector3? min = null, Vector3? max = null)
        {
            var a = new JsonObject { ["bufferView"] = view, ["componentType"] = 5126, ["count"] = count, ["type"] = type };
            if (min is { } mn && max is { } mx)
            {
                a["min"] = new JsonArray(mn.X, mn.Y, mn.Z);
                a["max"] = new JsonArray(mx.X, mx.Y, mx.Z);
            }
            accessors.Add(a);
            return accessors.Count - 1;
        }

        foreach (var (model, transform) in parts)
        foreach (var mesh in model.Meshes)
        {
            var mat = model.Materials[mesh.Material];
            var (tw, th) = textureSize(mat);
            var n = mesh.Vertices.Count;
            var pos = new byte[n * 12];
            var uv = new byte[n * 8];
            var col = new byte[n * 16];
            // A mirroring transform flips triangle winding; swap two corners of each triangle to restore it.
            var mirrored = transform.GetDeterminant() < 0;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            for (var i = 0; i < n; i++)
            {
                var v = mesh.Vertices[mirrored && i % 3 != 0 ? i - i % 3 + 3 - i % 3 : i];
                var p = Vector3.Transform(v.Position, transform);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                Write(pos, i * 12, p.X, p.Y, p.Z);
                Write(uv, i * 8, v.Uv.X / Math.Max(1, tw), v.Uv.Y / Math.Max(1, th));
                Write(col, i * 16, v.Color.X, v.Color.Y, v.Color.Z, v.Color.W);
            }

            var attributes = new JsonObject
            {
                ["POSITION"] = AddAccessor(AddView(pos, 12), n, "VEC3", min, max),
                ["TEXCOORD_0"] = AddAccessor(AddView(uv, 8), n, "VEC2"),
                ["COLOR_0"] = AddAccessor(AddView(col, 16), n, "VEC4"),
            };

            if (!materialIndex.TryGetValue((model, mesh.Material), out var gm))
            {
                var pbr = new JsonObject { ["metallicFactor"] = 0, ["roughnessFactor"] = 1 };
                var alpha = mat.Alpha / 31f;
                pbr["baseColorFactor"] = new JsonArray(mat.Diffuse.X, mat.Diffuse.Y, mat.Diffuse.Z, alpha);
                if (textureUri(mat) is { } uri)
                {
                    if (!imageIndex.TryGetValue(uri, out var img))
                    {
                        images.Add(new JsonObject { ["uri"] = uri });
                        img = imageIndex[uri] = images.Count - 1;
                    }
                    samplers.Add(new JsonObject
                    {
                        ["magFilter"] = 9728, ["minFilter"] = 9728,   // nearest: keep the pixel-art look
                        ["wrapS"] = Wrap(mat.RepeatS, mat.FlipS),
                        ["wrapT"] = Wrap(mat.RepeatT, mat.FlipT),
                    });
                    textures.Add(new JsonObject { ["source"] = img, ["sampler"] = samplers.Count - 1 });
                    pbr["baseColorTexture"] = new JsonObject { ["index"] = textures.Count - 1 };
                    // Base colour already comes from the texture; diffuse tint is folded into vertex colours by the DS.
                    pbr["baseColorFactor"] = new JsonArray(1, 1, 1, alpha);
                }
                var material = new JsonObject
                {
                    ["name"] = mat.Name,
                    ["pbrMetallicRoughness"] = pbr,
                    ["doubleSided"] = mat.BackFace && mat.FrontFace,
                    ["extensions"] = new JsonObject { ["KHR_materials_unlit"] = new JsonObject() },
                };
                if (alpha < 1)
                    material["alphaMode"] = "BLEND";
                else if (hasAlpha(mat))
                {
                    material["alphaMode"] = "MASK";
                    material["alphaCutoff"] = 0.5;
                }
                materials.Add(material);
                gm = materialIndex[(model, mesh.Material)] = materials.Count - 1;
            }

            primitives.Add(new JsonObject { ["attributes"] = attributes, ["material"] = gm, ["mode"] = 4 });
        }

        var root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Infinite Routes extractor" },
            ["extensionsUsed"] = new JsonArray("KHR_materials_unlit"),
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = new JsonArray(new JsonObject { ["name"] = name, ["mesh"] = 0 }),
            ["meshes"] = new JsonArray(new JsonObject { ["name"] = name, ["primitives"] = primitives }),
            ["materials"] = materials,
            ["accessors"] = accessors,
            ["bufferViews"] = views,
            ["buffers"] = new JsonArray(new JsonObject
            {
                ["byteLength"] = buffer.Length,
                ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(buffer.ToArray()),
            }),
        };
        if (images.Count > 0)
        {
            root["images"] = images;
            root["samplers"] = samplers;
            root["textures"] = textures;
        }
        return root;
    }

    public static void Save(JsonObject gltf, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, gltf.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    static int Wrap(bool repeat, bool flip) => !repeat ? 33071 : flip ? 33648 : 10497;

    static void Write(byte[] dst, int offset, params float[] values)
    {
        for (var i = 0; i < values.Length; i++)
            BitConverter.TryWriteBytes(dst.AsSpan(offset + i * 4), values[i]);
    }
}
