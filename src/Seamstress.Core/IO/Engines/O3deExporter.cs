using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Seamstress.Core.Model;

namespace Seamstress.Core.IO.Engines;

/// <summary>Writes an O3DE 26.10 bundle: an FBX, copied textures and StandardPBR material files.</summary>
internal static class O3deExporter
{
    private const string MaterialType = "Materials/Types/StandardPBR.materialtype";
    private const int MaterialTypeVersion = 5;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static EngineExportResult Export(ExportScene scene, string bundleFolder, string bundleName)
    {
        var files = new List<string>();
        var textures = EngineTexturePlan.Build(scene);
        files.AddRange(textures.CopyTo(bundleFolder));

        string fbxPath = Path.Combine(bundleFolder, bundleName + ".fbx");
        FbxExporter.Export(scene, fbxPath, textures.ByMaterial, portableTexturePaths: true);
        files.Add(fbxPath);

        var names = ObjExporter.MaterialNames(scene);
        foreach (var material in scene.Materials())
        {
            string materialPath = Path.Combine(bundleFolder, "Materials", names[material] + ".material");
            files.Add(EngineBundle.WriteText(materialPath, BuildMaterial(material, textures)));
        }

        string readmePath = Path.Combine(bundleFolder, "README.md");
        files.Add(EngineBundle.WriteText(readmePath, BuildReadme(scene, bundleName)));

        return new EngineExportResult(
            EngineTarget.O3de,
            bundleFolder,
            files,
            scene.Meshes.Count,
            EngineBundle.TriangleCount(scene),
            EngineBundle.FaceCount(scene));
    }

    private static string BuildMaterial(FabricMaterial material, EngineTexturePlan textures)
    {
        var (r, g, b) = ExportScene.ParseColor(material.Color);
        var properties = new JsonObject
        {
            ["baseColor.color"] = new JsonArray
            {
                JsonValue.Create(Linear(r)),
                JsonValue.Create(Linear(g)),
                JsonValue.Create(Linear(b)),
                JsonValue.Create(1.0f),
            },
            ["metallic.factor"] = JsonValue.Create(material.Metallic),
            ["metallic.useTexture"] = JsonValue.Create(false),
            ["roughness.factor"] = JsonValue.Create(material.Roughness),
            ["roughness.useTexture"] = JsonValue.Create(false),
            ["general.doubleSided"] = JsonValue.Create(true),
        };

        if (textures.ByMaterial.TryGetValue(material, out string? texture))
        {
            properties["baseColor.textureMap"] = JsonValue.Create("../" + texture);
            properties["baseColor.useTexture"] = JsonValue.Create(true);
        }
        else
        {
            properties["baseColor.useTexture"] = JsonValue.Create(false);
        }

        if (material.Opacity < 0.999f)
        {
            properties["opacity.mode"] = JsonValue.Create("Blended");
            properties["opacity.alphaSource"] = JsonValue.Create("None");
            properties["opacity.factor"] = JsonValue.Create(material.Opacity);
        }
        else
        {
            properties["opacity.mode"] = JsonValue.Create("Opaque");
        }

        if (MathF.Abs(material.Repeat - 1) > 1e-4f)
        {
            properties["uv.tileU"] = JsonValue.Create(material.Repeat);
            properties["uv.tileV"] = JsonValue.Create(material.Repeat);
        }

        var root = new JsonObject
        {
            ["description"] = JsonValue.Create("Exported by Seamstress"),
            ["materialType"] = JsonValue.Create(MaterialType),
            ["materialTypeVersion"] = JsonValue.Create(MaterialTypeVersion),
            ["propertyValues"] = properties,
        };
        return root.ToJsonString(JsonOptions) + "\n";
    }

    private static string BuildReadme(ExportScene scene, string bundleName)
    {
        string fbx = bundleName + ".fbx";
        var sb = new StringBuilder();
        sb.Append("# ").Append(EngineBundle.DisplayName(EngineBundle.SceneName(scene))).Append(" — O3DE 26.10 garment\n\n");
        sb.Append("Exported from Seamstress. This is a static draped garment; it has no skeleton or animation.\n\n");
        sb.Append("## Files\n\n");
        sb.Append("- `").Append(fbx).Append("` — mesh, normals, UVs, materials and relative texture paths.\n");
        sb.Append("- `Textures/` — fabric textures used by the FBX and materials.\n");
        sb.Append("- `Materials/` — StandardPBR material files with colour, metallic, roughness, opacity, ");
        sb.Append("texture repeat and double-sided rendering.\n\n");
        sb.Append("## Import\n\n");
        sb.Append("1. Copy this folder into your O3DE project's `Assets` folder, for example ");
        sb.Append("`Assets/Seamstress/").Append(bundleName).Append("/`.\n");
        sb.Append("2. Let the Asset Processor import the FBX and PNG files.\n");
        sb.Append("3. Place the model in a level or prefab and add a Mesh component.\n");
        sb.Append("4. The FBX importer creates materials from the FBX, but it may only carry the fabric ");
        sb.Append("colour. To use the supplied StandardPBR materials (with their texture, repeat and ");
        sb.Append("double-sided settings), assign them to the model's material slots with the Material component.\n\n");
        sb.Append("The garment is exported at its simulated size in metres, Y up. O3DE uses a 1 metre world scale; ");
        sb.Append("the FBX stores centimetres and converts on import.\n");
        return sb.ToString();
    }

    /// <summary>O3DE stores colours linearly; the document colour is sRGB.</summary>
    private static float Linear(float srgb)
        => srgb <= 0.04045f ? srgb / 12.92f : MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);
}
