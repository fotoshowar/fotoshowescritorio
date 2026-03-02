using System.ComponentModel.DataAnnotations;

namespace FotoshowTray.Models;

/// <summary>
/// Estado local de una foto registrada en FotoShow.
/// status: pending → processing → synced → sold → delivered | error
/// </summary>
public class PhotoRecord
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(1000)]
    public string LocalPath { get; set; } = string.Empty;

    /// <summary>SHA-256 del path normalizado (para overlay icons desde shell ext)</summary>
    [Required, MaxLength(64)]
    public string PathHash { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? ThumbnailPath { get; set; }

    /// <summary>Embedding facial serializado como JSON array de floats</summary>
    public string? Embedding { get; set; }

    [MaxLength(50)]
    public string Status { get; set; } = "pending";

    /// <summary>UUID asignado por el backend al sincronizar</summary>
    [MaxLength(36)]
    public string? BackendPhotoId { get; set; }

    [MaxLength(36)]
    public string? BackendGalleryId { get; set; }

    public int FacesDetected { get; set; } = 0;

    public long FileSize { get; set; } = 0;

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SyncedAt { get; set; }
    public DateTime? SoldAt { get; set; }
    public DateTime? DeliveredAt { get; set; }

    /// <summary>ID del order_item que disparó la notificación de venta</summary>
    [MaxLength(36)]
    public string? OrderItemId { get; set; }
}
