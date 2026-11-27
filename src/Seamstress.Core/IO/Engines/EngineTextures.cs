using Seamstress.Core.Model;

namespace Seamstress.Core.IO.Engines;

/// <summary>
/// Textures a bundle copies beside its meshes. Equal source files are shared, and different files with the same
/// file name get distinct names, so every material keeps the texture it was given.
/// </summary>
public sealed class EngineTexturePlan
{
    public sealed record Entry(string SourcePath, string RelativePath);

    private readonly Dictionary<FabricMaterial, string> _byMaterial;

    private EngineTexturePlan(List<Entry> entries, Dictionary<FabricMaterial, string> byMaterial)
    {
        Entries = entries;
        _byMaterial = byMaterial;
    }

    /// <summary>Every distinct copied texture, in the order the materials were first met.</summary>
    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>The path of each textured material's image, relative to the bundle folder.</summary>
    public IReadOnlyDictionary<FabricMaterial, string> ByMaterial => _byMaterial;

    public static EngineTexturePlan Build(ExportScene scene)
    {
        var entries = new List<Entry>();
        var byMaterial = new Dictionary<FabricMaterial, string>();
        var bySource = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var material in scene.Materials())
        {
            if (string.IsNullOrEmpty(material.Texture) || !File.Exists(material.Texture))
                continue;

            string source = Path.GetFullPath(material.Texture);
            if (!bySource.TryGetValue(source, out string? relative))
            {
                string extension = Path.GetExtension(source).ToLowerInvariant();
                string stem = ExportScene.SafeName(Path.GetFileNameWithoutExtension(source));
                string name = stem + extension;
                for (int i = 2; !usedNames.Add(name); i++)
                    name = $"{stem}_{i}{extension}";
                relative = "Textures/" + name;
                bySource[source] = relative;
                entries.Add(new Entry(source, relative));
            }
            byMaterial[material] = relative;
        }

        return new EngineTexturePlan(entries, byMaterial);
    }

    /// <summary>Copies the plan's textures under <paramref name="bundleFolder"/> and returns the copied paths.</summary>
    public IReadOnlyList<string> CopyTo(string bundleFolder)
    {
        var copied = new List<string>(Entries.Count);
        foreach (var entry in Entries)
        {
            string target = Path.Combine(bundleFolder, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            string? folder = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            if (!string.Equals(Path.GetFullPath(entry.SourcePath), Path.GetFullPath(target), StringComparison.Ordinal))
                File.Copy(entry.SourcePath, target, overwrite: true);
            copied.Add(target);
        }
        return copied;
    }
}
