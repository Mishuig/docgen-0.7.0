using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace DocumentGenerator.Infrastructure;

/// <summary>
/// Runs as early as the CLR will allow (module initializer), before App.OnStartup.
/// If this file never appears, the process did not enter managed code.
/// </summary>
internal static class EarlyBootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath)
                      ?? AppContext.BaseDirectory
                      ?? Path.GetTempPath();
            var path = Path.Combine(dir, "startup.log");
            var sb = new StringBuilder();
            sb.Append(DateTime.UtcNow.ToString("O"));
            sb.AppendLine(" | [module-init] managed code entered");
            sb.Append("ProcessPath=");
            sb.AppendLine(Environment.ProcessPath ?? "(null)");
            sb.Append("BaseDirectory=");
            sb.AppendLine(AppContext.BaseDirectory);
            sb.Append("Is64Bit=");
            sb.AppendLine(Environment.Is64BitProcess.ToString());
            File.AppendAllText(path, sb.ToString());
        }
        catch
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "Docugen-startup.log"),
                    $"{DateTime.UtcNow:O} | [module-init] failed to write beside EXE, used TEMP\n");
            }
            catch { /* last resort */ }
        }
    }
}
