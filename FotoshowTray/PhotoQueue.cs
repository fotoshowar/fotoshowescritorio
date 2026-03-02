using System.Collections.Concurrent;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using FotoshowTray.Models;

namespace FotoshowTray;

/// <summary>
/// Cola de procesamiento de fotos.
/// Por cada foto: thumbnail local (solo si no existe) + embedding facial → sync al backend.
/// NO muestra thumbnails en ninguna UI — son solo para uso interno.
/// </summary>
public class PhotoQueue : IDisposable
{
    private readonly ConcurrentQueue<string> _pendingPaths = new();
    private readonly LocalDb _db;
    private readonly AiClient _ai;
    private readonly BackendClient _backend;
    private readonly TrayConfig _config;
    private CancellationTokenSource _cts = new();
    private Task? _workerTask;
    private bool _disposed;

    public event Action<string>? OnLog;
    public event Action<int>? OnQueueCountChanged;
    public event Action<int, int, string>? OnProgressChanged;  // done, total, currentFile
    public event Action<int>? OnSyncDone;                      // totalSynced

    private static readonly string ThumbnailDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Fotoshow", "thumbs"
    );

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".tiff", ".bmp" };

    public PhotoQueue(LocalDb db, AiClient ai, BackendClient backend, TrayConfig config)
    {
        _db = db;
        _ai = ai;
        _backend = backend;
        _config = config;
        Directory.CreateDirectory(ThumbnailDir);
    }

    public void Start()
    {
        _workerTask = Task.Run(ProcessLoopAsync);
    }

    // ─── encolar ───────────────────────────────────────────────────────────────

    public void EnqueueFile(string path)
    {
        if (IsImageFile(path))
        {
            _pendingPaths.Enqueue(path);
            OnQueueCountChanged?.Invoke(_pendingPaths.Count);
        }
    }

    public void EnqueueFolder(string folderPath)
    {
        try
        {
            var files = Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
                .Where(IsImageFile)
                .OrderBy(f => f);

            foreach (var f in files)
                _pendingPaths.Enqueue(f);

            OnLog?.Invoke($"Carpeta agregada: {Path.GetFileName(folderPath)} ({_pendingPaths.Count} fotos en cola)");
            OnQueueCountChanged?.Invoke(_pendingPaths.Count);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Error leyendo carpeta {folderPath}: {ex.Message}");
        }
    }

    // ─── loop de procesamiento ─────────────────────────────────────────────────

    private async Task ProcessLoopAsync()
    {
        var sem = new SemaphoreSlim(_config.MaxConcurrentProcessing);
        var ct = _cts.Token;
        int done = 0;
        int batchTotal = 0;

        while (!ct.IsCancellationRequested)
        {
            if (!_pendingPaths.TryDequeue(out var path))
            {
                // Si terminó un batch → notificar "listo"
                if (done > 0 && _pendingPaths.IsEmpty)
                {
                    OnSyncDone?.Invoke(done);
                    done = 0;
                    batchTotal = 0;
                }
                await Task.Delay(500, ct).ContinueWith(_ => { });
                continue;
            }

            // Actualizar total del batch actual
            batchTotal = done + _pendingPaths.Count + 1;

            await sem.WaitAsync(ct).ContinueWith(_ => { });
            if (ct.IsCancellationRequested) break;

            var capturedPath = path;
            var capturedDone = done;
            OnProgressChanged?.Invoke(capturedDone, batchTotal, capturedPath);

            _ = Task.Run(async () =>
            {
                try { await ProcessOneAsync(capturedPath, ct); }
                finally
                {
                    Interlocked.Increment(ref done);
                    sem.Release();
                    OnQueueCountChanged?.Invoke(_pendingPaths.Count);
                    OnProgressChanged?.Invoke(done, batchTotal, capturedPath);
                }
            }, ct);
        }
    }

    private async Task ProcessOneAsync(string path, CancellationToken ct)
    {
        // Verificar si ya está procesada
        var existing = await _db.FindByPathAsync(path);
        if (existing?.Status is "synced" or "sold" or "delivered") return;

        // Registrar en DB si no existe
        var record = existing ?? new PhotoRecord
        {
            LocalPath = path,
            PathHash = LocalDb.HashPath(path),
            FileSize = new FileInfo(path).Length
        };

        if (existing == null)
        {
            record.Status = "processing";
            _db.Photos.Add(record);
        }
        else
        {
            record.Status = "processing";
        }
        await _db.SaveChangesAsync(ct);

        try
        {
            // Generar thumbnail SOLO si no existe aún
            if (string.IsNullOrEmpty(record.ThumbnailPath) || !File.Exists(record.ThumbnailPath))
                record.ThumbnailPath = await GenerateThumbnailAsync(path, ct);

            // Extraer embedding facial (requiere ai_worker)
            AiClient.AiResult? aiResult = null;
            if (_config.AutoProcessEnabled)
                aiResult = await _ai.ProcessPhotoAsync(path, ct);

            record.FacesDetected = aiResult?.FacesDetected ?? 0;
            record.Embedding = aiResult?.Embedding is not null
                ? JsonSerializer.Serialize(aiResult.Embedding)
                : null;

            // Sincronizar con el backend (thumbnail + embedding, NO la foto original)
            if (_config.IsLoggedIn && !string.IsNullOrEmpty(record.ThumbnailPath))
            {
                var backendId = await _backend.SyncPhotoMetadataAsync(record, ct);
                if (backendId is not null)
                {
                    record.BackendPhotoId = backendId;
                    record.Status = "synced";
                    record.SyncedAt = DateTime.UtcNow;
                }
                else
                {
                    record.Status = "error";
                }
            }
            else
            {
                // Sin login → queda como pending hasta que el usuario se loguee
                record.Status = _config.IsLoggedIn ? "error" : "pending";
            }
        }
        catch (Exception ex)
        {
            record.Status = "error";
            OnLog?.Invoke($"Error procesando {Path.GetFileName(path)}: {ex.Message}");
        }

        await _db.SaveChangesAsync(ct);
        OnLog?.Invoke($"[{record.Status.ToUpper()}] {Path.GetFileName(path)}");
    }

    // ─── thumbnail ─────────────────────────────────────────────────────────────

    private async Task<string?> GenerateThumbnailAsync(string sourcePath, CancellationToken ct)
    {
        var thumbPath = Path.Combine(ThumbnailDir, $"{Guid.NewGuid()}.jpg");
        try
        {
            using var image = await Image.LoadAsync(sourcePath, ct);
            image.Mutate(x => x.AutoOrient().Resize(new ResizeOptions
            {
                Size = new Size(_config.ThumbnailSize, _config.ThumbnailSize),
                Mode = ResizeMode.Max,
                Sampler = KnownResamplers.Lanczos3
            }));
            await image.SaveAsync(thumbPath, new JpegEncoder { Quality = 82 }, ct);
            return thumbPath;
        }
        catch
        {
            return null;
        }
    }

    // ─── helpers ───────────────────────────────────────────────────────────────

    private static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path));

    public int PendingCount => _pendingPaths.Count;

    public void Dispose()
    {
        if (!_disposed)
        {
            _cts.Cancel();
            _workerTask?.Wait(3000);
            _cts.Dispose();
            _disposed = true;
        }
    }
}
