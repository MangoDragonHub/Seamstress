using System.Text;
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
    public void UnimplementedEngineTargetsFailClearly()
    {
        var scene = Scene();
        var options = new EngineExportOptions { BundleName = "outfit" };
        var error = Assert.Throws<NotSupportedException>(() => EngineExport.Export(scene, EngineTarget.Unity, _folder, options));
        Assert.Contains("Unity", error.Message);
        Assert.Contains("not available yet", error.Message);
    }
}
