using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Seamstress.Core;
using Seamstress.Core.Editing;
using Seamstress.Core.IO;
using Seamstress.Core.IO.Engines;
using Xunit;

namespace Seamstress.Tests;

public class EngineExportTests : IDisposable
{
    private readonly string _folder;
    private readonly Workspace _ws = new();
    private readonly string _texture;

    public EngineExportTests()
    {
        _folder = Environment.GetEnvironmentVariable("SEAMSTRESS_DUMP") is { } dump
            ? Path.Combine(dump, "engine-export")
            : Path.Combine(Path.GetTempPath(), "seamstress-engine-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        Directory.CreateDirectory(Path.Combine(_folder, "src"));
        _texture = Path.Combine(_folder, "src", "checker.png");
        PngWriter.WriteChecker(_texture, 64, 4);

        _ws.AddTemplate("T-Shirt");
        _ws.AddTemplate("Skirt");
        _ws.Document.Materials[0].Texture = _texture;
        _ws.Document.Materials[0].Repeat = 2;
    }

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("SEAMSTRESS_DUMP") == null)
            Directory.Delete(_folder, true);
    }

    private ExportScene Scene()
        => ExportScene.Build(_ws.Document, _ws.MeshFor, new ExportOptions { WeldSeams = true });

    [Fact]
    public void GodotBundleContainsAReadyScene()
    {
        var result = EngineExport.Export(_ws.Document, _ws.MeshFor, EngineTarget.Godot, _folder,
            new EngineExportOptions { BundleName = "../Summer Outfit" });

        Assert.Equal(EngineTarget.Godot, result.Target);
        Assert.Equal(Path.Combine(_folder, "Summer_Outfit"), result.OutputPath);
        Assert.True(Directory.Exists(result.OutputPath));
        Assert.Equal(2, result.GarmentCount);
        Assert.True(result.TriangleCount > 0);

        var files = Directory.GetFiles(result.OutputPath).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new[] { "README.md", "Summer_Outfit.glb", "Summer_Outfit.tscn" }, files.OrderBy(f => f));

        var glb = File.ReadAllBytes(Path.Combine(result.OutputPath, "Summer_Outfit.glb"));
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(glb, 0));
        Assert.Equal((uint)glb.Length, BitConverter.ToUInt32(glb, 8));

        string scene = File.ReadAllText(Path.Combine(result.OutputPath, "Summer_Outfit.tscn"));
        Assert.Contains("[gd_scene format=3]", scene);
        Assert.Contains("[ext_resource type=\"PackedScene\" path=\"Summer_Outfit.glb\" id=\"1_garment\"]", scene);
        Assert.Contains("[node name=\"Summer_Outfit\" type=\"Node3D\"]", scene);
        Assert.Contains("instance=ExtResource(\"1_garment\")", scene);

        string readme = File.ReadAllText(Path.Combine(result.OutputPath, "README.md"));
        Assert.Contains("Godot 4", readme);
        Assert.Contains("Summer_Outfit.glb", readme);
        Assert.Contains("Summer_Outfit.tscn", readme);
    }

    [Fact]
    public void TexturePlanKeepsSameNamedTexturesApart()
    {
        string a = Path.Combine(_folder, "a", "checker.png");
        string b = Path.Combine(_folder, "b", "checker.png");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        PngWriter.WriteChecker(a, 32, 2);
        PngWriter.WriteChecker(b, 48, 3);
        _ws.Document.Materials[0].Texture = a;
        _ws.Document.Materials[1].Texture = b;

        var scene = Scene();
        var plan = EngineTexturePlan.Build(scene);
        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal(new[] { "Textures/checker.png", "Textures/checker_2.png" },
            plan.Entries.Select(e => e.RelativePath).OrderBy(p => p, StringComparer.Ordinal));

        string bundle = Path.Combine(_folder, "textures");
        plan.CopyTo(bundle);
        Assert.True(File.Exists(Path.Combine(bundle, "Textures", "checker.png")));
        Assert.True(File.Exists(Path.Combine(bundle, "Textures", "checker_2.png")));
        Assert.Equal(plan.Entries[0].RelativePath, plan.ByMaterial[_ws.Document.Materials[0]]);
        Assert.Equal(plan.Entries[1].RelativePath, plan.ByMaterial[_ws.Document.Materials[1]]);
    }

    [Fact]
    public void TexturePlanSharesOneFileUsedBySeveralMaterials()
    {
        _ws.Document.Materials[1].Texture = _texture;

        var plan = EngineTexturePlan.Build(Scene());
        Assert.Single(plan.Entries);
        Assert.Equal("Textures/checker.png", plan.Entries[0].RelativePath);
        Assert.Equal(plan.Entries[0].RelativePath, plan.ByMaterial[_ws.Document.Materials[0]]);
        Assert.Equal(plan.Entries[0].RelativePath, plan.ByMaterial[_ws.Document.Materials[1]]);
    }

    [Fact]
    public void FbxPortableExportDoesNotLeakMachinePaths()
    {
        var scene = Scene();
        string bundle = Path.Combine(_folder, "fbx");
        Directory.CreateDirectory(bundle);
        var plan = EngineTexturePlan.Build(scene);
        plan.CopyTo(bundle);

        string path = Path.Combine(bundle, "outfit.fbx");
        FbxExporter.Export(scene, path, plan.ByMaterial, portableTexturePaths: true);
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(path));

        Assert.Contains("Textures/checker.png", text);
        Assert.DoesNotContain(_folder, text);
    }

    [Fact]
    public void O3deBundleContainsPortableFbxTexturesAndMaterials()
    {
        _ws.Document.Materials[1].Opacity = 0.5f;
        var result = EngineExport.Export(_ws.Document, _ws.MeshFor, EngineTarget.O3de, _folder,
            new EngineExportOptions { BundleName = "Summer Outfit" });

        Assert.Equal(EngineTarget.O3de, result.Target);
        Assert.Equal(Path.Combine(_folder, "Summer_Outfit"), result.OutputPath);
        var files = Directory.GetFiles(result.OutputPath, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(result.OutputPath, p).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Summer_Outfit.fbx", files);
        Assert.Contains("README.md", files);
        Assert.Contains("Textures/checker.png", files);
        Assert.Contains("Materials/Shirt_fabric.material", files);
        Assert.Contains("Materials/Skirt_fabric.material", files);

        string fbx = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(result.OutputPath, "Summer_Outfit.fbx")));
        Assert.Contains("Textures/checker.png", fbx);
        Assert.Contains("Shirt_fabric", fbx);
        Assert.DoesNotContain(_folder, fbx);

        string shirtPath = Path.Combine(result.OutputPath, "Materials", "Shirt_fabric.material");
        using var shirt = JsonDocument.Parse(File.ReadAllText(shirtPath));
        var shirtRoot = shirt.RootElement;
        Assert.Equal("Materials/Types/StandardPBR.materialtype", shirtRoot.GetProperty("materialType").GetString());
        Assert.Equal(5, shirtRoot.GetProperty("materialTypeVersion").GetInt32());
        var shirtProps = shirtRoot.GetProperty("propertyValues");
        Assert.True(shirtProps.GetProperty("baseColor.useTexture").GetBoolean());
        Assert.Equal("../Textures/checker.png", shirtProps.GetProperty("baseColor.textureMap").GetString());
        Assert.Equal(4, shirtProps.GetProperty("baseColor.color").GetArrayLength());
        Assert.Equal(1f, shirtProps.GetProperty("baseColor.color")[3].GetSingle());
        Assert.Equal(2f, shirtProps.GetProperty("uv.tileU").GetSingle());
        Assert.Equal(2f, shirtProps.GetProperty("uv.tileV").GetSingle());
        Assert.Equal("Opaque", shirtProps.GetProperty("opacity.mode").GetString());
        Assert.True(shirtProps.GetProperty("general.doubleSided").GetBoolean());
        Assert.False(shirtProps.GetProperty("metallic.useTexture").GetBoolean());
        Assert.False(shirtProps.GetProperty("roughness.useTexture").GetBoolean());

        string skirtPath = Path.Combine(result.OutputPath, "Materials", "Skirt_fabric.material");
        using var skirt = JsonDocument.Parse(File.ReadAllText(skirtPath));
        var skirtProps = skirt.RootElement.GetProperty("propertyValues");
        Assert.False(skirtProps.GetProperty("baseColor.useTexture").GetBoolean());
        Assert.False(skirtProps.TryGetProperty("baseColor.textureMap", out _));
        Assert.False(skirtProps.TryGetProperty("uv.tileU", out _));
        Assert.Equal("Blended", skirtProps.GetProperty("opacity.mode").GetString());
        Assert.Equal("None", skirtProps.GetProperty("opacity.alphaSource").GetString());
        Assert.Equal(0.5, skirtProps.GetProperty("opacity.factor").GetDouble(), 3);

        string texturePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shirtPath)!,
            shirtProps.GetProperty("baseColor.textureMap").GetString()!.Replace('/', Path.DirectorySeparatorChar)));
        Assert.True(File.Exists(texturePath));
        Assert.Equal(File.ReadAllBytes(_texture), File.ReadAllBytes(texturePath));

        string readme = File.ReadAllText(Path.Combine(result.OutputPath, "README.md"));
        Assert.Contains("O3DE", readme);
        Assert.Contains("Assets/Seamstress/Summer_Outfit/", readme);
    }

    [Fact]
    public void O3deMaterialPropertiesMatchInstalledStandardPbr()
    {
        string? assets = O3deAssetsRoot();
        if (assets == null)
            return;
        string materialTypePath = Path.Combine(assets, "Materials", "Types", "StandardPBR.materialtype");
        if (!File.Exists(materialTypePath))
            return;
        var (version, allowed) = StandardPbrProperties(materialTypePath);

        var result = EngineExport.Export(_ws.Document, _ws.MeshFor, EngineTarget.O3de, _folder,
            new EngineExportOptions { BundleName = "checked" });
        foreach (string path in Directory.GetFiles(Path.Combine(result.OutputPath, "Materials"), "*.material"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(version, doc.RootElement.GetProperty("materialTypeVersion").GetInt32());
            foreach (var property in doc.RootElement.GetProperty("propertyValues").EnumerateObject())
                Assert.True(allowed.Contains(property.Name),
                    $"{Path.GetFileName(path)}: '{property.Name}' is not a StandardPBR property");
        }
    }

    private static string? O3deAssetsRoot()
    {
        string root = Environment.GetEnvironmentVariable("SEAMSTRESS_O3DE") ?? "/opt/O3DE/26.10";
        string assets = Path.Combine(root, "Gems", "Atom", "Feature", "Common", "Assets");
        return Directory.Exists(assets) ? assets : null;
    }

    /// <summary>The installed StandardPBR material type's version and every property id it defines.</summary>
    private static (int Version, HashSet<string> Properties) StandardPbrProperties(string materialTypePath)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var materialType = JsonDocument.Parse(File.ReadAllText(materialTypePath), options);
        int version = materialType.RootElement.GetProperty("version").GetInt32();
        var properties = new HashSet<string>(StringComparer.Ordinal);
        string folder = Path.GetDirectoryName(materialTypePath)!;
        foreach (var group in materialType.RootElement.GetProperty("propertyLayout").GetProperty("propertyGroups").EnumerateArray())
        {
            if (!group.TryGetProperty("$import", out var import))
                continue;
            string path = Path.Combine(folder, import.GetString()!.Replace('/', Path.DirectorySeparatorChar));
            using var groupFile = JsonDocument.Parse(File.ReadAllText(path), options);
            string name = groupFile.RootElement.GetProperty("name").GetString()!;
            foreach (var property in groupFile.RootElement.GetProperty("properties").EnumerateArray())
                properties.Add(name + "." + property.GetProperty("name").GetString());
        }
        return (version, properties);
    }

    [Fact]
    public void UnityPackageContainsReadyGarmentAssets()
    {
        _ws.Document.Materials[1].Opacity = 0.5f;
        var result = EngineExport.Export(_ws.Document, _ws.MeshFor, EngineTarget.Unity, _folder,
            new EngineExportOptions { BundleName = "Summer Outfit" });

        Assert.Equal(EngineTarget.Unity, result.Target);
        Assert.Equal(Path.Combine(_folder, "Summer_Outfit.unitypackage"), result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));

        var package = ReadUnityPackage(result.OutputPath);
        const string fbx = "Assets/Seamstress/Summer_Outfit/Summer_Outfit.fbx";
        const string texture = "Assets/Seamstress/Summer_Outfit/Textures/checker.png";
        const string shirt = "Assets/Seamstress/Summer_Outfit/Materials/Shirt_fabric.mat";
        const string skirt = "Assets/Seamstress/Summer_Outfit/Materials/Skirt_fabric.mat";
        const string manifest = "Assets/Seamstress/Summer_Outfit/seamstress-garment.json";
        const string readme = "Assets/Seamstress/Summer_Outfit/README.md";
        const string opaqueShader = "Assets/Seamstress/Shaders/SeamstressCloth.shader";
        const string transparentShader = "Assets/Seamstress/Shaders/SeamstressClothTransparent.shader";
        const string helper = "Assets/Seamstress/Editor/SeamstressGarmentImport.cs";

        foreach (string path in new[] { fbx, texture, shirt, skirt, manifest, readme, opaqueShader, transparentShader, helper })
            Assert.True(package.ContainsKey(path), "missing " + path);

        foreach (var item in package.Values)
        {
            Assert.Matches("^[a-f0-9]{32}$", item.Guid);
            Assert.Contains("guid: " + item.Guid, item.Meta);
        }

        string fbxText = Encoding.UTF8.GetString(package[fbx].Asset!);
        Assert.Contains("Textures/checker.png", fbxText);
        Assert.DoesNotContain(_folder, fbxText);
        Assert.Contains("TextureImporter:", package[texture].Meta);
        Assert.Contains("textureShape: 1", package[texture].Meta);

        string shirtText = Encoding.UTF8.GetString(package[shirt].Asset!);
        Assert.Contains("m_Shader: {fileID: 4800000, guid: " + package[opaqueShader].Guid + ", type: 3}", shirtText);
        Assert.Contains("m_Texture: {fileID: 2800000, guid: " + package[texture].Guid + ", type: 3}", shirtText);
        Assert.Contains("_Glossiness: 0.2", shirtText);
        Assert.Contains("_Metallic: 0", shirtText);
        Assert.Contains("r: 0.309804", shirtText);

        string skirtText = Encoding.UTF8.GetString(package[skirt].Asset!);
        Assert.Contains("m_Shader: {fileID: 4800000, guid: " + package[transparentShader].Guid + ", type: 3}", skirtText);
        Assert.Contains("a: 0.5", skirtText);
        Assert.Contains("m_TexEnvs: []", skirtText);

        Assert.Contains("Cull Off", Encoding.UTF8.GetString(package[opaqueShader].Asset!));
        Assert.Contains("Cull Off", Encoding.UTF8.GetString(package[transparentShader].Asset!));
        Assert.Contains("alpha:fade", Encoding.UTF8.GetString(package[transparentShader].Asset!));
        Assert.Contains("AddRemap", Encoding.UTF8.GetString(package[helper].Asset!));
        using var manifestJson = JsonDocument.Parse(Encoding.UTF8.GetString(package[manifest].Asset!));
        var manifestMaterials = manifestJson.RootElement.GetProperty("materials");
        Assert.Equal(2, manifestMaterials.GetArrayLength());
        var shirtEntry = manifestMaterials.EnumerateArray().First(m => m.GetProperty("name").GetString() == "Shirt_fabric");
        Assert.Equal("Textures/checker.png", shirtEntry.GetProperty("texture").GetString());
        Assert.Equal(2, shirtEntry.GetProperty("repeat").GetSingle());
        var skirtEntry = manifestMaterials.EnumerateArray().First(m => m.GetProperty("name").GetString() == "Skirt_fabric");
        Assert.Equal("", skirtEntry.GetProperty("texture").GetString());
        Assert.Equal(0.5, skirtEntry.GetProperty("opacity").GetDouble(), 3);

        Assert.Contains("Unity 2020.1+", Encoding.UTF8.GetString(package[readme].Asset!));
    }

    [Fact]
    public void UnityPackageUsesStableGuids()
    {
        var options = new EngineExportOptions { BundleName = "Summer Outfit" };
        var scene = Scene();
        var first = EngineExport.Export(scene, EngineTarget.Unity, Path.Combine(_folder, "first.unitypackage"), options);
        var second = EngineExport.Export(scene, EngineTarget.Unity, Path.Combine(_folder, "second.unitypackage"), options);
        var a = ReadUnityPackage(first.OutputPath);
        var b = ReadUnityPackage(second.OutputPath);

        Assert.Equal(a.Count, b.Count);
        foreach (var pair in a)
        {
            Assert.True(b.TryGetValue(pair.Key, out var other), "missing " + pair.Key);
            Assert.Equal(pair.Value.Guid, other.Guid);
            Assert.Equal(pair.Value.Meta, other.Meta);
            if (!pair.Key.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                Assert.Equal(pair.Value.Asset, other.Asset);
        }
    }

    private static Dictionary<string, (string Guid, string Meta, byte[]? Asset)> ReadUnityPackage(string path)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var file = File.OpenRead(path))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.DataStream == null)
                    continue;
                using var data = new MemoryStream();
                entry.DataStream.CopyTo(data);
                entries[entry.Name] = data.ToArray();
            }
        }

        var result = new Dictionary<string, (string Guid, string Meta, byte[]? Asset)>(StringComparer.Ordinal);
        foreach (var pair in entries)
        {
            if (!pair.Key.EndsWith("/pathname", StringComparison.Ordinal))
                continue;
            int slash = pair.Key.IndexOf('/');
            string guid = pair.Key.Substring(0, slash);
            string assetPath = Encoding.UTF8.GetString(pair.Value).TrimEnd('\n');
            byte[]? asset = entries.TryGetValue(guid + "/asset", out var bytes) ? bytes : null;
            string meta = entries.TryGetValue(guid + "/asset.meta", out var metaBytes)
                ? Encoding.UTF8.GetString(metaBytes)
                : "";
            result[assetPath] = (guid, meta, asset);
        }
        return result;
    }

    [Fact]
    public void UnimplementedEngineTargetsFailClearly()
    {
        var scene = Scene();
        var options = new EngineExportOptions { BundleName = "outfit" };
        var error = Assert.Throws<NotSupportedException>(() => EngineExport.Export(scene, EngineTarget.Unreal, _folder, options));
        Assert.Contains("Unreal", error.Message);
        Assert.Contains("not available yet", error.Message);
    }
}
