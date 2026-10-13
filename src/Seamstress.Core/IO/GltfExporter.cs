using System.Numerics;
using System.Text;
using System.Text.Json;
using Seamstress.Core.Model;

namespace Seamstress.Core.IO;

/// <summary>glTF 2.0 writer. ".glb" produces a single binary file, ".gltf" a JSON file with a ".bin" beside it.</summary>
public static class GltfExporter
{
    public static void Export(ExportScene scene, string path)
    {
        bool binary = Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase);
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string binName = Path.GetFileNameWithoutExtension(path) + ".bin";

        var buffer = new MemoryStream();
        var views = new List<(int Offset, int Length, int? Target)>();
        var accessors = new List<string>();
        var materials = scene.Materials().ToList();

        int AddView(byte[] data, int? target)
        {
            while (buffer.Length % 4 != 0)
                buffer.WriteByte(0);
            views.Add(((int)buffer.Length, data.Length, target));
            buffer.Write(data);
            return views.Count - 1;
        }

        static byte[] Bytes<T>(T[] values) where T : unmanaged
            => System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        static string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        var meshJson = new List<string>();
        foreach (var mesh in scene.Meshes)
        {
            var primitives = new List<string>();
            // glTF has one index per vertex, so every (position, uv) pair becomes its own vertex.
            foreach (var group in mesh.Parts.GroupBy(p => p.Material))
            {
                var lookup = new Dictionary<int, int>();
                var positions = new List<Vector3>();
                var normals = new List<Vector3>();
                var uvs = new List<Vector2>();
                var indices = new List<uint>();
                foreach (var part in group)
                {
                    for (int k = 0; k < part.PositionIndices.Length; k++)
                    {
                        int uvIndex = part.UvIndices[k];
                        if (!lookup.TryGetValue(uvIndex, out int vertex))
                        {
                            vertex = positions.Count;
                            lookup[uvIndex] = vertex;
                            positions.Add(mesh.Positions[part.PositionIndices[k]]);
                            normals.Add(mesh.Normals[part.PositionIndices[k]]);
                            uvs.Add(new Vector2(mesh.Uvs[uvIndex].X, 1 - mesh.Uvs[uvIndex].Y)); // glTF's V axis points down
                        }
                        indices.Add((uint)vertex);
                    }
                }
                if (indices.Count == 0)
                    continue;
                var min = positions.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
                var max = positions.Aggregate(new Vector3(float.MinValue), Vector3.Max);
                int count = positions.Count;
                int positionAccessor = accessors.Count;
                accessors.Add($"{{\"bufferView\":{AddView(Bytes(positions.ToArray()), 34962)},\"componentType\":5126,\"count\":{count},\"type\":\"VEC3\","
                    + $"\"min\":[{F(min.X)},{F(min.Y)},{F(min.Z)}],\"max\":[{F(max.X)},{F(max.Y)},{F(max.Z)}]}}");
                accessors.Add($"{{\"bufferView\":{AddView(Bytes(normals.ToArray()), 34962)},\"componentType\":5126,\"count\":{count},\"type\":\"VEC3\"}}");
                accessors.Add($"{{\"bufferView\":{AddView(Bytes(uvs.ToArray()), 34962)},\"componentType\":5126,\"count\":{count},\"type\":\"VEC2\"}}");
                accessors.Add($"{{\"bufferView\":{AddView(Bytes(indices.ToArray()), 34963)},\"componentType\":5125,\"count\":{indices.Count},\"type\":\"SCALAR\"}}");
                primitives.Add($"{{\"attributes\":{{\"POSITION\":{positionAccessor},\"NORMAL\":{positionAccessor + 1},\"TEXCOORD_0\":{positionAccessor + 2}}},"
                    + $"\"indices\":{positionAccessor + 3},\"material\":{materials.IndexOf(group.Key)},\"mode\":4}}");
            }
            meshJson.Add($"{{\"name\":{JsonSerializer.Serialize(mesh.Name)},\"primitives\":[{string.Join(",", primitives)}]}}");
        }

