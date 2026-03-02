using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Fotoshow.Models;

/// <summary>
/// Modelo de foto en la base de datos
/// </summary>
public class Photo
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(500)]
    public string Filename { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string OriginalName { get; set; } = string.Empty;

    [Required]
    [MaxLength(1000)]
    public string LocalPath { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? ThumbnailPath { get; set; }

    public DateTime UploadDate { get; set; } = DateTime.Now;

    public int FacesDetected { get; set; } = 0;

    /// <summary>
    /// ID de persona asignado por clustering facial (0 = sin cara detectada)
    /// Fotos con el mismo PersonId contienen la misma persona
    /// </summary>
    public int PersonId { get; set; } = 0;

    public string? Embedding { get; set; }

    public bool CloudSynced { get; set; } = false;

    [MaxLength(50)]
    public string Status { get; set; } = "pending"; // pending, processed, error

    public long FileSize { get; set; } = 0;

    public int Width { get; set; } = 0;
    
    public int Height { get; set; } = 0;

    // Propiedad de navegación para tags
    public virtual ICollection<Tag> Tags { get; set; } = new List<Tag>();

    // Propiedad calculada para orientación
    [NotMapped]
    public string Orientation => Height >= Width ? "Portrait" : "Landscape";

    // Propiedad calculada para tamaño legible
    [NotMapped]
    public string FileSizeFormatted
    {
        get
        {
            if (FileSize < 1024) return $"{FileSize} B";
            if (FileSize < 1024 * 1024) return $"{FileSize / 1024:N1} KB";
            return $"{FileSize / (1024 * 1024):N1} MB";
        }
    }
}

/// <summary>
/// Modelo de etiqueta/tag
/// </summary>
public class Tag
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public string? Color { get; set; }

    // Navegación muchos a muchos
    public virtual ICollection<Photo> Photos { get; set; } = new List<Photo>();
}

/// <summary>
/// Modelo para configuración de la aplicación
/// </summary>
public class AppSettings
{
    public string WatchDirectory { get; set; } = "fotoshow";
    public string ThumbnailDirectory { get; set; } = "thumbnails";
    public string MarcoDirectory { get; set; } = "marcos";
    public int ThumbnailSize { get; set; } = 200;
    public int GridColumns { get; set; } = 5;
    public int PageSize { get; set; } = 100;
    public string CloudApiUrl { get; set; } = "https://api.fotoshow.site";
    public float SimilarityThreshold { get; set; } = 0.45f;
}
