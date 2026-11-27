using Seamstress.Core.Geometry;
using Seamstress.Core.Model;

namespace Seamstress.Core.IO.Engines;

/// <summary>Writes a scene as an engine-ready bundle in a folder named after the bundle.</summary>
public static class EngineExport
{
    /// <summary>Builds the scene from the document and writes it as one engine bundle.</summary>
    public static EngineExportResult Export(Document document, Func<Garment, GarmentMesh> meshFor,
        EngineTarget target, string outputFolder, EngineExportOptions options)
    {
        var scene = ExportScene.Build(document, meshFor, new ExportOptions
        {
            WeldSeams = options.WeldSeams,
            GarmentIds = options.GarmentIds,
            IncludeHidden = options.IncludeHidden,
        });
        return Export(scene, target, outputFolder, options);
    }

    /// <summary>Writes an already-built scene as one engine bundle; only the bundle name comes from the options.</summary>
    public static EngineExportResult Export(ExportScene scene, EngineTarget target, string outputFolder, EngineExportOptions options)
    {
        if (scene.Meshes.Count == 0)
            throw new InvalidOperationException("The engine export scene has no meshes.");
        if (target != EngineTarget.Godot)
            throw new NotSupportedException($"The {target} exporter is not available yet.");

        string root = Path.GetFullPath(outputFolder);
        string bundleName = EngineBundle.BundleName(options.BundleName);
        string bundleFolder = Path.Combine(root, bundleName);
        Directory.CreateDirectory(bundleFolder);
        return GodotExporter.Export(scene, bundleFolder, bundleName);
    }
}
