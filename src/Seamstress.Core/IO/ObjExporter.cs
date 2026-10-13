using System.Globalization;
using System.Text;

namespace Seamstress.Core.IO;

/// <summary>Wavefront OBJ + MTL writer.</summary>
public static class ObjExporter
{
    public static void Export(ExportScene scene, string path)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string mtlName = Path.GetFileNameWithoutExtension(path) + ".mtl";
        var inv = CultureInfo.InvariantCulture;
        var names = MaterialNames(scene);

        var sb = new StringBuilder();
        sb.Append("# Exported by Seamstress\n# Units: metres, Y up\n");
        sb.Append("mtllib ").Append(mtlName).Append('\n');
        int positionBase = 1, uvBase = 1;
        foreach (var mesh in scene.Meshes)
        {
            sb.Append("o ").Append(ExportScene.SafeName(mesh.Name)).Append('\n');
            foreach (var p in mesh.Positions)
                sb.Append(inv, $"v {p.X:0.######} {p.Y:0.######} {p.Z:0.######}\n");
            foreach (var uv in mesh.Uvs)
                sb.Append(inv, $"vt {uv.X:0.######} {uv.Y:0.######}\n");
            foreach (var n in mesh.Normals)
                sb.Append(inv, $"vn {n.X:0.#####} {n.Y:0.#####} {n.Z:0.#####}\n");
            foreach (var part in mesh.Parts)
            {
                sb.Append("g ").Append(ExportScene.SafeName(part.Name)).Append('\n');
                sb.Append("usemtl ").Append(names[part.Material]).Append('\n');
                sb.Append("s 1\n");
                int corner = 0;
                foreach (int size in part.FaceSizes)
                {
                    sb.Append('f');
                    for (int k = 0; k < size; k++, corner++)
                    {
                        int p = part.FacePositionIndices[corner] + positionBase;
                        int uv = part.FaceUvIndices[corner] + uvBase;
                        sb.Append(' ').Append(p).Append('/').Append(uv).Append('/').Append(p);
                    }
                    sb.Append('\n');
                }
            }
            positionBase += mesh.Positions.Length;
            uvBase += mesh.Uvs.Length;
        }
        File.WriteAllText(path, sb.ToString());

        var mtl = new StringBuilder("# Exported by Seamstress\n");
        foreach (var material in scene.Materials())
        {
            var (r, g, b) = ExportScene.ParseColor(material.Color);
            mtl.Append("\nnewmtl ").Append(names[material]).Append('\n');
            mtl.Append(inv, $"Kd {r:0.####} {g:0.####} {b:0.####}\n");
            mtl.Append("Ka 0 0 0\n");
            float specular = (1 - material.Roughness) * 0.5f;
            mtl.Append(inv, $"Ks {specular:0.####} {specular:0.####} {specular:0.####}\n");
            mtl.Append(inv, $"Ns {(1 - material.Roughness) * 200 + 2:0.##}\n");
            mtl.Append(inv, $"d {material.Opacity:0.####}\n");
            mtl.Append(inv, $"Pr {material.Roughness:0.####}\nPm {material.Metallic:0.####}\n");
            mtl.Append("illum 2\n");
            string? texture = TextureFiles.CopyNextTo(material.Texture, folder);
            if (texture != null)
            {
                mtl.Append("map_Kd ");
                if (MathF.Abs(material.Repeat - 1) > 1e-4f)
                    mtl.Append(inv, $"-s {material.Repeat:0.####} {material.Repeat:0.####} 1 ");
                mtl.Append(texture).Append('\n');
            }
        }
        File.WriteAllText(Path.Combine(folder, mtlName), mtl.ToString());
    }

    /// <summary>Unique, file-safe names for the scene's materials.</summary>
    public static Dictionary<Model.FabricMaterial, string> MaterialNames(ExportScene scene)
    {
        var result = new Dictionary<Model.FabricMaterial, string>();
        var used = new HashSet<string>();
        foreach (var material in scene.Materials())
        {
            string baseName = ExportScene.SafeName(material.Name), name = baseName;
            for (int i = 2; !used.Add(name); i++)
                name = $"{baseName}_{i}";
            result[material] = name;
        }
        return result;
    }
}

internal static class TextureFiles
{
    /// <summary>Copies a texture beside the exported file and returns its file name, or null if there is none.</summary>
    public static string? CopyNextTo(string? texturePath, string folder)
    {
        if (string.IsNullOrEmpty(texturePath) || !File.Exists(texturePath))
            return null;
        string name = Path.GetFileName(texturePath);
        string target = Path.Combine(folder, name);
        if (!string.Equals(Path.GetFullPath(texturePath), Path.GetFullPath(target), StringComparison.Ordinal))
            File.Copy(texturePath, target, true);
        return name;
    }

    public static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png",
    };
}
