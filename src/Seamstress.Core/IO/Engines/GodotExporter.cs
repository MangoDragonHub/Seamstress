using System.Text;

namespace Seamstress.Core.IO.Engines;

/// <summary>Writes a Godot 4 bundle: a GLB mesh plus a scene that instances it.</summary>
internal static class GodotExporter
{
    public static EngineExportResult Export(ExportScene scene, string bundleFolder, string bundleName)
    {
        var files = new List<string>();

        string glbPath = Path.Combine(bundleFolder, bundleName + ".glb");
        GltfExporter.Export(scene, glbPath);
        files.Add(glbPath);

        string scenePath = Path.Combine(bundleFolder, bundleName + ".tscn");
        files.Add(EngineBundle.WriteText(scenePath, BuildScene(bundleName)));

        string readmePath = Path.Combine(bundleFolder, "README.md");
        files.Add(EngineBundle.WriteText(readmePath, BuildReadme(scene, bundleName)));

        return new EngineExportResult(
            EngineTarget.Godot,
            bundleFolder,
            files,
            scene.Meshes.Count,
            EngineBundle.TriangleCount(scene),
            EngineBundle.FaceCount(scene));
    }

    private static string BuildScene(string bundleName)
    {
        string glb = GodotString(bundleName + ".glb");
        string root = GodotString(bundleName);
        return $"""
            ; Exported by Seamstress. Static draped garment; no skeleton or animation.
            [gd_scene format=3]

            [ext_resource type="PackedScene" path="{glb}" id="1_garment"]

            [node name="{root}" type="Node3D"]

            [node name="Model" parent="." instance=ExtResource("1_garment")]
            """;
    }

    private static string BuildReadme(ExportScene scene, string bundleName)
    {
        string glb = bundleName + ".glb";
        string tscn = bundleName + ".tscn";
        var sb = new StringBuilder();
        sb.Append("# ").Append(EngineBundle.DisplayName(EngineBundle.SceneName(scene))).Append(" — Godot 4 garment\n\n");
        sb.Append("Exported from Seamstress. This is a static draped garment; it has no skeleton or animation.\n\n");
        sb.Append("## Files\n\n");
        sb.Append("- `").Append(glb).Append("` — mesh, UVs, normals and PBR materials. Textures are embedded.\n");
        sb.Append("- `").Append(tscn).Append("` — a scene that instances the GLB under a `Node3D`.\n\n");
        sb.Append("## Import\n\n");
        sb.Append("1. Copy this folder into your Godot project, for example `res://garments/").Append(bundleName).Append("/`.\n");
        sb.Append("2. Open the project in Godot 4 so it imports the GLB.\n");
        sb.Append("3. Open `").Append(tscn).Append("`, or drag `").Append(glb).Append("` into an open scene.\n\n");
        sb.Append("The garment is exported at its simulated size in metres, Y up. Materials keep their colour, opacity, ");
        sb.Append("metallic, roughness, texture repeat and double-sided rendering.\n");
        return sb.ToString();
    }

    private static string GodotString(string text)
        => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
}
