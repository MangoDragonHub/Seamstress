using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Seamstress.Core.Model;

namespace Seamstress.Core.IO.Engines;

/// <summary>Writes a Unity 2020.1+ package: FBX, textures, materials, shaders and an import helper.</summary>
internal static class UnityExporter
{
    private const string Root = "Assets/Seamstress";
    private const string OpaqueShaderPath = Root + "/Shaders/SeamstressCloth.shader";
    private const string TransparentShaderPath = Root + "/Shaders/SeamstressClothTransparent.shader";
    private const string ImportHelperPath = Root + "/Editor/SeamstressGarmentImport.cs";
    private const string ShaderTemplate = "SeamstressCloth.shader";
    private const string TransparentShaderTemplate = "SeamstressClothTransparent.shader";
    private const string ImportHelperTemplate = "SeamstressGarmentImport.cs";

    public static EngineExportResult Export(ExportScene scene, string packagePath, string bundleName)
    {
        string bundleRoot = Root + "/" + bundleName;
        var textures = EngineTexturePlan.Build(scene);
        var materialNames = ObjExporter.MaterialNames(scene);
        string tempFolder = Path.Combine(Path.GetTempPath(), "seamstress-unity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempFolder);
        try
        {
            string fbxPath = Path.Combine(tempFolder, bundleName + ".fbx");
            FbxExporter.Export(scene, fbxPath, textures.ByMaterial, portableTexturePaths: true);
            byte[] fbx = File.ReadAllBytes(fbxPath);

            string opaqueShaderGuid = UnityPackageWriter.GuidFor(OpaqueShaderPath);
            string transparentShaderGuid = UnityPackageWriter.GuidFor(TransparentShaderPath);

            using (var package = new UnityPackageWriter(packagePath))
            {
                package.AddFolder(Root);
                package.AddFolder(Root + "/Shaders");
                package.AddFolder(Root + "/Editor");
                package.AddFolder(bundleRoot);
                package.AddFolder(bundleRoot + "/Textures");
                package.AddFolder(bundleRoot + "/Materials");

                package.AddFile(OpaqueShaderPath, Encoding.UTF8.GetBytes(ReadTemplate(ShaderTemplate)));
                package.AddFile(TransparentShaderPath, Encoding.UTF8.GetBytes(ReadTemplate(TransparentShaderTemplate)));
                package.AddFile(ImportHelperPath, Encoding.UTF8.GetBytes(ReadTemplate(ImportHelperTemplate)));

                package.AddFile(bundleRoot + "/" + bundleName + ".fbx", fbx);
                foreach (var texture in textures.Entries)
                    package.AddFile(bundleRoot + "/" + texture.RelativePath, File.ReadAllBytes(texture.SourcePath));

                foreach (var material in scene.Materials())
                {
                    string name = materialNames[material];
                    bool transparent = material.Opacity < 0.999f;
                    string shaderGuid = transparent ? transparentShaderGuid : opaqueShaderGuid;
                    string? textureGuid = textures.ByMaterial.TryGetValue(material, out string? relative)
                        ? UnityPackageWriter.GuidFor(bundleRoot + "/" + relative)
                        : null;
                    package.AddFile(bundleRoot + "/Materials/" + name + ".mat",
                        Encoding.UTF8.GetBytes(BuildMaterial(material, name, shaderGuid, textureGuid)));
                }

                package.AddFile(bundleRoot + "/seamstress-garment.json", Encoding.UTF8.GetBytes(BuildManifest(scene, materialNames, textures)));
                package.AddFile(bundleRoot + "/README.md", Encoding.UTF8.GetBytes(BuildReadme(scene, bundleName)));
            }

            return new EngineExportResult(
                EngineTarget.Unity,
                packagePath,
                new[] { packagePath },
                scene.Meshes.Count,
                EngineBundle.TriangleCount(scene),
                EngineBundle.FaceCount(scene));
        }
        finally
        {
            Directory.Delete(tempFolder, true);
        }
    }

    private static string BuildMaterial(FabricMaterial material, string name, string shaderGuid, string? textureGuid)
    {
        var (r, g, b) = ExportScene.ParseColor(material.Color);
        var sb = new StringBuilder();
        sb.Append("%YAML 1.1\n");
        sb.Append("%TAG !u! tag:unity3d.com,2011:\n");
        sb.Append("--- !u!21 &2100000\n");
        sb.Append("Material:\n");
        sb.Append("  serializedVersion: 6\n");
        sb.Append("  m_ObjectHideFlags: 0\n");
        sb.Append("  m_CorrespondingSourceObject: {fileID: 0}\n");
        sb.Append("  m_PrefabInstance: {fileID: 0}\n");
        sb.Append("  m_PrefabAsset: {fileID: 0}\n");
        sb.Append("  m_Name: \"").Append(name.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"\n");
        sb.Append("  m_Shader: {fileID: 4800000, guid: ").Append(shaderGuid).Append(", type: 3}\n");
        sb.Append("  m_ShaderKeywords: \n");
        sb.Append("  m_LightmapFlags: 4\n");
        sb.Append("  m_EnableInstancingVariants: 0\n");
        sb.Append("  m_DoubleSidedGI: 0\n");
        sb.Append("  m_CustomRenderQueue: -1\n");
        sb.Append("  stringTagMap: {}\n");
        sb.Append("  disabledShaderPasses: []\n");
        sb.Append("  m_SavedProperties:\n");
        sb.Append("    serializedVersion: 3\n");
        if (textureGuid == null)
        {
            sb.Append("    m_TexEnvs: []\n");
        }
        else
        {
            sb.Append("    m_TexEnvs:\n");
            sb.Append("    - _MainTex:\n");
            sb.Append("        m_Texture: {fileID: 2800000, guid: ").Append(textureGuid).Append(", type: 3}\n");
            sb.Append("        m_Scale: {x: ").Append(F(material.Repeat)).Append(", y: ").Append(F(material.Repeat)).Append("}\n");
            sb.Append("        m_Offset: {x: 0, y: 0}\n");
        }
        sb.Append("    m_Floats:\n");
        sb.Append("    - _Metallic: ").Append(F(Math.Clamp(material.Metallic, 0f, 1f))).Append('\n');
        sb.Append("    - _Glossiness: ").Append(F(Math.Clamp(1f - material.Roughness, 0f, 1f))).Append('\n');
        sb.Append("    m_Colors:\n");
        sb.Append("    - _Color: {r: ").Append(F(r)).Append(", g: ").Append(F(g)).Append(", b: ").Append(F(b))
          .Append(", a: ").Append(F(Math.Clamp(material.Opacity, 0f, 1f))).Append("}\n");
        return sb.ToString();
    }

    private static string BuildManifest(ExportScene scene, IReadOnlyDictionary<FabricMaterial, string> names,
        EngineTexturePlan textures)
    {
        var materials = new JsonArray();
        foreach (var material in scene.Materials())
        {
            var (r, g, b) = ExportScene.ParseColor(material.Color);
            materials.Add(new JsonObject
            {
                ["name"] = JsonValue.Create(names[material]),
                ["texture"] = JsonValue.Create(textures.ByMaterial.TryGetValue(material, out string? relative) ? relative : ""),
                ["repeat"] = JsonValue.Create(material.Repeat),
                ["opacity"] = JsonValue.Create(material.Opacity),
                ["metallic"] = JsonValue.Create(material.Metallic),
                ["roughness"] = JsonValue.Create(material.Roughness),
                ["r"] = JsonValue.Create(r),
                ["g"] = JsonValue.Create(g),
                ["b"] = JsonValue.Create(b),
            });
        }
        var root = new JsonObject { ["materials"] = materials };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static string BuildReadme(ExportScene scene, string bundleName)
    {
        string fbx = bundleName + ".fbx";
        var sb = new StringBuilder();
        sb.Append("# ").Append(EngineBundle.DisplayName(EngineBundle.SceneName(scene))).Append(" — Unity 2020.1+ garment\n\n");
        sb.Append("Exported from Seamstress. This is a static draped garment; it has no skeleton or animation.\n\n");
        sb.Append("## Files\n\n");
        sb.Append("- `").Append(fbx).Append("` — mesh, normals, UVs and material slots.\n");
        sb.Append("- `Textures/` — fabric textures.\n");
        sb.Append("- `Materials/` — double-sided cloth materials.\n");
        sb.Append("- `seamstress-garment.json` — material/texture mapping used by the Editor helper.\n");
        sb.Append("- `Assets/Seamstress/Editor/SeamstressGarmentImport.cs` — configures the model and materials after import.\n\n");
        sb.Append("## Import\n\n");
        sb.Append("1. Import this package with **Assets ▸ Import Package ▸ Custom Package**, or double-click the `.unitypackage`.\n");
        sb.Append("2. The Editor helper sets the model units and material import settings, assigns the generated materials, ");
        sb.Append("and adapts them to the project's Built-in, URP or HDRP render pipeline.\n");
        sb.Append("3. If the model still shows Unity's generated materials, run **Tools ▸ Seamstress ▸ Reimport Garment Assets**.\n\n");
        sb.Append("The garment is exported at its simulated size in metres, Y up. Materials are double-sided and keep their ");
        sb.Append("colour, opacity, metallic, roughness and texture repeat.\n");
        return sb.ToString();
    }

    private static string ReadTemplate(string fileName)
    {
        string resource = "Seamstress.Core.IO.Engines.Templates.Unity." + fileName;
        using Stream stream = typeof(UnityExporter).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded Unity template '{resource}'.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static string TextureMeta(string guid) => ReadTemplate("Texture.meta").Replace("__GUID__", guid);

    private static string F(float value) => value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Writes the gzip-compressed tar archive Unity uses for .unitypackage files.</summary>
internal sealed class UnityPackageWriter : IDisposable
{
    private static readonly DateTimeOffset Timestamp = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FileStream _file;
    private readonly GZipStream _gzip;
    private readonly TarWriter _tar;
    private readonly HashSet<string> _written = new(StringComparer.Ordinal);

    public UnityPackageWriter(string packagePath)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(packagePath));
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        _file = File.Create(packagePath);
        _gzip = new GZipStream(_file, CompressionLevel.Optimal, leaveOpen: true);
        _tar = new TarWriter(_gzip, TarEntryFormat.Ustar, leaveOpen: true);
    }

    public static string GuidFor(string assetPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("Seamstress/" + assetPath));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    public void AddFolder(string assetPath)
    {
        if (!_written.Add(assetPath))
            return;
        string guid = GuidFor(assetPath);
        AddDirectory(guid);
        AddText(guid + "/pathname", assetPath + "\n");
        AddText(guid + "/asset.meta", "fileFormatVersion: 2\nguid: " + guid + "\nfolderAsset: yes\n");
    }

    public void AddFile(string assetPath, byte[] content)
    {
        if (!_written.Add(assetPath))
            return;
        string guid = GuidFor(assetPath);
        AddDirectory(guid);
        AddText(guid + "/pathname", assetPath + "\n");
        AddText(guid + "/asset.meta", IsTexture(assetPath)
            ? UnityExporter.TextureMeta(guid)
            : "fileFormatVersion: 2\nguid: " + guid + "\n");
        AddBytes(guid + "/asset", content);
    }

    private static bool IsTexture(string assetPath)
    {
        string extension = Path.GetExtension(assetPath).ToLowerInvariant();
        return extension == ".png" || extension == ".jpg" || extension == ".jpeg" ||
               extension == ".tga" || extension == ".psd" || extension == ".bmp" ||
               extension == ".tif" || extension == ".tiff";
    }

    private void AddDirectory(string name)
    {
        var entry = new UstarTarEntry(TarEntryType.Directory, name) { ModificationTime = Timestamp };
        _tar.WriteEntry(entry);
    }

    private void AddText(string name, string text)
    {
        AddBytes(name, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
    }

    private void AddBytes(string name, byte[] data)
    {
        var entry = new UstarTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(data, writable: false),
            ModificationTime = Timestamp,
        };
        _tar.WriteEntry(entry);
    }

    public void Dispose()
    {
        _tar.Dispose();
        _gzip.Dispose();
        _file.Dispose();
    }
}
