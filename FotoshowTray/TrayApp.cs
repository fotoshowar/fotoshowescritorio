using System.Diagnostics;
using FotoshowTray.Models;

namespace FotoshowTray;

/// <summary>
/// ApplicationContext del tray — corre sin ventana principal.
/// Gestiona el NotifyIcon, el ciclo de vida de servicios y la UI mínima.
/// </summary>
public class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly TrayConfig _config;
    private readonly LocalDb _db;
    private readonly AiClient _ai;
    private readonly BackendClient _backend;
    private readonly PhotoQueue _queue;
    private readonly PipeServer _pipe;
    private readonly TrayPopup _popup;
    private readonly CancellationTokenSource _appCts = new();

    // Ítems del menú que se actualizan dinámicamente
    private readonly ToolStripMenuItem _itemLogin;
    private readonly ToolStripMenuItem _itemLogout;

    public TrayApp()
    {
        _config = TrayConfig.Load();
        _db = new LocalDb();
        _ai = new AiClient();
        _backend = new BackendClient(_config);
        _queue = new PhotoQueue(_db, _ai, _backend, _config);
        _pipe = new PipeServer();
        _popup = new TrayPopup();
        _ = _popup.Handle;  // Forzar creación del handle en el hilo UI para que InvokeIfNeeded funcione desde threads de background

        // ─── menú del tray ─────────────────────────────────────────────────────
        var menu = new ContextMenuStrip();

        var lblStatus = new ToolStripMenuItem("FotoShow") { Enabled = false, Font = new Font("Segoe UI", 9, FontStyle.Bold) };
        menu.Items.Add(lblStatus);
        menu.Items.Add(new ToolStripSeparator());

        var itemQueue = new ToolStripMenuItem("Cola: 0 fotos pendientes") { Enabled = false };
        menu.Items.Add(itemQueue);
        menu.Items.Add(new ToolStripSeparator());

        _itemLogin  = new ToolStripMenuItem("Iniciar sesión...", null, OnLoginClick);
        _itemLogout = new ToolStripMenuItem("Cerrar sesión", null, OnLogoutClick)
            { ForeColor = Color.FromArgb(0xff, 0x60, 0x60) };

        menu.Items.Add(_itemLogin);
        menu.Items.Add(_itemLogout);

        var itemWeb = new ToolStripMenuItem("Abrir FotoShow.online", null, (_, _) =>
            OpenUrl(_config.BackendUrl));
        menu.Items.Add(itemWeb);

        menu.Items.Add(new ToolStripSeparator());

        // Pegar token manualmente (hasta que el installer registre fotoshow://)
        menu.Items.Add(new ToolStripMenuItem("Iniciar sesión con token...", null, OnPasteTokenClick));

        menu.Items.Add(new ToolStripMenuItem("Salir", null, OnExitClick));

        // ─── notify icon ───────────────────────────────────────────────────────
        _tray = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
            Text = "FotoShow",
            Icon = LoadIcon("fotoshow.ico")
        };
        _tray.DoubleClick += (_, _) => OpenUrl(_config.BackendUrl);

        // ─── eventos de servicios ──────────────────────────────────────────────
        _ai.OnLog += Log;
        _queue.OnLog += Log;
        _queue.OnQueueCountChanged += count =>
        {
            if (InvokeRequired(itemQueue))
                itemQueue.Owner!.Invoke(() => itemQueue.Text = $"Cola: {count} fotos pendientes");
            else
                itemQueue.Text = $"Cola: {count} fotos pendientes";
        };
        _queue.OnProgressChanged += (done, total, file) => _popup.ShowProgress(done, total, file);
        _queue.OnSyncDone += total =>
        {
            _popup.ShowDone(total);
            _tray.ShowBalloonTip(4000, "FotoShow",
                $"✓  {total} foto{(total == 1 ? "" : "s")} subida{(total == 1 ? "" : "s")} a FotoShow",
                ToolTipIcon.Info);
        };

        _pipe.OnLog += Log;
        _pipe.OnAddFile += path => _queue.EnqueueFile(path);
        _pipe.OnAddFolder += path =>
        {
            // Mostrar diálogo en el hilo UI para elegir/crear galería con todos los datos
            if (_itemLogin.Owner?.InvokeRequired == true)
                _itemLogin.Owner.Invoke(() => ShowGalleryDialogAndEnqueue(path));
            else
                ShowGalleryDialogAndEnqueue(path);
        };

        _pipe.OnAuthToken += jwt =>
        {
            _config.JwtToken = jwt;
            _config.PhotographerEmail = ExtractEmailFromJwt(jwt) ?? "Fotógrafo";
            _config.PhotographerName  = _config.PhotographerEmail;
            _config.Save();
            _backend.SetToken(jwt);
            _ = _backend.StartListeningAsync(_appCts.Token);
            RefreshAuthMenu();
            _popup.ShowMessage("✓  Sesión iniciada");
        };

        _backend.OnLog += Log;
        _backend.OnTokenExpired += () =>
        {
            _popup.ShowMessage("⚠  Sesión expirada — iniciá sesión nuevamente.");
            _config.JwtToken = null;
            _config.Save();
            RefreshAuthMenu();
        };
        _backend.OnSaleReceived += OnSaleNotification;

        // Actualizar menú con estado inicial (puede haber un JWT guardado)
        if (_config.IsLoggedIn && string.IsNullOrEmpty(_config.PhotographerEmail) && !string.IsNullOrEmpty(_config.JwtToken))
            _config.PhotographerEmail = ExtractEmailFromJwt(_config.JwtToken) ?? _config.PhotographerName;
        RefreshAuthMenu();

        // ─── arranque de servicios ─────────────────────────────────────────────
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await _db.InitAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error iniciando base de datos:\n{ex.Message}", "FotoShow — Error crítico",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Exit();
            return;
        }

        // Arrancar cola y pipe INMEDIATAMENTE — no esperar a la IA
        _pipe.Start();
        _queue.Start();

        if (_config.IsLoggedIn)
            _ = _backend.StartListeningAsync(_appCts.Token);

        // IA en background — la cola funciona sin ella (solo sin embedding facial)
        _ = InitAiAsync();
    }

    // ─── inicio de IA en background ────────────────────────────────────────────

    private async Task InitAiAsync()
    {
        _popup.ShowMessage("⚙  Iniciando motor de IA (1ª vez: descargando modelos)...", autoHide: false);
        try
        {
            var aiOk = await _ai.EnsureWorkerRunningAsync(_appCts.Token);
            _popup.ShowMessage(aiOk
                ? "✓  Motor de IA listo"
                : "⚠  IA no disponible — fotos se subirán sin análisis facial", autoHide: true);
        }
        catch
        {
            _popup.ShowMessage("⚠  IA no disponible — fotos se subirán sin análisis facial", autoHide: true);
        }
    }

    // ─── galería ───────────────────────────────────────────────────────────────

    private void ShowGalleryDialogAndEnqueue(string folderPath)
    {
        List<GalleryInfo> galleries = [];

        // Cargar galerías del backend si el usuario está logueado
        if (_config.IsLoggedIn)
        {
            try
            {
                galleries = _backend.ListGalleriesAsync().GetAwaiter().GetResult();
            }
            catch { /* sin conexión — continuar con lista vacía */ }
        }

        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderName)) folderName = folderPath;

        using var dlg = new GalleryDialog(folderName, galleries);
        if (dlg.ShowDialog() != DialogResult.OK) return;

        if (dlg.NewGalleryData is { } newData)
        {
            // Crear galería con todos los campos usando el endpoint completo
            _ = Task.Run(async () =>
            {
                string? galleryId = null;
                if (_config.IsLoggedIn)
                {
                    try { galleryId = await _backend.CreateGalleryFullAsync(newData); }
                    catch { Log($"No se pudo crear galería '{newData.Name}' — subiendo sin galería"); }
                }
                _queue.EnqueueFolder(folderPath, galleryId);
            });
        }
        else
        {
            // Galería existente seleccionada
            _queue.EnqueueFolder(folderPath, dlg.SelectedGalleryId);
        }
    }

    // ─── venta recibida ────────────────────────────────────────────────────────

    private void OnSaleNotification(SaleNotification sale)
    {
        Log($"¡Venta! foto {sale.BackendPhotoId} para {sale.BuyerEmail}");
        _popup.ShowSale(sale.EventTitle, sale.BuyerEmail);

        _ = Task.Run(async () =>
        {
            // Buscar la foto local por path_hash
            var record = await _db.Photos
                .FirstOrDefaultAsync(p => p.PathHash == sale.PhotoPathHash);

            if (record is null || !File.Exists(record.LocalPath))
            {
                Log($"Foto no encontrada localmente: {sale.PhotoPathHash}");
                return;
            }

            var ok = await _backend.DeliverPhotoAsync(record.LocalPath, sale.OrderItemId, _appCts.Token);
            if (ok)
            {
                record.Status = "delivered";
                record.SoldAt = DateTime.UtcNow;
                record.DeliveredAt = DateTime.UtcNow;
                record.OrderItemId = sale.OrderItemId.ToString();
                await _db.SaveChangesAsync(_appCts.Token);

                _tray.ShowBalloonTip(3000, "FotoShow", $"Foto entregada a {sale.BuyerEmail}", ToolTipIcon.Info);
            }
        });
    }

    // ─── login ─────────────────────────────────────────────────────────────────

    private void OnLoginClick(object? sender, EventArgs e)
    {
        if (_config.IsLoggedIn)
        {
            // Abrir panel del fotógrafo pasando el JWT para crear sesión de browser
            OpenUrl(_backend.GetDashboardUrl());
        }
        else
        {
            // Abrir browser para login con Google
            // El backend redirige a fotoshow://auth?token=... que capturamos con URI scheme
            OpenUrl(_backend.GetDesktopLoginUrl());
        }
    }

    // ─── pegar token (dev) ────────────────────────────────────────────────────

    private void OnPasteTokenClick(object? sender, EventArgs e)
    {
        // Leer del clipboard si hay algo
        var clipboard = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
        var token = clipboard.StartsWith("eyJ") ? clipboard : "";

        using var form  = new Form
        {
            Text            = "FotoShow — Token JWT",
            Width           = 420,
            Height          = 160,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition   = FormStartPosition.CenterScreen,
            MaximizeBox     = false,
            MinimizeBox     = false,
            BackColor       = Color.FromArgb(0x1a, 0x1a, 0x1a),
            ForeColor       = Color.White,
        };
        var lbl = new Label { Text = "Pegá el JWT que devuelve /api/auth/google/desktop:", Dock = DockStyle.Top, Height = 32, ForeColor = Color.FromArgb(0x7C, 0xFC, 0x00), Padding = new Padding(8, 8, 0, 0) };
        var txt = new TextBox { Text = token, Dock = DockStyle.Top, Height = 28, BackColor = Color.FromArgb(0x2a, 0x2a, 0x2a), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        var btn = new Button  { Text = "Guardar", Dock = DockStyle.Bottom, Height = 32, BackColor = Color.FromArgb(0x7C, 0xFC, 0x00), ForeColor = Color.Black, FlatStyle = FlatStyle.Flat };

        btn.Click += (_, _) =>
        {
            var jwt = txt.Text.Trim();
            if (string.IsNullOrEmpty(jwt)) return;
            _config.JwtToken = jwt;
            _config.PhotographerEmail = ExtractEmailFromJwt(jwt) ?? "Fotógrafo";
            _config.PhotographerName  = _config.PhotographerEmail;
            _config.Save();
            _backend.SetToken(jwt);
            _ = _backend.StartListeningAsync(_appCts.Token);
            RefreshAuthMenu();
            _popup.ShowMessage("✓  Sesión iniciada");
            form.Close();
        };

        form.Controls.AddRange([lbl, txt, btn]);
        form.ShowDialog();
    }

    // ─── salida ────────────────────────────────────────────────────────────────

    private void OnExitClick(object? sender, EventArgs e)
    {
        _appCts.Cancel();
        _tray.Visible = false;
        _popup.Dispose();
        _pipe.Dispose();
        _queue.Dispose();
        _ai.Dispose();
        _ = _backend.DisposeAsync().AsTask();
        _db.Dispose();
        Application.Exit();
    }

    // ─── auth menu ─────────────────────────────────────────────────────────────

    private void RefreshAuthMenu()
    {
        void Update()
        {
            if (_config.IsLoggedIn)
            {
                _itemLogin.Text    = $"Sesión: {_config.PhotographerEmail ?? _config.PhotographerName ?? "Fotógrafo"}";
                _itemLogin.Enabled = true;
                _itemLogout.Visible = true;
            }
            else
            {
                _itemLogin.Text    = "Iniciar sesión...";
                _itemLogin.Enabled = true;
                _itemLogout.Visible = false;
            }
        }

        if (_itemLogin.Owner?.InvokeRequired == true)
            _itemLogin.Owner.Invoke(Update);
        else
            Update();
    }

    private void OnLogoutClick(object? sender, EventArgs e)
    {
        _config.JwtToken = null;
        _config.PhotographerName = null;
        _config.PhotographerEmail = null;
        _config.Save();
        RefreshAuthMenu();
        _popup.ShowMessage("Sesión cerrada.");
    }

    private static string? ExtractEmailFromJwt(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;

            // Base64url → Base64
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);

            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("email", out var email) && email.GetString() is string e and not "")
                return e;
            if (root.TryGetProperty("sub", out var sub))
                return sub.GetString();

            return null;
        }
        catch { return null; }
    }

    // ─── helpers ───────────────────────────────────────────────────────────────

    private void Log(string msg) =>
        System.Diagnostics.Debug.WriteLine($"[FotoshowTray] {msg}");

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private static bool InvokeRequired(ToolStripItem item) =>
        item.Owner?.InvokeRequired == true;

    private static Icon LoadIcon(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", name);
        if (File.Exists(path)) return new Icon(path);

        // Fallback: icono del sistema
        return SystemIcons.Application;
    }

    // Necesario para acceder a EF desde cualquier thread
    private async Task<PhotoRecord?> FirstOrDefaultAsync(
        Func<PhotoRecord, bool> predicate, CancellationToken ct)
    {
        return await Task.Run(() => _db.Photos.FirstOrDefault(predicate), ct);
    }
}

// helper extension para FindAsync en otro thread
file static class DbExtensions
{
    public static Task<T?> FirstOrDefaultAsync<T>(
        this Microsoft.EntityFrameworkCore.DbSet<T> set,
        Func<T, bool> pred) where T : class
        => Task.Run(() => set.FirstOrDefault(pred));
}
