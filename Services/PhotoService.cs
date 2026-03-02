using Microsoft.EntityFrameworkCore;
using Fotoshow.Data;
using Fotoshow.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using System.Collections.Concurrent;
using System.IO;

namespace Fotoshow.Services;

/// <summary>
/// Servicio principal para gestión de fotos con procesamiento paralelo
/// </summary>
public class PhotoService
{
    private readonly FotoshowContext _context;
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _semaphore;

    public PhotoService(FotoshowContext context, AppSettings settings)
    {
        _context = context;
        _settings = settings;
        _semaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);
    }

    /// <summary>
    /// Obtiene fotos con paginación (SUPER RÁPIDO)
    /// </summary>
    public async Task<List<Photo>> GetPhotosAsync(int page, int pageSize, string? folderFilter = null)
    {
        var query = _context.Photos.AsNoTracking();

        if (!string.IsNullOrEmpty(folderFilter))
        {
            query = query.Where(p => p.LocalPath.StartsWith(folderFilter));
        }

        return await query
            .OrderByDescending(p => p.UploadDate)
            .Skip(page * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    /// <summary>
    /// Cuenta total de fotos (con caché)
    /// </summary>
    public async Task<int> GetPhotoCountAsync(string? folderFilter = null)
    {
        var query = _context.Photos.AsNoTracking();

        if (!string.IsNullOrEmpty(folderFilter))
        {
            query = query.Where(p => p.LocalPath.StartsWith(folderFilter));
        }

        return await query.CountAsync();
    }

    /// <summary>
    /// Procesa múltiples archivos en PARALELO
    /// Usa todos los cores del CPU eficientemente
    /// </summary>
    public async Task<int> ProcessFilesAsync(
        List<string> files, 
        IProgress<(int processed, int total)>? progress = null)
    {
        var processed = 0;
        var total = files.Count;
        var successCount = 0;

        // Filtrar archivos ya existentes en BD
        var existingPaths = await _context.Photos
            .Where(p => files.Contains(p.LocalPath))
            .Select(p => p.LocalPath)
            .ToListAsync();

        var newFiles = files.Except(existingPaths).ToList();

        if (newFiles.Count == 0)
            return 0;

        // Procesar en paralelo con control de concurrencia
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        var photosToAdd = new ConcurrentBag<Photo>();

        await Parallel.ForEachAsync(newFiles, options, async (file, ct) =>
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                var photo = await ProcessSingleFileAsync(file);
                if (photo != null)
                {
                    photosToAdd.Add(photo);
                    Interlocked.Increment(ref successCount);
                }

                var current = Interlocked.Increment(ref processed);
                progress?.Report((current, total));
            }
            finally
            {
                _semaphore.Release();
            }
        });

        // Guardar todo en batch (MUCHO más rápido)
        if (photosToAdd.Any())
        {
            await _context.Photos.AddRangeAsync(photosToAdd);
            await _context.SaveChangesAsync();
        }

        return successCount;
    }

    /// <summary>
    /// Procesa un archivo individual: COPIA + thumbnail + metadata
    /// </summary>
    private async Task<Photo?> ProcessSingleFileAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            // ═══════════════════════════════════════════════════════
            // PASO 1: COPIAR EL ARCHIVO A LA CARPETA FOTOSHOW
            // ═══════════════════════════════════════════════════════
            var watchDir = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                _settings.WatchDirectory
            );
            Directory.CreateDirectory(watchDir);

            var fileName = Path.GetFileName(filePath);
            var destPath = Path.Combine(watchDir, fileName);

            // Si ya existe, agregar sufijo único
            if (File.Exists(destPath))
            {
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                var extension = Path.GetExtension(fileName);
                destPath = Path.Combine(watchDir, 
                    $"{fileNameWithoutExt}_{Guid.NewGuid().ToString()[..6]}{extension}");
            }

            // COPIAR EL ARCHIVO
            File.Copy(filePath, destPath, overwrite: false);

            // ═══════════════════════════════════════════════════════
            // PASO 2: PROCESAR LA COPIA (no el original)
            // ═══════════════════════════════════════════════════════
            var fileInfo = new FileInfo(destPath);
            var photo = new Photo
            {
                Filename = fileInfo.Name,
                OriginalName = fileInfo.Name,
                LocalPath = destPath,  // ← Path de la COPIA, no del original
                UploadDate = DateTime.Now,
                FileSize = fileInfo.Length,
                Status = "pending"
            };

            // Generar thumbnail desde la copia
            var thumbnailPath = await GenerateThumbnailAsync(destPath);
            photo.ThumbnailPath = thumbnailPath;

            // Obtener dimensiones REALES (respetando EXIF orientation)
            using var imgForSize = await Image.LoadAsync(destPath);
            imgForSize.Mutate(x => x.AutoOrient());
            photo.Width = imgForSize.Width;
            photo.Height = imgForSize.Height;

            return photo;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error procesando {filePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Genera thumbnail con ImageSharp (MUY RÁPIDO)
    /// </summary>
    private async Task<string> GenerateThumbnailAsync(string sourcePath)
    {
        var thumbnailDir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            _settings.ThumbnailDirectory
        );
        Directory.CreateDirectory(thumbnailDir);

        var thumbnailId = Guid.NewGuid().ToString();
        var thumbnailPath = Path.Combine(thumbnailDir, $"{thumbnailId}.jpg");

        try
        {
            using var image = await Image.LoadAsync(sourcePath);

            // Aplicar rotación EXIF ANTES de redimensionar
            // Sin esto, fotos tomadas en vertical aparecen de costado
            image.Mutate(x => x.AutoOrient());

            // Redimensionar manteniendo proporción
            var size = _settings.ThumbnailSize;
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(size, size),
                Mode = ResizeMode.Max,
                Sampler = KnownResamplers.Lanczos3
            }));

            // Guardar con calidad optimizada
            var encoder = new JpegEncoder
            {
                Quality = 85
            };

            await image.SaveAsync(thumbnailPath, encoder);
            return thumbnailPath;
        }
        catch
        {
            // Si falla, usar la imagen original
            return sourcePath;
        }
    }

    /// <summary>
    /// Elimina una foto (archivo + thumbnail + BD)
    /// </summary>
    public async Task<bool> DeletePhotoAsync(Guid photoId)
    {
        try
        {
            var photo = await _context.Photos.FindAsync(photoId);
            if (photo == null)
                return false;

            // Eliminar archivos
            if (File.Exists(photo.LocalPath))
                File.Delete(photo.LocalPath);

            if (!string.IsNullOrEmpty(photo.ThumbnailPath) && 
                File.Exists(photo.ThumbnailPath) && 
                photo.ThumbnailPath != photo.LocalPath)
            {
                File.Delete(photo.ThumbnailPath);
            }

            // Eliminar de BD
            _context.Photos.Remove(photo);
            await _context.SaveChangesAsync();

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error eliminando foto: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Busca fotos por nombre o ruta
    /// </summary>
    public async Task<List<Photo>> SearchPhotosAsync(string searchTerm)
    {
        return await _context.Photos
            .AsNoTracking()
            .Where(p => 
                p.Filename.Contains(searchTerm) || 
                p.LocalPath.Contains(searchTerm))
            .OrderByDescending(p => p.UploadDate)
            .Take(100)
            .ToListAsync();
    }

    /// <summary>
    /// Obtiene fotos sin procesar (para cola de IA)
    /// </summary>
    public async Task<List<Photo>> GetPendingPhotosAsync(int batchSize = 50)
    {
        return await _context.Photos
            .Where(p => p.Status == "pending")
            .OrderBy(p => p.UploadDate)
            .Take(batchSize)
            .ToListAsync();
    }

    /// <summary>
    /// Actualiza el estado de procesamiento de IA
    /// </summary>
    public async Task UpdatePhotoAIDataAsync(Guid photoId, int facesDetected, string? embedding)
    {
        var photo = await _context.Photos.FindAsync(photoId);
        if (photo != null)
        {
            photo.FacesDetected = facesDetected;
            photo.Embedding = embedding;
            photo.Status = "processed";
            await _context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Marca foto como sincronizada en la nube
    /// </summary>
    public async Task MarkAsSyncedAsync(Guid photoId)
    {
        var photo = await _context.Photos.FindAsync(photoId);
        if (photo != null)
        {
            photo.CloudSynced = true;
            await _context.SaveChangesAsync();
        }
    }
}
