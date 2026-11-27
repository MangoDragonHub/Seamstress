namespace Seamstress.Core.IO.Engines;

/// <summary>What a finished engine export wrote, for status messages and tests.</summary>
public sealed record EngineExportResult(
    EngineTarget Target,
    string OutputPath,
    IReadOnlyList<string> Files,
    int GarmentCount,
    int TriangleCount,
    int FaceCount);
