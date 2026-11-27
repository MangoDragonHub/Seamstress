using System.Text;

namespace Seamstress.Core.IO.Engines;

/// <summary>Small helpers every engine bundle writer shares.</summary>
internal static class EngineBundle
{
    /// <summary>The name a bundle and its files use, safe on every file system.</summary>
    public static string BundleName(string? name) => ExportScene.SafeName(DisplayName(name));

    /// <summary>A display name on one line, without surrounding or repeated spaces.</summary>
    public static string DisplayName(string? name)
    {
        string line = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length == 0 ? "Garment" : line;
    }

    public static string WriteText(string path, string content)
    {
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public static int TriangleCount(ExportScene scene) => scene.Meshes.Sum(m => m.TriangleCount);

    public static int FaceCount(ExportScene scene) => scene.Meshes.Sum(m => m.FaceCount);
}
