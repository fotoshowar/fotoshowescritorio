using Microsoft.EntityFrameworkCore;
using FotoshowTray.Models;
using System.Security.Cryptography;
using System.Text;

namespace FotoshowTray;

/// <summary>
/// SQLite compartido entre el tray service y la shell extension (WAL mode).
/// Ubicado en %APPDATA%\Fotoshow\photos.db
/// </summary>
public class LocalDb : DbContext
{
    public DbSet<PhotoRecord> Photos { get; set; } = null!;

    public static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Fotoshow", "photos.db"
    );

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        options.UseSqlite($"Data Source={DbPath}");
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<PhotoRecord>(e =>
        {
            e.HasIndex(p => p.LocalPath).IsUnique();
            e.HasIndex(p => p.PathHash).IsUnique();
            e.HasIndex(p => p.Status);
            e.HasIndex(p => p.BackendPhotoId);
        });
    }

    public async Task InitAsync()
    {
        await Database.EnsureCreatedAsync();
        // WAL mode para que la shell extension pueda leer sin bloquear
        await Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");
    }

    // ─── helpers ───────────────────────────────────────────────────────────────

    public static string HashPath(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath).ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async Task<PhotoRecord?> FindByPathAsync(string fullPath)
    {
        var hash = HashPath(fullPath);
        return await Photos.FirstOrDefaultAsync(p => p.PathHash == hash);
    }

    public async Task<string> GetStatusByHashAsync(string pathHash)
    {
        var photo = await Photos
            .AsNoTracking()
            .Where(p => p.PathHash == pathHash)
            .Select(p => new { p.Status })
            .FirstOrDefaultAsync();
        return photo?.Status ?? "unknown";
    }
}
