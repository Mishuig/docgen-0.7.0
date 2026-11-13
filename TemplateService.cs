using System.IO;
using System.Security.Cryptography;
using DocumentGenerator.Domain;
using DocumentGenerator.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DocumentGenerator.Services;

public sealed class TemplateService
{
    private readonly TemplateCache _cache;

    public TemplateService(TemplateCache cache) => _cache = cache;

    public async Task<List<TemplateRecord>> GetAllAsync(string? search = null)
    {
        await using var db = new AppDbContext();
        // Завантажуємо з SQLite, фільтруємо в пам'яті:
        // SQLite lower()/LIKE за замовчуванням погано працюють з кирилицею,
        // а StringComparison.OrdinalIgnoreCase коректно обробляє українські літери, цифри та спецсимволи.
        var items = await db.Templates.AsNoTracking().ToListAsync();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            items = items
                .Where(x =>
                    (!string.IsNullOrEmpty(x.Name) &&
                     x.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(x.Category) &&
                     x.Category.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        return items
            .OrderBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<TemplateRecord> ImportAsync(string sourcePath)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Файл не знайдено.", sourcePath);
        if (!string.Equals(Path.GetExtension(sourcePath), ".docx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Підтримуються лише файли DOCX.");
        var safeName = Path.GetFileName(sourcePath);
        var destinationName = $"{Guid.NewGuid():N}_{safeName}";
        AppPaths.EnsureFolder(AppPaths.Templates);
        var destination = Path.Combine(AppPaths.Templates, destinationName);
        try
        {
            // Читаємо джерело зі спільним доступом, щоб імпорт працював, навіть якщо файл відкритий у Word,
            // і перевіряємо вже збережену копію, а не оригінал.
            TemplateCache.CopyShared(sourcePath, destination);
            _ = new DocxTemplateEngine().FindPlaceholders(destination);
        }
        catch { if (File.Exists(destination)) File.Delete(destination); throw; }
        var record = new TemplateRecord
        {
            Name = Path.GetFileNameWithoutExtension(sourcePath),
            RelativePath = Path.Combine("templates", destinationName),
            Sha256 = await CalculateHashAsync(destination),
            CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
        };
        try
        {
            await using var db = new AppDbContext();
            db.Templates.Add(record);
            await db.SaveChangesAsync();
        }
        catch { if (File.Exists(destination)) File.Delete(destination); throw; }

        try { _cache.Ensure(record); }
        catch (Exception ex) { AppLog.Write($"Не вдалося створити кеш шаблону {record.Id} після імпорту.", ex); }
        return record;
    }

    public async Task RenameAsync(int id, string name, string category)
    {
        name = name.Trim(); category = string.IsNullOrWhiteSpace(category) ? "Інше" : category.Trim();
        if (name.Length is < 1 or > 240) throw new ArgumentException("Назва має містити від 1 до 240 символів.");
        await using var db = new AppDbContext();
        var item = await db.Templates.FirstOrDefaultAsync(x => x.Id == id) ?? throw new InvalidOperationException("Шаблон не знайдено.");
        item.Name = name; item.Category = category; item.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = new AppDbContext();
        var item = await db.Templates.FirstOrDefaultAsync(x => x.Id == id) ?? throw new InvalidOperationException("Шаблон не знайдено.");
        var path = AppPaths.ResolveTemplate(item.RelativePath);
        // Move to backup first so accidental deletion is recoverable.
        AppPaths.EnsureFolder(AppPaths.Backups);
        if (File.Exists(path)) File.Move(path, Path.Combine(AppPaths.Backups, $"deleted_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Path.GetFileName(path)}"));
        db.Templates.Remove(item);
        await db.SaveChangesAsync();
        _cache.Delete(id);
    }

    public async Task BackupAllAsync()
    {
        AppPaths.EnsureFolder(AppPaths.Backups);
        var destination = Path.Combine(AppPaths.Backups, $"backup_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(destination);
        List<TemplateRecord> items;
        await using (var db = new AppDbContext())
            items = await db.Templates.AsNoTracking().ToListAsync();
        foreach (var item in items)
        {
            var source = AppPaths.ResolveTemplate(item.RelativePath);
            if (File.Exists(source)) File.Copy(source, Path.Combine(destination, Path.GetFileName(source)), true);
        }
        if (File.Exists(AppPaths.Database)) File.Copy(AppPaths.Database, Path.Combine(destination, "document-generator.db"), true);
    }

    private static async Task<string> CalculateHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
