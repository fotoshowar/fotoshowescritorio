using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Fotoshow.Data;
using Fotoshow.Models;
using Microsoft.EntityFrameworkCore;

namespace Fotoshow.Services;

// ═══════════════════════════════════════════════════════════
// MODELOS JSON para comunicación con ai_worker.py
// ═══════════════════════════════════════════════════════════
public class AiRequest
{
    [JsonPropertyName("cmd")]   public string Cmd   { get; set; } = "";
    [JsonPropertyName("path")]  public string? Path { get; set; }
    [JsonPropertyName("paths")] public List<string>? Paths { get; set; }
}

public class AiPhotoResult
{
    [JsonPropertyName("status")]    public string Status    { get; set; } = "";
    [JsonPropertyName("path")]      public string? Path     { get; set; }
    [JsonPropertyName("faces")]     public int Faces        { get; set; }
    [JsonPropertyName("embedding")] public List<float>? Embedding { get; set; }
    [JsonPropertyName("message")]   public string? Message  { get; set; }
}

public class AiBatchResponse
{
    [JsonPropertyName("status")]  public string Status  { get; set; } = "";
    [JsonPropertyName("results")] public List<AiPhotoResult>? Results { get; set; }
    [JsonPropertyName("total")]   public int Total { get; set; }
}

// ═══════════════════════════════════════════════════════════
// SERVICIO PRINCIPAL DE IA
// ═══════════════════════════════════════════════════════════
public class AiService : IDisposable
{
    // ── Config ────────────────────────────────────────────────
    private const string HOST          = "127.0.0.1";
    private const int    PORT          = 54321;
    private const int    CONNECT_TIMEOUT_MS = 30_000;   // 30s para cargar modelo
    private const int    BATCH_SIZE    = 10;             // fotos por request
    private const float  SIMILARITY_THRESHOLD = 0.45f;  // umbral clustering

    // ── Estado ────────────────────────────────────────────────
    private Process?    _pythonProcess;
    private TcpClient?  _client;
    private NetworkStream? _stream;
    private StreamReader?  _reader;
    private readonly SemaphoreSlim _sockLock = new(1, 1);
    private bool _disposed = false;

    // Callbacks para UI
    public event Action<string>?       OnLog;
    public event Action<int, int>?     OnProgress;   // (procesadas, total)
    public event Action<bool>?         OnReady;      // (listo=true/error=false)

    // ── Inicio ────────────────────────────────────────────────
    /// <summary>
    /// Lanza el proceso de IA y espera a que esté listo.
    /// Detecta automáticamente: EXE compilado, Python embebido o Python del sistema.
    /// </summary>
    public async Task StartAsync()
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // Matar proceso previo si quedó colgado
            KillExistingProcess();

            // PRIORIDAD 1: EXE compilado (Nuitka o PyInstaller)
            var exePaths = new[]
            {
                Path.Combine(baseDir, "FotoshowAI.exe"),
                Path.Combine(baseDir, "ai_worker.exe")
            };

            foreach (var exePath in exePaths)
            {
                if (File.Exists(exePath))
                {
                    Log($"🚀 Iniciando motor de IA (EXE compilado)...");
                    var psi = new ProcessStartInfo
                    {
                        FileName  = exePath,
                        UseShellExecute        = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        CreateNoWindow         = true,
                        WorkingDirectory       = baseDir
                    };
                    await StartProcessAsync(psi);
                    return;
                }
            }

            // PRIORIDAD 2: Python embebido portable
            var embeddedPython = Path.Combine(baseDir, "Fotoshow_IA_Portable", "python", "python.exe");
            var embeddedScript = Path.Combine(baseDir, "Fotoshow_IA_Portable", "ai_worker.py");

            if (File.Exists(embeddedPython) && File.Exists(embeddedScript))
            {
                Log($"🐍 Iniciando motor de IA (Python portable)...");
                var psi = new ProcessStartInfo
                {
                    FileName  = embeddedPython,
                    Arguments = $"\"{embeddedScript}\"",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                    WorkingDirectory       = Path.GetDirectoryName(embeddedScript)
                };
                await StartProcessAsync(psi);
                return;
            }

            // PRIORIDAD 3: Python del sistema + ai_worker.py
            var scriptPath = Path.Combine(baseDir, "ai_worker.py");
            if (File.Exists(scriptPath))
            {
                Log("🐍 Iniciando motor de IA (Python del sistema)...");
                var psi = new ProcessStartInfo
                {
                    FileName  = FindPython(),
                    Arguments = $"\"{scriptPath}\"",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                    WorkingDirectory       = baseDir
                };
                await StartProcessAsync(psi);
                return;
            }

