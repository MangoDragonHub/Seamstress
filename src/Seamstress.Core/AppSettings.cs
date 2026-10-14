using System.Text.Json;

namespace Seamstress.Core;

/// <summary>User preferences, stored as JSON in the per-user configuration folder.</summary>
public sealed class AppSettings
{
    /// <summary>MakeHuman's user data folder (the one containing "data/clothes"), e.g. ~/makehuman/v1py3.</summary>
    public string MakeHumanUserFolder { get; set; } = "";

    /// <summary>The MakeHuman base mesh (data/3dobjs/base.obj) used as the fitting avatar.</summary>
    public string MakeHumanBaseMesh { get; set; } = "";

    /// <summary>Optional command that starts MakeHuman after a garment was sent, e.g. "python3 /opt/makehuman/makehuman.py".</summary>
    public string MakeHumanCommand { get; set; } = "";

    /// <summary>"auto", "gpu" or "cpu". Takes effect the next time the application starts.</summary>
    public string Renderer { get; set; } = "auto";

    /// <summary>Texture size in pixels assumed when reporting texel density.</summary>
    public int TextureResolution { get; set; } = 2048;

    /// <summary>"metric" or "imperial". Only changes what the interface shows; files and exports stay metric.</summary>
    public string Units { get; set; } = "metric";

    /// <summary>Spacing of the 3D ground grid in centimetres; 0 uses the default for the units (10 cm or 4 in).</summary>
    public double GridSize { get; set; }

    /// <summary>Step in centimetres that Shift-dragging a move handle snaps to; 0 uses the default for the units.</summary>
    public double SnapStep { get; set; }

    /// <summary>The grid size and snap step used until the user chooses their own.</summary>
    public static double DefaultGridStep(bool imperial) => imperial ? 10.16 : 10;

    /// <summary>Keyboard shortcuts the user changed, by command id (see <see cref="KeyBindings"/>); the rest keep their defaults.</summary>
    public Dictionary<string, string[]> Shortcuts { get; set; } = new();

    /// <summary>Written into MakeHuman assets as their author; empty uses the login name.</summary>
    public string Author { get; set; } = "";

    /// <summary>Licence written into MakeHuman assets, e.g. "CC0" or "CC-BY".</summary>
    public string MakeHumanLicense { get; set; } = "CC0";
    public List<string> RecentFiles { get; set; } = new();

    public static string DefaultPath
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Seamstress", "settings.json");

    // Relaxed escaping keeps keys such as "Ctrl+S" readable in the file.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged settings file should not stop the application from starting.
        }
        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    public void AddRecent(string file)
    {
        RecentFiles.Remove(file);
        RecentFiles.Insert(0, file);
        if (RecentFiles.Count > 10)
            RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
    }

    /// <summary>The folder MakeHuman loads clothes from, or null when no user folder is linked.</summary>
    public string? MakeHumanClothesFolder
        => string.IsNullOrWhiteSpace(MakeHumanUserFolder) ? null : Path.Combine(MakeHumanUserFolder, "data", "clothes");

    /// <summary>Looks in the usual places for a MakeHuman installation and fills in whatever is still empty.</summary>
    public bool DetectMakeHuman()
    {
        bool changed = false;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(MakeHumanUserFolder))
        {
            var candidates = new List<string>();
            foreach (string root in new[] { home, documents, Path.Combine(home, "Documents") }.Where(r => !string.IsNullOrEmpty(r)).Distinct())
            {
                foreach (string version in new[] { "v1py3", "v1" })
                    candidates.Add(Path.Combine(root, "makehuman", version));
            }
            // Prefer the folder that already has clothes in it.
            string? found = candidates.FirstOrDefault(c => Directory.Exists(Path.Combine(c, "data", "clothes")))
                ?? candidates.FirstOrDefault(Directory.Exists);
            if (found != null)
            {
                MakeHumanUserFolder = found;
                changed = true;
            }
        }
        if (string.IsNullOrWhiteSpace(MakeHumanBaseMesh))
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(MakeHumanUserFolder))
                candidates.Add(Path.Combine(MakeHumanUserFolder, "data", "3dobjs", "base.obj"));
            candidates.Add("/usr/share/makehuman/data/3dobjs/base.obj");
            candidates.Add("/opt/makehuman/data/3dobjs/base.obj");
            candidates.Add(Path.Combine(home, "makehuman", "data", "3dobjs", "base.obj"));
            string? found = candidates.FirstOrDefault(File.Exists);
            if (found != null)
            {
                MakeHumanBaseMesh = found;
                changed = true;
            }
        }
        return changed;
    }
}
