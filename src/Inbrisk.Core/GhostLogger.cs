using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Inbrisk.Core;

/// <summary>
/// Dedicated high-reliability rolling file logger for Ghost OS and Inbrisk components.
/// Logs timestamped entries to %LOCALAPPDATA%\inbrisk\logs\ghost-os.log.
/// Registered secret values are automatically redacted before anything is written to disk.
/// </summary>
public static class GhostLogger
{
    private static readonly object Gate = new();
    private static string? _logFilePath;
    private static readonly List<string> _secrets = new();
    private const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
    private const string RedactedPlaceholder = "***REDACTED***";

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
                string logDir = Path.Combine(UserSettings.DataDir, "logs");
                try
                {
                    if (!Directory.Exists(logDir))
                    {
                        Directory.CreateDirectory(logDir);
                    }
                    ApplyLogDirectoryAcl(logDir);
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

    /// <summary>
    /// Registers a sensitive value (e.g. a credential) that must never be written to the log file.
    /// Every subsequent log line has all occurrences of the value replaced with a redaction marker.
    /// </summary>
    public static void RegisterSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        lock (Gate)
        {
            if (!_secrets.Contains(secret))
            {
                _secrets.Add(secret);
            }
        }
    }

    /// <summary>
    /// Replaces every registered secret occurrence in the provided text with a redaction marker.
    /// </summary>
    public static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        string[] secrets;
        lock (Gate)
        {
            secrets = _secrets.ToArray();
        }

        foreach (var secret in secrets)
        {
            message = message.Replace(secret, RedactedPlaceholder, StringComparison.Ordinal);
        }
        return message;
    }

    private static void Write(string level, string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string line = $"{timestamp} [{level}] {Redact(message)}";

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

    private static void ApplyLogDirectoryAcl(string logDir)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser != null)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    currentUser,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            new DirectoryInfo(logDir).SetAccessControl(security);
        }
        catch { }
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
        count = Math.Clamp(count, 1, 5000);
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
