using System;
using System.IO;
using System.Text;

namespace Inbrisk.Core;

/// <summary>
/// Dedicated high-reliability rolling file logger for Ghost OS and Inbrisk components.
/// Logs timestamped entries to %LOCALAPPDATA%\inbrisk\logs\ghost-os.log.
/// </summary>
public static class GhostLogger
{
    private static readonly object Gate = new();
    private static string? _logFilePath;
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

    /// <summary>
    /// Gets the absolute path to the active ghost log file.
    /// </summary>
    public static string LogFilePath
    {
        get
        {
            if (_logFilePath != null) return _logFilePath;
            lock (Gate)
            {
                if (_logFilePath != null) return _logFilePath;
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string logDir = Path.Combine(localAppData, "inbrisk", "logs");
                try
                {
                    if (!Directory.Exists(logDir))
                    {
                        Directory.CreateDirectory(logDir);
                    }
                }
                catch { }

                _logFilePath = Path.Combine(logDir, "ghost-os.log");
                return _logFilePath;
            }
        }
    }

    /// <summary>Logs an informational message.</summary>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>Logs a warning message.</summary>
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>Logs an error message, optionally with an exception.</summary>
    public static void Error(string message, Exception? ex = null)
    {
        string fullMessage = ex != null ? $"{message} (Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace})" : message;
        Write("ERROR", fullMessage);
    }

    /// <summary>Logs a debug diagnostic message.</summary>
    public static void Debug(string message) => Write("DEBUG", message);

    private static void Write(string level, string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string line = $"{timestamp} [{level}] {message}";

        lock (Gate)
        {
            try
            {
                string path = LogFilePath;
                RotateIfNecessary(path);

                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.WriteLine(line);
                writer.Flush();
            }
            catch
            {
                // Logging must never crash the caller
            }
        }
    }

    private static void RotateIfNecessary(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                if (fi.Length > MaxFileSizeBytes)
                {
                    string oldPath = path + ".old";
                    if (File.Exists(oldPath))
                    {
                        File.Delete(oldPath);
                    }
                    File.Move(path, oldPath);
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Reads the most recent lines from the log file.
    /// </summary>
    public static string[] ReadRecentLines(int count = 50)
    {
        lock (Gate)
        {
            try
            {
                string path = LogFilePath;
                if (!File.Exists(path))
                {
                    return Array.Empty<string>();
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var lines = new System.Collections.Generic.List<string>();
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.Add(line);
                }

                if (lines.Count <= count) return lines.ToArray();
                return lines.GetRange(lines.Count - count, count).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
