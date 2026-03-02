using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FotoshowTray;

/// <summary>
/// Cliente TCP al ai_worker.exe local (puerto 54321).
/// Extrae embeddings faciales y cuenta caras de una foto.
/// Basado en el AiService.cs del proyecto original.
/// </summary>
public class AiClient : IDisposable
{
    private const string Host = "127.0.0.1";
    private const int Port = 54400;

    private Process? _workerProcess;
    private bool _disposed;

    public event Action<string>? OnLog;

    // ─── ciclo de vida del worker ───────────────────────────────────────────────

    public async Task<bool> EnsureWorkerRunningAsync(CancellationToken ct = default)
    {
        // Verificar si ya hay algo escuchando en el puerto
        if (await IsPortOpenAsync())
        {
            OnLog?.Invoke("ai_worker ya está corriendo.");
            return true;
        }

        var exeDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(exeDir, "FotoshowAI.exe"),
            Path.Combine(exeDir, "ai_worker.exe"),
        };

        string? exePath = candidates.FirstOrDefault(File.Exists);

        if (exePath != null)
        {
            OnLog?.Invoke($"Iniciando {Path.GetFileName(exePath)}...");
            _workerProcess = Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute    = false,
                CreateNoWindow     = true,
                WorkingDirectory   = Path.GetDirectoryName(exePath) ?? exeDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            });
            // Prioridad baja para que no trabe la UI ni el sistema
            try { if (_workerProcess != null) _workerProcess.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch { }
        }
        else
        {
            // Intentar con python portable empaquetado
            var portable = Path.Combine(exeDir, "Fotoshow_IA_Portable", "python", "python.exe");
            var script = Path.Combine(exeDir, "ai_worker.py");
            if (File.Exists(portable) && File.Exists(script))
            {
                OnLog?.Invoke("Iniciando ai_worker con Python portable...");
                _workerProcess = Process.Start(new ProcessStartInfo(portable, $"\"{script}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            else
            {
                OnLog?.Invoke("ai_worker.exe no encontrado — procesamiento de IA desactivado.");
                return false;
            }
        }

        // Esperar a que levante (hasta que CT sea cancelado — puede ser varios minutos)
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct).ContinueWith(_ => { });
            if (await IsPortOpenAsync()) return true;
        }

        OnLog?.Invoke("Timeout esperando ai_worker.");
        return false;
    }

    // ─── procesamiento de fotos ─────────────────────────────────────────────────

    public record AiResult(int FacesDetected, float[]? Embedding);

    /// <summary>Extrae embedding facial de una foto. Retorna null si falla.</summary>
    public async Task<AiResult?> ProcessPhotoAsync(string imagePath, CancellationToken ct = default)
    {
        try
        {
            var request = JsonSerializer.Serialize(new { cmd = "analyze", path = imagePath });
            var response = await SendReceiveAsync(request, ct);

            if (response is null) return null;

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            // Si el worker retornó error, loguear y salir
            if (root.TryGetProperty("status", out var statusEl) && statusEl.GetString() == "error")
            {
                var msg = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "error desconocido";
                OnLog?.Invoke($"ai_worker error: {msg}");
                return new AiResult(0, null);
            }

            if (!root.TryGetProperty("faces", out var facesEl)) return new AiResult(0, null);

            int faces = facesEl.GetInt32();
            float[]? embedding = null;

            if (root.TryGetProperty("embedding", out var embEl) && embEl.ValueKind == JsonValueKind.Array)
            {
                embedding = embEl.EnumerateArray().Select(e => e.GetSingle()).ToArray();
            }

            return new AiResult(faces, embedding);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"AiClient error procesando {Path.GetFileName(imagePath)}: {ex.Message}");
            return null;
        }
    }

    // ─── TCP helpers ────────────────────────────────────────────────────────────

    private async Task<string?> SendReceiveAsync(string json, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(Host, Port, ct);

        var stream = client.GetStream();
        var payload = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(payload, ct);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadLineAsync(ct);
    }

    private async Task<bool> IsPortOpenAsync()
    {
        try
        {
            using var c = new TcpClient();
            await c.ConnectAsync(Host, Port);
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            try { _workerProcess?.Kill(); } catch { }
            _workerProcess?.Dispose();
            _disposed = true;
        }
    }
}
