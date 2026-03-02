using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FotoshowTray.Models;

namespace FotoshowTray;

/// <summary>
/// Cliente HTTP + WebSocket hacia fotoshow.online.
/// - SyncPhotoMetadata: sube thumbnail + embedding (NO la foto original)
/// - ListenForSalesAsync: WebSocket que recibe notificaciones de venta
/// - DeliverPhotoAsync: sube la foto original cuando hay una venta
/// </summary>
public class BackendClient : IAsyncDisposable
{
    private readonly TrayConfig _config;
    private readonly HttpClient _http;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;

    public event Action<SaleNotification>? OnSaleReceived;
    public event Action<string>? OnLog;
    public event Action? OnTokenExpired;

    public BackendClient(TrayConfig config)
    {
        _config = config;
        _http = new HttpClient { BaseAddress = new Uri(config.BackendUrl) };
        if (!string.IsNullOrEmpty(config.JwtToken))
            SetToken(config.JwtToken);
    }

    public void SetToken(string token)
    {
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
    }

    // ─── sync metadata ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sube thumbnail (como multipart) + embedding JSON al backend.
    /// Retorna el backend_photo_id si OK, null si error.
    /// </summary>
    public async Task<string?> SyncPhotoMetadataAsync(PhotoRecord record, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(record.ThumbnailPath) || !File.Exists(record.ThumbnailPath))
            return null;

        try
        {
            using var form = new MultipartFormDataContent();

            // Thumbnail
            var thumbBytes = await File.ReadAllBytesAsync(record.ThumbnailPath, ct);
            var thumbContent = new ByteArrayContent(thumbBytes);
            thumbContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(thumbContent, "thumbnail", Path.GetFileName(record.ThumbnailPath));

            // Metadata
            form.Add(new StringContent(record.LocalPath), "local_path");
            form.Add(new StringContent(record.PathHash), "path_hash");
            form.Add(new StringContent(record.FacesDetected.ToString()), "faces_detected");
            form.Add(new StringContent(record.FileSize.ToString()), "file_size");

            if (!string.IsNullOrEmpty(record.Embedding))
                form.Add(new StringContent(record.Embedding), "embedding");

            if (!string.IsNullOrEmpty(record.BackendGalleryId))
                form.Add(new StringContent(record.BackendGalleryId), "gallery_id");

            var resp = await _http.PostAsync("/api/desktop/sync", form, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                OnTokenExpired?.Invoke();
                return null;
            }

            if (!resp.IsSuccessStatusCode)
            {
                OnLog?.Invoke($"Sync error {resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("photo_id").GetString();
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"SyncMetadata error: {ex.Message}");
            return null;
        }
    }

    // ─── entrega de foto original (directo a R2, servidor no toca el binario) ───

    /// <summary>
    /// Entrega la foto original cuando hay una venta:
    /// 1. Pide presigned PUT URL al backend
    /// 2. Sube la foto DIRECTO a R2 (Cloudflare) — el servidor no recibe el archivo
    /// 3. Confirma al backend que ya subió
    /// </summary>
    public async Task<bool> DeliverPhotoAsync(string localPath, int orderItemId, CancellationToken ct = default)
    {
        if (!File.Exists(localPath)) return false;

        try
        {
            // Paso 1: obtener presigned URL
            var urlResp = await _http.GetAsync($"/api/desktop/upload-url?order_item_id={orderItemId}", ct);
            if (!urlResp.IsSuccessStatusCode)
            {
                OnLog?.Invoke($"Error obteniendo upload URL: {urlResp.StatusCode}");
                return false;
            }

            var urlJson = await urlResp.Content.ReadAsStringAsync(ct);
            using var urlDoc = JsonDocument.Parse(urlJson);
            var uploadUrl = urlDoc.RootElement.GetProperty("upload_url").GetString()!;
            var s3Key = urlDoc.RootElement.GetProperty("s3_key").GetString()!;

            // Paso 2: subir directo a R2 con PUT (sin pasar por nuestro servidor)
            var photoBytes = await File.ReadAllBytesAsync(localPath, ct);
            using var r2Client = new HttpClient();
            var putContent = new ByteArrayContent(photoBytes);
            putContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            var putResp = await r2Client.PutAsync(uploadUrl, putContent, ct);

            if (!putResp.IsSuccessStatusCode)
            {
                OnLog?.Invoke($"Error subiendo a R2: {putResp.StatusCode}");
                return false;
            }

            // Paso 3: confirmar al backend
            using var confirmForm = new MultipartFormDataContent();
            confirmForm.Add(new StringContent(orderItemId.ToString()), "order_item_id");
            confirmForm.Add(new StringContent(s3Key), "s3_key");
            var confirmResp = await _http.PostAsync("/api/desktop/deliver-confirm", confirmForm, ct);

            if (!confirmResp.IsSuccessStatusCode)
            {
                OnLog?.Invoke($"Error confirmando entrega: {confirmResp.StatusCode}");
                return false;
            }

            OnLog?.Invoke($"Foto entregada directo a R2: {Path.GetFileName(localPath)}");
            return true;
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"Deliver error: {ex.Message}");
            return false;
        }
    }

    // ─── WebSocket — notificaciones de venta ───────────────────────────────────

    public async Task StartListeningAsync(CancellationToken appCt)
    {
        _wsCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);

        while (!_wsCts.Token.IsCancellationRequested)
        {
            try
            {
                await ConnectAndListenAsync(_wsCts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                OnLog?.Invoke($"WS desconectado: {ex.Message}. Reconectando en 10 s...");
                await Task.Delay(10_000, _wsCts.Token).ContinueWith(_ => { });
            }
        }
    }

    private async Task ConnectAndListenAsync(CancellationToken ct)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();

        if (!string.IsNullOrEmpty(_config.JwtToken))
            _ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.JwtToken}");

        var wsUrl = _config.BackendUrl
            .Replace("https://", "wss://")
            .Replace("http://", "ws://")
            + "/api/desktop/events";

        await _ws.ConnectAsync(new Uri(wsUrl), ct);
        OnLog?.Invoke("WebSocket conectado — esperando ventas...");

        var buffer = new byte[4096];
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await _ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;

            var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
            try
            {
                var notification = JsonSerializer.Deserialize<SaleNotification>(msg,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (notification is not null)
                {
                    OnLog?.Invoke($"VENTA recibida: {notification.PhotoLocalPath}");
                    OnSaleReceived?.Invoke(notification);
                }
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"WS parse error: {ex.Message}");
            }
        }
    }

    // ─── autenticación ─────────────────────────────────────────────────────────

    /// <summary>
    /// Retorna la URL para autenticar al fotógrafo desde el browser del desktop.
    /// El backend debe redirigir de vuelta con el token en el callback.
    /// </summary>
    public string GetDesktopLoginUrl() =>
        $"{_config.BackendUrl}/api/auth/google/desktop?redirect_uri=fotoshow://auth";

    public async ValueTask DisposeAsync()
    {
        _wsCts?.Cancel();
        if (_ws is not null)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
            catch { }
            _ws.Dispose();
        }
        _wsCts?.Dispose();
        _http.Dispose();
    }
}

// ─── modelos de WebSocket ───────────────────────────────────────────────────

public record SaleNotification(
    int OrderItemId,
    string PhotoPathHash,    // SHA-256 del path local — para encontrar el archivo
    int BackendPhotoId,
    string BuyerEmail,
    string EventTitle
);
