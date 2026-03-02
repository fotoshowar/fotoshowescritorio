using System.IO.Pipes;
using System.Text.Json;

namespace FotoshowTray;

/// <summary>
/// Named pipe server: recibe comandos de la shell extension o de instancias secundarias.
/// Nombre del pipe: "FotoshowTray"
/// Protocolo: una línea JSON por conexión.
///   {"action":"add_file","path":"C:\\fotos\\img001.jpg"}
///   {"action":"add_folder","path":"C:\\fotos\\evento"}
///   {"action":"ping"}
/// </summary>
public class PipeServer : IDisposable
{
    public const string PipeName = "FotoshowTray";

    private CancellationTokenSource _cts = new();
    private bool _disposed;

    public event Action<string>? OnLog;
    public event Action<string>? OnAddFile;
    public event Action<string>? OnAddFolder;

    public void Start()
    {
        Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // Crear un nuevo servidor para cada conexión
                var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous
                );

                await pipe.WaitForConnectionAsync(_cts.Token);
                _ = HandleClientAsync(pipe); // procesar en background sin await
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                OnLog?.Invoke($"PipeServer error: {ex.Message}");
                await Task.Delay(1000, _cts.Token).ContinueWith(_ => { });
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe);
            var line = await reader.ReadLineAsync(_cts.Token);
            if (string.IsNullOrEmpty(line)) return;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var action = root.GetProperty("action").GetString() ?? "";
            var path = root.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";

            switch (action)
            {
                case "add_file":
                    if (!string.IsNullOrEmpty(path))
                    {
                        OnLog?.Invoke($"pipe → agregar foto: {path}");
                        OnAddFile?.Invoke(path);
                    }
                    break;

                case "add_folder":
                    if (!string.IsNullOrEmpty(path))
                    {
                        OnLog?.Invoke($"pipe → agregar carpeta: {path}");
                        OnAddFolder?.Invoke(path);
                    }
                    break;

                case "ping":
                    // solo para verificar que el tray está corriendo
                    break;
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"PipeServer client error: {ex.Message}");
        }
        finally
        {
            pipe.Dispose();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cts.Cancel();
            _cts.Dispose();
            _disposed = true;
        }
    }
}

// ─── cliente para uso desde instancias secundarias y shell extension ─────────

public static class PipeClient
{
    public static async Task<bool> SendAsync(string action, string path = "", int timeoutMs = 2000)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeServer.PipeName, PipeDirection.Out);
            await pipe.ConnectAsync(timeoutMs);
            using var writer = new StreamWriter(pipe);
            var msg = JsonSerializer.Serialize(new { action, path });
            await writer.WriteLineAsync(msg);
            await writer.FlushAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsTrayRunning(int timeoutMs = 500)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeServer.PipeName, PipeDirection.Out);
            pipe.Connect(timeoutMs);
            return true;
        }
        catch { return false; }
    }
}
