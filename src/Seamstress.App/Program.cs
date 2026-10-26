using System.Diagnostics;
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
            AppLog.Write($"Could not set {name}: {e.Message}");
        }
    }

    /// <summary>How long the interface has to draw its first frame before the launch counts as failed.</summary>
    private const int StartupTimeoutSeconds = 60;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr parent, string text, string caption, uint type);

    /// <summary>
    /// Closes a launch that never gets a window. The Qt bridge gives no result for a failed QML load: the engine
    /// writes the reason to the console, which a windowed app does not have, then waits as if all were well. The
    /// first frame is the only sign that the interface really came up, so without this the process would stay in
    /// the task manager with nothing on screen.
    /// </summary>
    private static void WatchStartup(bool automated)
    {
        var watchdog = new Thread(() =>
        {
            for (int quarter = 0; quarter < StartupTimeoutSeconds * 4; quarter++)
            {
                if (AppController.InterfaceDrawn)
                    return;
                Thread.Sleep(250);
            }
            if (AppController.InterfaceDrawn)
                return;
            string log = AppLog.FilePath ?? "(this run keeps no log file)";
            AppLog.Write($"No window after {StartupTimeoutSeconds} seconds: the interface did not load. The most "
                + "likely cause is a Qt QML module missing from this installation (the 3D viewport needs Qt Quick 3D).");
            AppLog.Stop();
            ReportStartupFailure($"The Seamstress window did not appear within {StartupTimeoutSeconds} seconds.\n\n"
                + "The interface could not be loaded. The most likely cause is a missing Qt Quick 3D module "
                + "(the 3D viewport needs it) or a graphics driver that did not start.", automated, log);
            Environment.Exit(1);
        })
        {
            IsBackground = true,
            Name = "startup watchdog",
        };
        watchdog.Start();
    }

    /// <summary>
    /// Says why a launch failed. A windowed program has no console, so the text goes into a message box (Windows)
    /// or to standard error, where automated runs read it.
    /// </summary>
    private static void ReportStartupFailure(string text, bool automated, string log)
    {
        if (automated)
        {
            Console.Error.WriteLine("error: " + text.Replace("\n\n", " ").Replace('\n', ' '));
            return;
        }
        string full = text + $"\n\nThe log has the details:\n{log}";
        if (OperatingSystem.IsWindows())
            MessageBoxW(IntPtr.Zero, full, "Seamstress", 0x00000010 /* MB_ICONERROR */);
        else
            Console.Error.WriteLine("error: " + full);
    }

    /// <summary>
    /// What is wrong with the files next to the program, or null when they are all there. An incomplete package, or
    /// one whose Qt libraries come from different versions, does not fail with a message: Qt aborts inside the QML
    /// engine, before there is a window to report anything.
    /// </summary>
    private static string? DeploymentProblem()
    {
        string root = AppContext.BaseDirectory;
        if (OperatingSystem.IsWindows())
        {
            foreach (string file in new[] { "Qt6Core.dll", "Qt6Quick.dll", "Qt6Quick3D.dll" })
                if (!File.Exists(Path.Combine(root, file)))
                    return $"{file} is missing";

            // Two Qt versions next to each other is the other way a deployment goes wrong, and it aborts as well.
            string? expected = QtVersionOf(Path.Combine(root, "Qt6Core.dll"));
            if (expected != null)
            {
                foreach (string dll in Directory.EnumerateFiles(root, "Qt6*.dll"))
                {
                    string? actual = QtVersionOf(dll);
                    if (actual != null && actual != expected)
                        return $"{Path.GetFileName(dll)} is Qt {actual} but Qt6Core.dll is Qt {expected}";
                }
            }
        }
        else if (!File.Exists(Path.Combine(root, "lib", "libQt6Core.so.6")))
        {
            return "the Qt libraries are missing (lib/libQt6Core.so.6)";
        }

        if (!File.Exists(Path.Combine(root, "qml", "QtQuick3D", "qmldir")))
            return "the Qt Quick 3D QML module is missing (qml/QtQuick3D)";
        if (!File.Exists(Path.Combine(root, "Application", "Main.qml")))
            return "the application's QML files are missing (Application/Main.qml)";
        return null;
    }

    private static string? QtVersionOf(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
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

    private static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    /// <summary>Writes the machine and startup details that make a launch log useful for a bug report.</summary>
    private static void LogStartup(string[] args)
    {
        AppLog.Write($"Seamstress {Version} starting");
        AppLog.Write($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})");
        AppLog.Write($"Runtime: .NET {Environment.Version}");
        AppLog.Write($"Arguments: {(args.Length == 0 ? "(none)" : string.Join(" ", args))}");
        AppLog.Write($"Settings: {AppSettings.DefaultPath}");
        AppLog.Write($"Log: {AppLog.FilePath ?? "(not written for this automated run)"}");
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

        var settings = AppSettings.Load();
        bool automated = options.Smoke || options.Screenshot != null;
        // The log starts before Qt so that failures while the window is created are recorded too. Automated runs
        // report on standard error and do not leave session logs behind.
        if (!automated)
            AppLog.Start(AppSettings.LogsFolder);
        LogStartup(args);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("Unhandled exception:\n" + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write("Unobserved task exception:\n" + e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => AppLog.Stop();

        // Decide between the GPU renderer and the CPU fallback before Qt creates its rendering context.
        string preference = forced ?? settings.Renderer;
        bool gpuUsable = GpuLooksUsable();
        bool previousGpuFailure = StartupOptions.PreviousGpuStartFailed;
        AppLog.Write($"Renderer preference: {preference}");
        AppLog.Write($"Previous GPU start failed: {previousGpuFailure}");
        if (OperatingSystem.IsLinux())
            AppLog.Write($"GPU driver found: {gpuUsable}");
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
        else if (!gpuUsable)
        {
            software = true;
            options.RendererDescription = Lang.N("CPU (no GPU driver found)");
        }
        else if (previousGpuFailure)
        {
            software = true;
            options.RendererDescription = Lang.N("CPU (the GPU renderer failed to start last time)");
            options.ClearCrashMarker();
            AppLog.Write("The previous GPU start did not finish; falling back to the CPU renderer.");
        }
        else
        {
            software = false;
            options.RendererDescription = Lang.N("GPU");
        }
        AppLog.Write($"Renderer: {options.RendererDescription}");
        AppLog.Write($"Language: {options.Language ?? settings.Language}");

        if (software)
        {
            SetNativeEnvironment("QSG_RHI_PREFER_SOFTWARE_RENDERER", "1");
            SetNativeEnvironment("LIBGL_ALWAYS_SOFTWARE", "1");
            AppLog.Write("Software rendering variables set.");
        }
        else if (options.Screenshot == null)
        {
            StartupOptions.WriteCrashMarker();
            AppLog.Write("GPU startup marker written.");
        }
        SetNativeEnvironment("QT_QUICK_CONTROLS_STYLE", "Fusion");
        // Qt's file dialogs need this to list local files when they run inside QML.
        SetNativeEnvironment("QML_XHR_ALLOW_FILE_READ", "1");

        // Refuse an installation that cannot work before Qt is asked to load it: in that case Qt aborts, and there
        // is no window left to say why.
        string? broken = DeploymentProblem();
        if (broken != null)
        {
            string log = AppLog.FilePath ?? "(this run keeps no log file)";
            AppLog.Write($"This installation is incomplete: {broken}");
            AppLog.Stop();
            ReportStartupFailure($"Seamstress cannot start from this folder:\n{broken}\n\n"
                + "Extract the whole archive again into a new folder (do not start the program from inside it) "
                + "and try again.", automated, log);
            Environment.Exit(1);
        }

        AppController.Startup = options;
        Qt.Application.DisplayName = "Seamstress";
        Qt.Application.SetWindowIcon("qrc:/assemblies/Seamstress/icons/seamstress.svg");
        // Started before the QML load, which reports nothing when the interface cannot be built.
        WatchStartup(automated);
        Qml.LoadFromRootModule("Main");
        Qml.WaitForExit();
    }
}
