using System.Numerics;
using System.Text;
using System.Text.Json;
using Seamstress.Core;
using Seamstress.Core.Editing;
using Seamstress.Core.Geometry;
using Seamstress.Core.IO;
using Seamstress.Core.Model;
using Seamstress.Core.Simulation;
using Xunit;

namespace Seamstress.Tests;

public class ExportTests : IDisposable
{
    private readonly string _folder;
    private readonly Workspace _ws = new();
    private readonly string _texture;

    public ExportTests()
    {
        _folder = Environment.GetEnvironmentVariable("SEAMSTRESS_DUMP") is { } dump
            ? Path.Combine(dump, "export")
            : Path.Combine(Path.GetTempPath(), "seamstress-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        Directory.CreateDirectory(Path.Combine(_folder, "src"));
        _texture = Path.Combine(_folder, "src", "checker.png");
        PngWriter.WriteChecker(_texture, 256, 8);
        _ws.AddTemplate("T-Shirt");
        _ws.AddTemplate("Skirt");
        _ws.Document.Materials[0].Texture = _texture;
        _ws.Document.Materials[0].Repeat = 2;
        // Close the seams (without an avatar the pieces simply meet in the middle) so welding has something to merge.
        var sim = new SimulationController(_ws, () => null);
        _ws.Document.Simulation.Gravity = 0;
        sim.Start(true);
        sim.RunFrames(90);
        sim.Stop();
    }

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("SEAMSTRESS_DUMP") == null)
            Directory.Delete(_folder, true);
    }

    private ExportScene Scene(bool weld = true)
        => ExportScene.Build(_ws.Document, _ws.MeshFor, new ExportOptions { WeldSeams = weld });

    [Fact]
    public void WeldingMergesSeamVertices()
    {
        var welded = Scene(true);
        var loose = Scene(false);
        Assert.Equal(2, welded.Meshes.Count);
        var shirt = welded.Meshes[0];
        Assert.Equal(_ws.MeshFor(_ws.Document.Garments[0]).TriangleCount, shirt.TriangleCount);
        Assert.True(shirt.Positions.Length < loose.Meshes[0].Positions.Length);
        Assert.Equal(loose.Meshes[0].Uvs.Length, shirt.Uvs.Length);
        Assert.All(shirt.Uvs, uv => { Assert.InRange(uv.X, 0, 1); Assert.InRange(uv.Y, 0, 1); });
        Assert.All(shirt.Normals, n => Assert.InRange(n.Length(), 0.99f, 1.01f));
        Assert.Equal(4, shirt.Parts.Count);
    }

    [Fact]
    public void ObjContainsAllGeometryAndMaterials()
    {
        var scene = Scene();
        string path = Path.Combine(_folder, "outfit.obj");
        ObjExporter.Export(scene, path);
        var lines = File.ReadAllLines(path);
        Assert.Equal(scene.Meshes.Sum(m => m.Positions.Length), lines.Count(l => l.StartsWith("v ")));
        Assert.Equal(scene.Meshes.Sum(m => m.Uvs.Length), lines.Count(l => l.StartsWith("vt ")));
        Assert.Equal(scene.Meshes.Sum(m => m.TriangleCount), lines.Count(l => l.StartsWith("f ")));
        Assert.Equal(2, lines.Count(l => l.StartsWith("o ")));
        string mtl = File.ReadAllText(Path.Combine(_folder, "outfit.mtl"));
        Assert.Contains("map_Kd -s 2 2 1 checker.png", mtl);
        Assert.True(File.Exists(Path.Combine(_folder, "checker.png")));
    }

    [Fact]
    public void GlbIsWellFormed()
    {
        var scene = Scene();
        string path = Path.Combine(_folder, "outfit.glb");
        GltfExporter.Export(scene, path);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(bytes, 0));
        Assert.Equal((uint)bytes.Length, BitConverter.ToUInt32(bytes, 8));
        int jsonLength = BitConverter.ToInt32(bytes, 12);
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(bytes, 20, jsonLength));
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("meshes").GetArrayLength());
        Assert.Equal("KHR_texture_transform", root.GetProperty("extensionsUsed")[0].GetString());
        int binLength = BitConverter.ToInt32(bytes, 20 + jsonLength);
        Assert.Equal(root.GetProperty("buffers")[0].GetProperty("byteLength").GetInt32(), binLength);
        foreach (var view in root.GetProperty("bufferViews").EnumerateArray())
            Assert.True(view.GetProperty("byteOffset").GetInt32() + view.GetProperty("byteLength").GetInt32() <= binLength);
        // Index counts add up to the scene's triangles.
        int indices = root.GetProperty("meshes").EnumerateArray().SelectMany(m => m.GetProperty("primitives").EnumerateArray())
            .Sum(p => root.GetProperty("accessors")[p.GetProperty("indices").GetInt32()].GetProperty("count").GetInt32());
        Assert.Equal(scene.Meshes.Sum(m => m.TriangleCount) * 3, indices);

        GltfExporter.Export(scene, Path.Combine(_folder, "outfit_text.gltf"));
        Assert.True(File.Exists(Path.Combine(_folder, "outfit_text.bin")));
    }

    /// <summary>Reads the node tree of a binary FBX back, checking every offset on the way.</summary>
    private static Dictionary<string, int> ReadFbx(byte[] data, out List<string> names)
    {
        var counts = new Dictionary<string, int>();
        var all = new List<string>();
        Assert.Equal("Kaydara FBX Binary  ", Encoding.ASCII.GetString(data, 0, 20));
        Assert.Equal(7400u, BitConverter.ToUInt32(data, 23));
        int ReadNode(int offset, string path)
        {
            uint end = BitConverter.ToUInt32(data, offset);
            uint propertyCount = BitConverter.ToUInt32(data, offset + 4);
            uint propertyLength = BitConverter.ToUInt32(data, offset + 8);
            int nameLength = data[offset + 12];
            if (end == 0)
            {
                Assert.Equal(0u, propertyCount);
                return offset + 13;
            }
            string name = Encoding.ASCII.GetString(data, offset + 13, nameLength);
            string full = path + "/" + name;
            all.Add(full);
            counts[name] = counts.GetValueOrDefault(name) + 1;
            int p = offset + 13 + nameLength;
            int propertiesEnd = p + (int)propertyLength;
            for (int i = 0; i < propertyCount; i++)
            {
                char type = (char)data[p++];
                switch (type)
                {
                    case 'C': p += 1; break;
                    case 'Y': p += 2; break;
                    case 'I': case 'F': p += 4; break;
                    case 'L': case 'D': p += 8; break;
                    case 'S': case 'R': p += 4 + BitConverter.ToInt32(data, p); break;
                    case 'd': case 'f': case 'i': case 'l':
                        {
                            int count = BitConverter.ToInt32(data, p), encoding = BitConverter.ToInt32(data, p + 4), length = BitConverter.ToInt32(data, p + 8);
                            if (encoding == 1)
                            {
                                using var z = new System.IO.Compression.ZLibStream(new MemoryStream(data, p + 12, length), System.IO.Compression.CompressionMode.Decompress);
                                using var raw = new MemoryStream();
                                z.CopyTo(raw);
                                Assert.Equal(count * (type is 'd' or 'l' ? 8 : 4), raw.Length);
                            }
                            p += 12 + length;
                            break;
                        }
                    default: Assert.Fail($"unknown property type '{type}' in {full}"); break;
                }
            }
            Assert.Equal(propertiesEnd, p);
            while (p < end)
            {
                int next = ReadNode(p, full);
                bool terminator = BitConverter.ToUInt32(data, p) == 0;
                p = next;
                if (terminator)
                    break;
            }
            Assert.Equal((int)end, p);
            return p;
        }
        int position = 27;
        while (BitConverter.ToUInt32(data, position) != 0)
            position = ReadNode(position, "");
        names = all;
        return counts;
    }

    [Fact]
    public void FbxTreeIsConsistent()
    {
        var scene = Scene();
        string path = Path.Combine(_folder, "outfit.fbx");
        FbxExporter.Export(scene, path);
        var data = File.ReadAllBytes(path);
        var counts = ReadFbx(data, out var names);
        Assert.Equal(2, counts["Geometry"]);
        Assert.Equal(2, counts["Model"]);
        Assert.Equal(2, counts["Material"]);
        Assert.Equal(1, counts["Texture"]);
        Assert.Contains("/Objects/Geometry/LayerElementUV/UVIndex", names);
        Assert.Contains("/GlobalSettings/Properties70/P", names);
        // geometry->model, model->root and material->model per mesh, plus video->texture and texture->material.
        Assert.Equal(2 * 3 + 2, counts["C"]);
        // The file ends with the version, 120 zero bytes and the 16-byte footer magic.
        Assert.Equal(0xf8, data[^16]);
        Assert.Equal(7400u, BitConverter.ToUInt32(data, data.Length - 16 - 120 - 4));
    }

    [Fact]
    public void MakeHumanAssetReproducesTheGarment()
    {
        var settings = new AppSettings();
        settings.DetectMakeHuman();
        if (!File.Exists(settings.MakeHumanBaseMesh))
            return; // MakeHuman is not installed on this machine
        var avatar = Avatar.FromMakeHumanBase(settings.MakeHumanBaseMesh);
        Assert.NotNull(avatar.MakeHuman);
        Assert.InRange(avatar.Mesh.Bounds().Max.Y, 1.5f, 1.9f);

        var mesh = Scene().Meshes[1];
        var result = MakeHumanExporter.Export(mesh, avatar.MakeHuman!, Path.Combine(_folder, "clothes"), "tester");
        var lines = File.ReadAllLines(result.MhcloPath);
        Assert.Contains("basemesh hm08", lines);
        int start = Array.IndexOf(lines, "verts 0") + 1;
        Assert.Equal(result.VertexCount, lines.Length - start);
        var objVerts = File.ReadAllLines(Path.ChangeExtension(result.MhcloPath, ".obj")).Where(l => l.StartsWith("v ")).ToArray();
        Assert.Equal(result.VertexCount, objVerts.Length);

        // Rebuild each vertex the way MakeHuman does: weighted base vertices plus offset.
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var baseVerts = avatar.MakeHuman!.Vertices;
        for (int i = 0; i < result.VertexCount; i += 7)
        {
            var f = lines[start + i].Split(' ');
            var rebuilt = baseVerts[int.Parse(f[0])] * float.Parse(f[3], inv) + baseVerts[int.Parse(f[1])] * float.Parse(f[4], inv)
                        + baseVerts[int.Parse(f[2])] * float.Parse(f[5], inv)
                        + new Vector3(float.Parse(f[6], inv), float.Parse(f[7], inv), float.Parse(f[8], inv));
            var o = objVerts[i].Split(' ');
            var expected = new Vector3(float.Parse(o[1], inv), float.Parse(o[2], inv), float.Parse(o[3], inv));
            Assert.True(Vector3.Distance(rebuilt, expected) < 0.002f, $"vertex {i}: {rebuilt} vs {expected}");
        }
    }
}
