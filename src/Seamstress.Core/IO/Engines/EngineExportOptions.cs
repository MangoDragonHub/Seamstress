namespace Seamstress.Core.IO.Engines;

/// <summary>What an engine bundle contains and how it is named.</summary>
public sealed class EngineExportOptions
{
    /// <summary>Merge the vertices joined by seams so each garment becomes one connected surface.</summary>
    public bool WeldSeams { get; set; } = true;

    /// <summary>Restrict the export to these garments; null exports every visible garment.</summary>
    public IReadOnlyCollection<string>? GarmentIds { get; set; }

    public bool IncludeHidden { get; set; }

    /// <summary>Name of the bundle folder and of the files inside it. Unsafe characters are replaced.</summary>
    public string BundleName { get; set; } = "garment";
}
