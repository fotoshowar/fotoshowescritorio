using Microsoft.EntityFrameworkCore;
using Fotoshow.Models;
using System.IO;

namespace Fotoshow.Data;

/// <summary>
/// Contexto de base de datos con Entity Framework Core
/// Incluye configuración optimizada para SQLite con índices
/// </summary>
public class FotoshowContext : DbContext
{
    public DbSet<Photo> Photos { get; set; }
    public DbSet<Tag> Tags { get; set; }

    public FotoshowContext(DbContextOptions<FotoshowContext> options) 
        : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var dbPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, 
                "database.db"
            );

            optionsBuilder
                .UseSqlite($"Data Source={dbPath}")
                .EnableSensitiveDataLogging(false)
                .EnableDetailedErrors(false);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configuración de Photo
        modelBuilder.Entity<Photo>(entity =>
        {
            // Índices para optimización de queries
            entity.HasIndex(p => p.LocalPath)
                .IsUnique()
                .HasDatabaseName("IX_Photos_LocalPath");

            entity.HasIndex(p => p.UploadDate)
                .IsDescending()
                .HasDatabaseName("IX_Photos_UploadDate");

            entity.HasIndex(p => p.Status)
                .HasDatabaseName("IX_Photos_Status");

            // Índice para búsqueda por persona
            entity.HasIndex(p => p.PersonId)
                .HasDatabaseName("IX_Photos_PersonId");

            entity.HasIndex(p => p.CloudSynced)
                .HasDatabaseName("IX_Photos_CloudSynced");

            entity.HasIndex(p => new { p.LocalPath, p.UploadDate })
                .HasDatabaseName("IX_Photos_Path_Date");

            // Configuración de precisión para fechas
            entity.Property(p => p.UploadDate)
                .HasColumnType("TEXT")
                .HasConversion(
                    v => v.ToString("O"),
                    v => DateTime.Parse(v)
                );
        });

        // Configuración de Tag
        modelBuilder.Entity<Tag>(entity =>
        {
            entity.HasIndex(t => t.Name)
                .IsUnique()
                .HasDatabaseName("IX_Tags_Name");
        });

        // Relación muchos a muchos entre Photo y Tag
        modelBuilder.Entity<Photo>()
            .HasMany(p => p.Tags)
            .WithMany(t => t.Photos)
            .UsingEntity(j => j.ToTable("PhotoTags"));
    }

    /// <summary>
    /// Configura la base de datos y aplica migraciones
    /// </summary>
    public async Task InitializeDatabaseAsync()
    {
        try
        {
            // Crear la base de datos si no existe
            await Database.EnsureCreatedAsync();

            // Optimizaciones de SQLite
            await Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            await Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");
            await Database.ExecuteSqlRawAsync("PRAGMA cache_size=10000;");
            await Database.ExecuteSqlRawAsync("PRAGMA temp_store=MEMORY;");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error inicializando BD: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Optimiza la base de datos ejecutando VACUUM y ANALYZE
    /// </summary>
    public async Task OptimizeDatabaseAsync()
    {
        try
        {
            await Database.ExecuteSqlRawAsync("VACUUM;");
            await Database.ExecuteSqlRawAsync("ANALYZE;");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error optimizando BD: {ex.Message}");
        }
    }
}