            // No se encontró ningún método
            Log($"❌ Motor de IA no encontrado. Buscar:");
            Log($"   - FotoshowAI.exe (compilado)");
            Log($"   - Fotoshow_IA_Portable/python/python.exe");
            Log($"   - ai_worker.py + Python instalado");
            OnReady?.Invoke(false);
        }
        catch (Exception ex)
        {
            Log($"❌ Error iniciando IA: {ex.Message}");
            OnReady?.Invoke(false);
        }
    }

    /// <summary>
    /// Inicia el proceso de IA y espera conexión.
    /// </summary>
    private async Task StartProcessAsync(ProcessStartInfo psi)
    {
        try
        {
            _pythonProcess = new Process { StartInfo = psi };
            _pythonProcess.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Log($"[ai] {e.Data}");
            };
            _pythonProcess.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Log($"[ai-err] {e.Data}");
            };

            _pythonProcess.Start();
            _pythonProcess.BeginOutputReadLine();
            _pythonProcess.BeginErrorReadLine();

            // Conectar al socket con timeout
            var connected = await WaitForConnectionAsync(CONNECT_TIMEOUT_MS);
            if (!connected)
            {
                Log("❌ Timeout esperando el motor de IA");
                OnReady?.Invoke(false);
                return;
            }

            // Ping para confirmar
            var ping = await SendAsync<JsonElement>("{\"cmd\":\"ping\"}\n");
            Log("✅ Motor de IA listo");
            OnReady?.Invoke(true);
        }
        catch (Exception ex)
        {
            Log($"❌ Error en proceso IA: {ex.Message}");
            OnReady?.Invoke(false);
        }
    }

    /// <summary>
    /// Procesa todas las fotos "pending" de la BD en batches.
    /// Llama OnProgress a medida que avanza.
    /// </summary>
    public async Task ProcessPendingAsync(
        FotoshowContext db,
        CancellationToken ct = default)
    {
        if (_client == null || !_client.Connected)
        {
            Log("⚠️  Motor de IA no conectado");
            return;
        }

        List<Photo> pending;
        try
        {
            pending = await db.Photos
                .Where(p => p.Status == "pending")
                .OrderBy(p => p.UploadDate)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            Log($"❌ Error leyendo BD: {ex.Message}");
            return;
        }

        if (pending.Count == 0)
        {
            Log("✅ No hay fotos pendientes de IA");
            return;
        }

        Log($"🧠 Procesando {pending.Count} foto(s) con IA...");
        int done = 0;

        // Dividir en batches para no saturar el socket
        var batches = pending
            .Select((p, i) => (Photo: p, Index: i))
            .GroupBy(x => x.Index / BATCH_SIZE)
            .Select(g => g.Select(x => x.Photo).ToList())
            .ToList();

        foreach (var batch in batches)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var paths = batch.Select(p => p.LocalPath).ToList();
                var req   = JsonSerializer.Serialize(new AiRequest
                {
                    Cmd   = "analyze_batch",
                    Paths = paths
                }) + "\n";

                var resp = await SendAsync<AiBatchResponse>(req, ct);

                if (resp.Results == null) continue;

                // Guardar resultados en BD
                foreach (var result in resp.Results)
                {
                    if (result.Status != "ok") continue;

                    var photo = batch.FirstOrDefault(
                        p => p.LocalPath == result.Path);
                    if (photo == null) continue;

                    photo.FacesDetected = result.Faces;
                    photo.Status        = "processed";

                    if (result.Embedding != null && result.Embedding.Count > 0)
                    {
                        photo.Embedding = JsonSerializer.Serialize(result.Embedding);
                    }

                    done++;
                    OnProgress?.Invoke(done, pending.Count);
                }

                // Guardar batch en BD
                await db.SaveChangesAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"⚠️  Error en batch: {ex.Message}");
            }
        }

        Log($"✅ IA completada: {done}/{pending.Count} fotos procesadas");

        // Después de procesar, recalcular clustering de personas
        if (done > 0)
            await ClusterFacesAsync(db, ct);
    }

    // ═══════════════════════════════════════════════════════════
    // CLUSTERING — agrupa fotos por persona
    // ═══════════════════════════════════════════════════════════
    /// <summary>
    /// Agrupa todas las fotos con cara en "personas" usando
    /// similitud coseno de embeddings. Asigna PersonId (int) a cada foto.
    /// Escala bien a miles de fotos porque no usa k-means sino
    /// union-find incremental O(n log n).
    /// </summary>
    public async Task ClusterFacesAsync(
        FotoshowContext db,
        CancellationToken ct = default)
    {
        Log("👥 Calculando grupos de personas...");

        var photos = await db.Photos
            .Where(p => p.Embedding != null && p.FacesDetected > 0)
            .ToListAsync(ct);

        if (photos.Count == 0)
        {
            Log("ℹ️  Sin fotos con caras para agrupar");
            return;
        }

        // Deserializar embeddings
        var items = photos
            .Select(p => new
            {
                Photo     = p,
                Embedding = TryDeserializeEmbedding(p.Embedding!)
            })
            .Where(x => x.Embedding != null)
            .ToList();

        int n = items.Count;
        Log($"👥 Agrupando {n} fotos con caras...");

        // Union-Find
        var parent = Enumerable.Range(0, n).ToArray();

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        void Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a != b) parent[b] = a;
        }

        // Comparar cada par — O(n²) pero con early-exit y threshold
        // Para n > 5000 podría optimizarse con HNSW, pero para uso normal es suficiente
        for (int i = 0; i < n; i++)
        {
            if (ct.IsCancellationRequested) break;
            for (int j = i + 1; j < n; j++)
            {
                var sim = CosineSimilarity(items[i].Embedding!, items[j].Embedding!);
                if (sim >= SIMILARITY_THRESHOLD)
                    Union(i, j);
            }
        }

        // Asignar PersonId a cada raíz
        var rootToPersonId = new Dictionary<int, int>();
        int nextId = 1;
        for (int i = 0; i < n; i++)
        {
            var root = Find(i);
            if (!rootToPersonId.ContainsKey(root))
                rootToPersonId[root] = nextId++;
        }

        // Guardar PersonId en las fotos
        for (int i = 0; i < n; i++)
        {
            items[i].Photo.PersonId = rootToPersonId[Find(i)];
        }

        await db.SaveChangesAsync(ct);

        Log($"✅ {nextId - 1} persona(s) detectada(s) en {n} foto(s)");
    }

    // ═══════════════════════════════════════════════════════════
    // BÚSQUEDA por foto de referencia
    // ═══════════════════════════════════════════════════════════
    /// <summary>
    /// Dado un path de foto de referencia, devuelve todas las fotos
    /// de la BD que contienen la misma persona, ordenadas por similitud.
    /// </summary>
    public async Task<List<(Photo Photo, float Score)>> SearchByFaceAsync(
        string referencePhotoPath,
        FotoshowContext db,
        CancellationToken ct = default)
    {
        if (_client == null || !_client.Connected)
        {
            Log("⚠️  Motor IA no disponible para búsqueda");
            return new();
        }

        // Analizar la foto de referencia
        var req  = JsonSerializer.Serialize(new AiRequest
        {
            Cmd  = "analyze",
            Path = referencePhotoPath
        }) + "\n";

        var refResult = await SendAsync<AiPhotoResult>(req, ct);

        if (refResult.Status != "ok" || refResult.Embedding == null)
        {
            Log($"⚠️  No se detectó cara en la foto de referencia");
            return new();
        }

        var refEmb = refResult.Embedding.ToArray();

        // Buscar en todas las fotos con embedding
        var photosWithEmbedding = await db.Photos
            .Where(p => p.Embedding != null && p.FacesDetected > 0)
            .ToListAsync(ct);

        var results = new List<(Photo, float)>();

        foreach (var photo in photosWithEmbedding)
        {
            var emb = TryDeserializeEmbedding(photo.Embedding!);
            if (emb == null) continue;

            var score = CosineSimilarity(refEmb, emb);
            if (score >= SIMILARITY_THRESHOLD)
                results.Add((photo, score));
        }

        return results
            .OrderByDescending(r => r.Item2)
            .ToList();
    }

    // ═══════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════
    private async Task<T> SendAsync<T>(
        string jsonLine,
        CancellationToken ct = default)
    {
        await _sockLock.WaitAsync(ct);
        try
        {
            var data = Encoding.UTF8.GetBytes(jsonLine);
            await _stream!.WriteAsync(data, ct);
            await _stream.FlushAsync(ct);

            var line = await _reader!.ReadLineAsync();
            if (string.IsNullOrEmpty(line))
                throw new IOException("Conexión cerrada por el servidor IA");

            return JsonSerializer.Deserialize<T>(line)
                ?? throw new JsonException("Respuesta IA vacía");
        }
        finally
        {
            _sockLock.Release();
        }
    }

    private async Task<bool> WaitForConnectionAsync(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync(HOST, PORT);
                _stream = _client.GetStream();
                _reader = new StreamReader(_stream, Encoding.UTF8);
                return true;
            }
            catch
            {
                _client?.Dispose();
                _client = null;
                await Task.Delay(500);
            }
        }
        return false;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        if (normA == 0 || normB == 0) return 0f;
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    private static float[]? TryDeserializeEmbedding(string json)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<float>>(json);
            return list?.ToArray();
        }
        catch { return null; }
    }

    private static void KillExistingProcess()
    {
        try
        {
            // Intentar conectar y mandar quit
            using var c = new TcpClient();
            c.Connect(HOST, PORT);
            using var s = c.GetStream();
            var quit = Encoding.UTF8.GetBytes("{\"cmd\":\"quit\"}\n");
            s.Write(quit);
        }
        catch { /* No había proceso previo */ }
    }

    private static string FindPython()
    {
        // Buscar en orden de preferencia
        foreach (var candidate in new[] { "python", "python3", "py" })
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName  = candidate,
                    Arguments = "--version",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow         = true
                });
                p?.WaitForExit(2000);
                if (p?.ExitCode == 0) return candidate;
            }
            catch { }
        }
        return "python"; // fallback
    }

    private void Log(string msg)
        => OnLog?.Invoke(msg);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // Mandar quit graceful
            if (_client?.Connected == true)
            {
                var quit = Encoding.UTF8.GetBytes("{\"cmd\":\"quit\"}\n");
                _stream?.Write(quit);
            }
        }
        catch { }
        _reader?.Dispose();
        _stream?.Dispose();
        _client?.Dispose();
        try { _pythonProcess?.Kill(); } catch { }
        _pythonProcess?.Dispose();
    }
}
