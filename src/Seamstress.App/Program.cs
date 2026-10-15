using System.Runtime.InteropServices;
using Qt.Quick;
using Seamstress.Core;

namespace Seamstress.App;

/// <summary>Options taken from the command line before the user interface starts.</summary>
internal sealed class StartupOptions
{
    public string? File { get; set; }
    public List<string> Templates { get; } = new();
    public int SimulateFrames { get; set; }
    public string? Tool { get; set; }
    public bool UvMode { get; set; }
    public bool SelectFirst { get; set; }
    public string? Screenshot { get; set; }
    public string View { get; set; } = "";
    public string? Avatar { get; set; }
    public bool Smoke { get; set; }
    public bool Quads { get; set; }
    public float? Gender { get; set; }
    public string? Units { get; set; }
    /// <summary>"--lang": the interface language for this run, without changing the saved one.</summary>
    public string? Language { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? SelectPiece { get; set; }
    public bool AllowLaunch { get; set; } = true;
    /// <summary>Which renderer is in use, in English; the interface shows it translated.</summary>
    public string RendererDescription { get; set; } = "GPU";

    private static string CrashMarker => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "gpu-startup.marker");

    /// <summary>
    /// Left behind while the GPU renderer starts and removed once it has drawn a frame. Finding it on the next launch
    /// means the GPU path crashed or hung last time, so that launch falls back to the CPU renderer.
    /// </summary>
    public static bool PreviousGpuStartFailed => System.IO.File.Exists(CrashMarker);

    public static void WriteCrashMarker()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashMarker)!);
            System.IO.File.WriteAllText(CrashMarker, DateTime.Now.ToString("O"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void ClearCrashMarker()
    {
        try { System.IO.File.Delete(CrashMarker); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

public static class Program
{
    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int SetEnvUnix(string name, string value, int overwrite);

    // Qt reads its settings through the C runtime, which keeps its own copy of the environment on Windows.
    [DllImport("ucrtbase", EntryPoint = "_putenv_s")]
    private static extern int SetEnvWindows(string name, string value);

    /// <summary>Sets a variable in the native process environment, where Qt reads its configuration.</summary>
    private static void SetNativeEnvironment(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            if (OperatingSystem.IsWindows())
                SetEnvWindows(name, value);
            else
                SetEnvUnix(name, value, 1);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"Could not set {name}: {e.Message}");
        }
    }

    private static bool GpuLooksUsable()
    {
        if (!OperatingSystem.IsLinux())
            return true;
        try
        {
            // No DRM render node means there is no GPU driver to talk to (bare VM, container, broken driver).
            return Directory.Exists("/dev/dri") && Directory.GetFiles("/dev/dri", "renderD*").Length > 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static void Main(string[] args)
    {
        var options = new StartupOptions();
        string? forced = null;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--cpu": forced = "cpu"; break;
                case "--gpu": forced = "gpu"; break;
                case "--template": options.Templates.Add(Next()); break;
                case "--simulate": int.TryParse(Next(), out int frames); options.SimulateFrames = frames; break;
                case "--tool": options.Tool = Next(); break;
                case "--uv": options.UvMode = true; break;
                case "--select-first": options.SelectFirst = true; break;
                case "--view": options.View = Next(); break;
                case "--avatar": options.Avatar = Next(); break;
                case "--quads": options.Quads = true; break;
                case "--units": options.Units = Next(); break;
                case "--lang": options.Language = Next(); break;
                case "--gender":
                    if (float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g))
                        options.Gender = g;
                    break;
                case "--select": options.SelectPiece = Next(); break;
                case "--size":
                    {
                        var parts = Next().Split('x');
                        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                        {
                            options.Width = w;
                            options.Height = h;
                        }
                        break;
                    }
                case "--smoke": options.Smoke = true; options.AllowLaunch = false; break;
                case "--screenshot": options.Screenshot = Path.GetFullPath(Next()); options.AllowLaunch = false; break;
                case "--help":
                case "-h":
                    Console.WriteLine("Usage: Seamstress [file.garment.json] [--cpu | --gpu]\n"
                        + "  --cpu   render the 3D viewport on the CPU (software rasterizer)\n"
                        + "  --gpu   render on the GPU even if the last GPU start failed\n"
                        + "  --lang  interface language for this run: " + string.Join(", ", Lang.Languages.Select(l => l.Code)));
                    return;
                default:
                    if (!args[i].StartsWith("--", StringComparison.Ordinal) && System.IO.File.Exists(args[i]))
                        options.File = Path.GetFullPath(args[i]);
                    break;
            }
        }

        // The automated check reads English texts unless asked for another language.
        if (options.Smoke)
            options.Language ??= Lang.English;

        // Decide between the GPU renderer and the CPU fallback before Qt creates its rendering context.
        string preference = forced ?? AppSettings.Load().Renderer;
        bool software;
        if (preference == "cpu")
        {
            software = true;
            options.RendererDescription = Lang.N("CPU (software rendering)");
        }
        else if (preference == "gpu")
        {
            software = false;
            options.RendererDescription = Lang.N("GPU");
        }
        else if (!GpuLooksUsable())
        {
            software = true;
            options.RendererDescription = Lang.N("CPU (no GPU driver found)");
        }
        else if (StartupOptions.PreviousGpuStartFailed)
        {
            software = true;
            options.RendererDescription = Lang.N("CPU (the GPU renderer failed to start last time)");
            options.ClearCrashMarker();
        }
        else
        {
            software = false;
            options.RendererDescription = Lang.N("GPU");
        }

        if (software)
        {
            SetNativeEnvironment("QSG_RHI_PREFER_SOFTWARE_RENDERER", "1");
            SetNativeEnvironment("LIBGL_ALWAYS_SOFTWARE", "1");
        }
        else if (options.Screenshot == null)
        {
            StartupOptions.WriteCrashMarker();
        }
        SetNativeEnvironment("QT_QUICK_CONTROLS_STYLE", "Fusion");
        // Qt's file dialogs need this to list local files when they run inside QML.
        SetNativeEnvironment("QML_XHR_ALLOW_FILE_READ", "1");

        AppController.Startup = options;
        Qt.Application.DisplayName = "Seamstress";
        Qt.Application.SetWindowIcon("qrc:/assemblies/Seamstress/icons/seamstress.svg");
        Qml.LoadFromRootModule("Main");
        Qml.WaitForExit();
    }
}
