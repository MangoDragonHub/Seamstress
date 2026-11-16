using Seamstress.Core.IO;

namespace Seamstress.App;

/// <summary>
/// Gives the 2D canvas a URL for a reference image. WebP files are decoded with libwebp and cached as PNGs,
/// because some Qt installations ship without the WebP image plugin. Other formats are used as they are.
/// </summary>
internal sealed class ReferenceImageCache : IDisposable
{
    private readonly string _folder;
    private readonly Dictionary<string, string> _converted = new();
    private int _counter;

    public ReferenceImageCache()
    {
        string root = Path.GetTempPath();
        RemoveStaleFolders(root);
        _folder = Path.Combine(root, $"seamstress-refs-{Environment.ProcessId}");
        Directory.CreateDirectory(_folder);
    }

    public string Url(string path)
    {
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase))
            return ViewportSync.ToUrl(path);
        try
        {
            var info = new FileInfo(path);
            string key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            if (_converted.TryGetValue(key, out string? cached) && File.Exists(cached))
                return ViewportSync.ToUrl(cached);

            string output = Path.Combine(_folder, $"reference_{++_counter}.png");
            if (!WebpDecoder.TryWritePng(path, output))
                return ViewportSync.ToUrl(path); // Qt may have its own WebP plugin
            _converted[key] = output;
            return ViewportSync.ToUrl(output);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ViewportSync.ToUrl(path);
        }
    }

    private static void RemoveStaleFolders(string root)
    {
        try
        {
            foreach (string dir in Directory.GetDirectories(root, "seamstress-refs-*"))
            {
                string suffix = Path.GetFileName(dir)["seamstress-refs-".Length..];
                if (!int.TryParse(suffix, out int pid))
                    continue;
                bool alive;
                try { alive = !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
                catch (ArgumentException) { alive = false; }
                if (!alive)
                    Directory.Delete(dir, true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left-over reference caches from an earlier run are harmless.
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
