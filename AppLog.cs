using System.IO;
using System.Text;

namespace DocumentGenerator.Infrastructure;

/// <summary>
/// Lightweight log. startup.log next to EXE (always);
/// logs/application.log after AppPaths is ready.
/// Never throws.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _startupLogPath;

    public static string StartupLogPath
    {
        get
        {
            if (_startupLogPath is not null) return _startupLogPath;
            try
            {
                var processPath = Environment.ProcessPath;
                var dir = !string.IsNullOrEmpty(processPath)
                    ? Path.GetDirectoryName(processPath)!
                    : AppContext.BaseDirectory;
                _startupLogPath = Path.Combine(dir, "startup.log");
            }
            catch
            {
                _startupLogPath = Path.Combine(Path.GetTempPath(), "Docugen-startup.log");
            }
            return _startupLogPath;
        }
    }

    public static void Bootstrap(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                var sb = new StringBuilder(128);
                sb.Append(DateTime.UtcNow.ToString("O"));
                sb.Append(" | ");
                sb.AppendLine(message);
                if (ex is not null)
                {
                    sb.AppendLine(ex.ToString());
                    sb.AppendLine();
                }
                File.AppendAllText(StartupLogPath, sb.ToString());
            }
        }
        catch { /* ignore */ }
    }

    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            lock (Gate)
            {
                var text = ex is null
                    ? $"{DateTime.UtcNow:O} | {message}\n"
                    : $"{DateTime.UtcNow:O} | {message}\n{ex}\n\n";

                try
                {
                    if (Directory.Exists(AppPaths.Logs))
                        File.AppendAllText(Path.Combine(AppPaths.Logs, "application.log"), text);
                }
                catch { /* ignore */ }

                File.AppendAllText(StartupLogPath, text);
            }
        }
        catch { /* ignore */ }
    }
}
