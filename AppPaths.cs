using System.IO;

namespace DocumentGenerator.Infrastructure;

public static class AppPaths
{
    public static string Root { get; private set; } = AppContext.BaseDirectory;
    public static string Data => Path.Combine(Root, "data");
    public static string Templates => Path.Combine(Root, "templates");
    public static string Exports => Path.Combine(Root, "exports");
    public static string Backups => Path.Combine(Root, "backups");
    public static string Cache => Path.Combine(Root, "cache");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Database => Path.Combine(Data, "document-generator.db");

    public static void Initialize()
    {
        // Portable: data next to the EXE (not the single-file extraction temp).
        var processPath = Environment.ProcessPath;
        Root = !string.IsNullOrEmpty(processPath)
            ? Path.GetDirectoryName(processPath)!
            : AppContext.BaseDirectory;

        // Create only what we need immediately (data + logs). Other folders on demand.
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Logs);

        // Write probe — fail fast on read-only locations (Program Files etc.).
        var probe = Path.Combine(Data, $".write-test-{Guid.NewGuid():N}");
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
    }

    public static void EnsureFolder(string path)
    {
        Directory.CreateDirectory(path);
    }

    public static string ResolveTemplate(string relativePath)
    {
        // templates/ may not exist yet on a brand-new install
        EnsureFolder(Templates);

        var fullPath = Path.GetFullPath(Path.Combine(Root, relativePath));
        var rootPath = Path.GetFullPath(Templates) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Некоректний шлях до шаблону.");
        return fullPath;
    }
}