        // Textures: embedded in .glb, copied beside a .gltf.
        var images = new List<string>();
        var textures = new List<string>();
        var materialJson = new List<string>();
        bool usesTransform = false;
        var imageIndex = new Dictionary<string, int>();
        foreach (var material in materials)
        {
            var (r, g, b) = ExportScene.ParseColor(material.Color);
            var sb = new StringBuilder();
            sb.Append($"{{\"name\":{JsonSerializer.Serialize(material.Name)},\"doubleSided\":true,");
            if (material.Opacity < 0.999f)
                sb.Append("\"alphaMode\":\"BLEND\",");
            sb.Append($"\"pbrMetallicRoughness\":{{\"baseColorFactor\":[{F(Linear(r))},{F(Linear(g))},{F(Linear(b))},{F(material.Opacity)}],");
            sb.Append($"\"metallicFactor\":{F(material.Metallic)},\"roughnessFactor\":{F(material.Roughness)}");
            if (!string.IsNullOrEmpty(material.Texture) && File.Exists(material.Texture))
            {
                if (!imageIndex.TryGetValue(material.Texture, out int image))
                {
                    image = images.Count;
                    imageIndex[material.Texture] = image;
                    if (binary)
                    {
                        int view = AddView(File.ReadAllBytes(material.Texture), null);
                        images.Add($"{{\"bufferView\":{view},\"mimeType\":\"{TextureFiles.MimeType(material.Texture)}\"}}");
                    }
                    else
                    {
                        string name = TextureFiles.CopyNextTo(material.Texture, folder)!;
                        images.Add($"{{\"uri\":{JsonSerializer.Serialize(Uri.EscapeDataString(name))}}}");
                    }
                    textures.Add($"{{\"source\":{image},\"sampler\":0}}");
                }
                sb.Append($",\"baseColorTexture\":{{\"index\":{image}");
                if (MathF.Abs(material.Repeat - 1) > 1e-4f)
                {
                    usesTransform = true;
                    sb.Append($",\"extensions\":{{\"KHR_texture_transform\":{{\"scale\":[{F(material.Repeat)},{F(material.Repeat)}]}}}}");
                }
                sb.Append('}');
            }
            sb.Append("}}");
            materialJson.Add(sb.ToString());
        }

        while (buffer.Length % 4 != 0)
            buffer.WriteByte(0);
        var json = new StringBuilder();
        json.Append("{\"asset\":{\"version\":\"2.0\",\"generator\":\"Seamstress\"},");
        if (usesTransform)
            json.Append("\"extensionsUsed\":[\"KHR_texture_transform\"],");
        json.Append("\"scene\":0,\"scenes\":[{\"name\":\"Scene\",\"nodes\":[");
        json.Append(string.Join(",", Enumerable.Range(0, scene.Meshes.Count)));
        json.Append("]}],\"nodes\":[");
        json.Append(string.Join(",", scene.Meshes.Select((m, i) => $"{{\"name\":{JsonSerializer.Serialize(m.Name)},\"mesh\":{i}}}")));
        json.Append("],\"meshes\":[").Append(string.Join(",", meshJson)).Append("],");
        json.Append("\"materials\":[").Append(string.Join(",", materialJson)).Append("],");
        if (textures.Count > 0)
        {
            json.Append("\"textures\":[").Append(string.Join(",", textures)).Append("],");
            json.Append("\"images\":[").Append(string.Join(",", images)).Append("],");
            json.Append("\"samplers\":[{\"magFilter\":9729,\"minFilter\":9987,\"wrapS\":10497,\"wrapT\":10497}],");
        }
        json.Append("\"accessors\":[").Append(string.Join(",", accessors)).Append("],");
        json.Append("\"bufferViews\":[");
        json.Append(string.Join(",", views.Select(v => $"{{\"buffer\":0,\"byteOffset\":{v.Offset},\"byteLength\":{v.Length}"
            + (v.Target is { } target ? $",\"target\":{target}}}" : "}"))));
        json.Append("],\"buffers\":[{\"byteLength\":").Append(buffer.Length);
        if (!binary)
            json.Append(",\"uri\":").Append(JsonSerializer.Serialize(Uri.EscapeDataString(binName)));
        json.Append("}]}");

        if (!binary)
        {
            File.WriteAllText(path, json.ToString());
            File.WriteAllBytes(Path.Combine(folder, binName), buffer.ToArray());
            return;
        }

        var jsonBytes = new List<byte>(Encoding.UTF8.GetBytes(json.ToString()));
        while (jsonBytes.Count % 4 != 0)
            jsonBytes.Add((byte)' ');
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write(0x46546C67u); // "glTF"
        w.Write(2u);
        w.Write((uint)(12 + 8 + jsonBytes.Count + 8 + buffer.Length));
        w.Write((uint)jsonBytes.Count);
        w.Write(0x4E4F534Au); // "JSON"
        w.Write(jsonBytes.ToArray());
        w.Write((uint)buffer.Length);
        w.Write(0x004E4942u); // "BIN"
        w.Write(buffer.ToArray());
    }

    /// <summary>glTF colour factors are linear; the UI colour is sRGB.</summary>
    private static float Linear(float srgb) => srgb <= 0.04045f ? srgb / 12.92f : MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);
}
