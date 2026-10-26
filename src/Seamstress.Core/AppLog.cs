using System.Globalization;

namespace Seamstress.Core;

/// <summary>
/// The plain-text log of a session: one file per launch in the "logs" folder of the dedicated Seamstress folder.
/// Logging is always on; the file is opened before Qt starts so startup failures are recorded too.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static string _folder = AppSettings.LogsFolder;
    private static string? _file;

    /// <summary>Whether this session is writing a log file.</summary>
    public static bool IsActive { get { lock (Gate) return _writer != null; } }

    /// <summary>The folder new logs are written to.</summary>
    public static string Folder { get { lock (Gate) return _folder; } }

    /// <summary>The file this session is writing, or null when logging is off.</summary>
    public static string? FilePath { get { lock (Gate) return _file; } }

    /// <summary>
    /// Starts a new log named after the current date and time, closing any previous one without deleting it.
    /// Returns false when the folder or file cannot be created.
    /// </summary>
    public static bool Start(string folder, string? firstLine = null)
    {
        lock (Gate)
        {
            CloseLocked();
            _folder = folder;
            try
            {
                Directory.CreateDirectory(folder);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
                string path = Path.Combine(folder, $"seamstress-{stamp}.txt");
                // Two launches in the same second must still get a file of their own.
                for (int n = 2; File.Exists(path); n++)
                    path = Path.Combine(folder, $"seamstress-{stamp}-{n}.txt");
                _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                _file = path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _writer = null;
                _file = null;
                return false;
            }
            if (firstLine != null)
                WriteLocked(firstLine);
            return true;
        }
    }

    /// <summary>Writes a line, or several lines, with the time in front of each. Does nothing when logging is off.</summary>
    public static void Write(string message)
    {
        lock (Gate)
            WriteLocked(message);
    }

    private static void WriteLocked(string message)
    {
        if (_writer == null)
            return;
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        try
        {
            foreach (string line in message.Replace("\r\n", "\n").Split('\n'))
                _writer.WriteLine($"[{stamp}] {line}");
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // A log that cannot be written must not bring the application down with it.
        }
    }

    /// <summary>Closes the log.</summary>
    public static void Stop()
    {
        lock (Gate)
            CloseLocked();
    }

    private static void CloseLocked()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
        _writer = null;
        _file = null;
    }
}
