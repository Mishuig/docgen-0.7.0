using System.IO;
using System.Security.Cryptography;
using DocumentGenerator.Domain;
using DocumentGenerator.Infrastructure;

namespace DocumentGenerator.Services;

/// <param name="Path">Шлях до робочої копії шаблону в кеші.</param>
/// <param name="Refreshed">Копію було створено або оновлено під час цього виклику.</param>
/// <param name="OriginalMissing">Оригінал недоступний, використано збережену копію.</param>
public sealed record CacheResult(string Path, bool Refreshed, bool OriginalMissing);

/// <summary>
/// Кеш шаблонів. Програма НІКОЛИ не працює з оригінальним .docx напряму:
/// перегляд, підстановка змінних і збереження документа виконуються з робочої копії
/// cache/&lt;id&gt;/template.docx. Оригінал лише читається (з дозволом спільного доступу),
/// тому Word, Провідник чи інша програма, що тримає файл відкритим, нічого не ламають.
/// Кнопка «Відкрити шаблон» відкриває окрему копію з cache/&lt;id&gt;/view, а не оригінал.
/// </summary>
public sealed class TemplateCache
{
    private static readonly object Gate = new();

    private static string DirFor(int id) => Path.Combine(AppPaths.Cache, id.ToString());
    private static string CopyPath(int id) => Path.Combine(DirFor(id), "template.docx");
    private static string ViewDir(int id) => Path.Combine(DirFor(id), "view");

    /// <summary>
    /// Гарантує актуальну робочу копію. Порівнює SHA-256 оригіналу та копії;
    /// якщо відрізняються (або копії немає) — атомарно оновлює копію.
    /// </summary>
    public CacheResult Ensure(TemplateRecord record)
    {
        lock (Gate)
        {
            var original = AppPaths.ResolveTemplate(record.RelativePath);
            var copy = CopyPath(record.Id);
            AppPaths.EnsureFolder(AppPaths.Cache);
            Directory.CreateDirectory(DirFor(record.Id));

            if (!File.Exists(original))
            {
                if (File.Exists(copy))
                {
                    AppLog.Write($"Оригінал шаблону {record.Id} недоступний, використовується кеш: {original}");
                    return new CacheResult(copy, false, true);
                }
                throw new FileNotFoundException("Файл шаблону не знайдено, а копії в кеші немає.", original);
            }

            string originalHash;
            try { originalHash = Retry(() => HashShared(original)); }
            catch (Exception ex) when (File.Exists(copy))
            {
                AppLog.Write($"Не вдалося прочитати оригінал шаблону {record.Id}, використовується кеш.", ex);
                return new CacheResult(copy, false, true);
            }

            if (File.Exists(copy))
            {
                string? copyHash = null;
                try { copyHash = HashShared(copy); } catch (Exception ex) { AppLog.Write("Кеш шаблону пошкоджено, буде перестворено.", ex); }
                if (string.Equals(originalHash, copyHash, StringComparison.OrdinalIgnoreCase))
                    return new CacheResult(copy, false, false);
            }

            try
            {
                Retry(() => { CopyShared(original, copy, validate: true); return 0; });
                AppLog.Write($"Кеш шаблону {record.Id} оновлено.");
                return new CacheResult(copy, true, false);
            }
            catch (Exception ex) when (File.Exists(copy))
            {
                // Оригінал може бути напівзаписаним (Word саме зберігає). Лишаємо попередню робочу копію.
                AppLog.Write($"Не вдалося оновити кеш шаблону {record.Id}, лишається попередня копія.", ex);
                return new CacheResult(copy, false, false);
            }
        }
    }

    /// <summary>Копія для ручного відкриття у Word. Оригінал і робоча копія не блокуються й не змінюються.</summary>
    public string PrepareViewCopy(TemplateRecord record)
    {
        var working = Ensure(record).Path;
        lock (Gate)
        {
            var dir = ViewDir(record.Id);
            Directory.CreateDirectory(dir);
            CleanupViewCopies(dir);

            var safe = SafeName(record.Name);
            var target = Path.Combine(dir, safe + ".docx");
            try { Retry(() => { CopyShared(working, target, validate: false); return 0; }); }
            catch (IOException)
            {
                // Попередню копію ще тримає відкритою Word — робимо нову з іншою назвою.
                target = Path.Combine(dir, $"{safe}_{DateTime.Now:HHmmss}.docx");
                CopyShared(working, target, validate: false);
            }
            return target;
        }
    }

    /// <summary>Видаляє кеш шаблону (при видаленні шаблону з бібліотеки).</summary>
    public void Delete(int id)
    {
        lock (Gate)
        {
            try { if (Directory.Exists(DirFor(id))) Directory.Delete(DirFor(id), true); }
            catch (Exception ex) { AppLog.Write($"Не вдалося видалити кеш шаблону {id}.", ex); }
        }
    }

    /// <summary>
    /// Атомарне копіювання: спершу у тимчасовий файл поруч, потім заміна цілі одним викликом.
    /// Джерело читається зі спільним доступом, тому відкритий у Word файл не заважає.
    /// </summary>
    public static void CopyShared(string source, string destination, bool validate = false)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                input.CopyTo(output);

            // Перевіряємо, що це цілий DOCX, ще до заміни робочої копії.
            if (validate) _ = new DocxTemplateEngine().FindPlaceholders(temp);

            File.Move(temp, destination, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
        }
    }

    private static string HashShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>До 3 спроб з паузою 300 мс, якщо файл тимчасово зайнятий іншою програмою.</summary>
    private static T Retry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return action(); }
            catch (IOException) when (attempt < 3) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) when (attempt < 3) { Thread.Sleep(300); }
        }
    }

    private static void CleanupViewCopies(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1)) File.Delete(file);
            }
            catch { /* файл відкритий — залишаємо */ }
        }
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim();
        if (name.Length == 0) name = "template";
        return name.Length > 80 ? name[..80] : name;
    }
}
