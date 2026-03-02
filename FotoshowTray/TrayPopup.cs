using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace FotoshowTray;

/// <summary>
/// Ventana flotante que aparece arriba del tray (como OneDrive/Google Drive).
/// Estética de fotoshow.site/admin: fondo #0a0a0a, verde #7CFC00, fuente Segoe UI.
/// Se posiciona automáticamente arriba de la barra de tareas, lado derecho.
/// </summary>
public class TrayPopup : Form
{
    // ── colores del sitio ──────────────────────────────────────────────────────
    private static readonly Color BgPrimary    = Color.FromArgb(0x0a, 0x0a, 0x0a);
    private static readonly Color BgCard       = Color.FromArgb(0x1a, 0x1a, 0x1a);
    private static readonly Color GreenPrimary = Color.FromArgb(0x7C, 0xFC, 0x00);
    private static readonly Color GreenDark    = Color.FromArgb(0x32, 0xCD, 0x32);
    private static readonly Color TextPrimary  = Color.White;
    private static readonly Color TextMuted    = Color.FromArgb(0xa0, 0xa0, 0xa0);
    private static readonly Color BorderColor  = Color.FromArgb(0x2a, 0x2a, 0x2a);

    // ── controles ─────────────────────────────────────────────────────────────
    private readonly PictureBox   _logo;
    private readonly Label        _lblTitle;
    private readonly Label        _lblStatus;
    private readonly Label        _lblCount;
    private readonly ProgressBar  _progress;
    private readonly Panel        _separator;
    private readonly Label        _lblSale;          // notificación de venta
    private readonly System.Windows.Forms.Timer _hideTimer;
    private readonly System.Windows.Forms.Timer _fadeTimer;

    private int   _totalFiles;
    private int   _doneFiles;
    private float _opacity = 0f;
    private bool  _fadingIn;

    public TrayPopup()
    {
        // ── ventana sin bordes ─────────────────────────────────────────────────
        FormBorderStyle  = FormBorderStyle.None;
        ShowInTaskbar    = false;
        TopMost          = true;
        BackColor        = BgPrimary;
        Width            = 300;
        Height           = 130;
        Opacity          = 0;
        StartPosition    = FormStartPosition.Manual;
        DoubleBuffered   = true;

        // ── logo 32x32 ─────────────────────────────────────────────────────────
        _logo = new PictureBox
        {
            Size     = new Size(32, 32),
            Location = new Point(14, 14),
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "fotoshow.ico");
            if (File.Exists(iconPath))
                _logo.Image = new Icon(iconPath, 32, 32).ToBitmap();
        }
        catch { }

        // ── título ─────────────────────────────────────────────────────────────
        _lblTitle = new Label
        {
            Text      = "FotoShow",
            ForeColor = GreenPrimary,
            Font      = new Font("Segoe UI", 11, FontStyle.Bold),
            AutoSize  = true,
            Location  = new Point(54, 14),
        };

        // ── estado (línea principal) ───────────────────────────────────────────
        _lblStatus = new Label
        {
            Text      = "Listo",
            ForeColor = TextPrimary,
            Font      = new Font("Segoe UI", 9),
            AutoSize  = false,
            Size      = new Size(272, 18),
            Location  = new Point(14, 50),
        };

        // ── contador derecha ───────────────────────────────────────────────────
        _lblCount = new Label
        {
            Text      = "",
            ForeColor = TextMuted,
            Font      = new Font("Segoe UI", 8),
            AutoSize  = true,
            Location  = new Point(54, 34),
        };

        // ── barra de progreso custom ───────────────────────────────────────────
        _progress = new ProgressBar
        {
            Size     = new Size(272, 6),
            Location = new Point(14, 72),
            Minimum  = 0,
            Maximum  = 100,
            Value    = 0,
            Style    = ProgressBarStyle.Continuous,
        };
        // custom render vía owner draw (ver OnPaint)

        // ── separador ─────────────────────────────────────────────────────────
        _separator = new Panel
        {
            Size      = new Size(272, 1),
            Location  = new Point(14, 86),
            BackColor = BorderColor,
        };

        // ── notificación de venta ─────────────────────────────────────────────
        _lblSale = new Label
        {
            Text      = "",
            ForeColor = GreenPrimary,
            Font      = new Font("Segoe UI", 8, FontStyle.Bold),
            AutoSize  = false,
            Size      = new Size(272, 32),
            Location  = new Point(14, 92),
            Visible   = false,
        };

        Controls.AddRange([_logo, _lblTitle, _lblCount, _lblStatus, _progress, _separator, _lblSale]);

        // ── timer para auto-ocultar ────────────────────────────────────────────
        _hideTimer = new System.Windows.Forms.Timer { Interval = 4000 };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); FadeOut(); };

        // ── timer para fade in/out ─────────────────────────────────────────────
        _fadeTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _fadeTimer.Tick += OnFadeTick;

        // ── borde redondeado ───────────────────────────────────────────────────
        SetRoundedCorners(12);
    }

    // ── API pública ────────────────────────────────────────────────────────────

    /// <summary>Mostrar progreso de subida de cola de fotos.</summary>
    public void ShowProgress(int done, int total, string currentFile = "")
    {
        if (IsDisposed) return;

        _totalFiles = total;
        _doneFiles  = done;

        InvokeIfNeeded(() =>
        {
            _lblStatus.Text  = total == 0
                ? "Procesando..."
                : string.IsNullOrEmpty(currentFile)
                    ? $"Subiendo fotos..."
                    : $"Subiendo: {Path.GetFileName(currentFile)}";

            _lblCount.Text   = total > 0 ? $"{done}/{total}" : "";
            _progress.Value  = total > 0 ? Math.Clamp((int)(done * 100.0 / total), 0, 100) : 0;
            _lblSale.Visible = false;
            _separator.Visible = false;

            AdjustHeight(false);
            PositionNearTray();
            FadeIn();
            Invalidate();        // repintar barra custom
            _hideTimer.Stop();   // no ocultar mientras hay progreso activo
        });
    }

    /// <summary>Mostrar que terminó de sincronizar.</summary>
    public void ShowDone(int totalSynced)
    {
        if (IsDisposed) return;
        InvokeIfNeeded(() =>
        {
            _lblStatus.Text = $"✓  {totalSynced} foto{(totalSynced == 1 ? "" : "s")} sincronizada{(totalSynced == 1 ? "" : "s")}";
            _lblCount.Text  = "";
            _progress.Value = 100;
            _lblSale.Visible = false;
            _separator.Visible = false;

            AdjustHeight(false);
            PositionNearTray();
            FadeIn();
            Invalidate();
            _hideTimer.Start();
        });
    }

    /// <summary>Mostrar notificación de venta recibida.</summary>
    public void ShowSale(string eventTitle, string buyerEmail)
    {
        if (IsDisposed) return;
        InvokeIfNeeded(() =>
        {
            _lblStatus.Text  = "📸  ¡Nueva venta!";
            _lblCount.Text   = "";
            _progress.Value  = 0;

            _separator.Visible = true;
            _lblSale.Text    = $"{eventTitle}\n{buyerEmail}";
            _lblSale.Visible = true;

            AdjustHeight(true);
            PositionNearTray();
            FadeIn();
            _hideTimer.Interval = 7000;
            _hideTimer.Start();
        });
    }

    /// <summary>Mostrar mensaje de estado genérico.</summary>
    public void ShowMessage(string message, bool autoHide = true)
    {
        if (IsDisposed) return;
        InvokeIfNeeded(() =>
        {
            _lblStatus.Text  = message;
            _lblCount.Text   = "";
            _progress.Value  = 0;
            _lblSale.Visible = false;
            _separator.Visible = false;

            AdjustHeight(false);
            PositionNearTray();
            FadeIn();
            if (autoHide)
                _hideTimer.Start();
            else
                _hideTimer.Stop();
        });
    }

    // ── renderizado custom ─────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Borde verde sutil
        using var borderPen = new Pen(Color.FromArgb(60, GreenPrimary), 1f);
        g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

        // Barra de progreso manual (el ProgressBar nativo ignora BackColor en Win11)
        var pb = _progress.Bounds;
        using var bgBrush = new SolidBrush(Color.FromArgb(0x2a, 0x2a, 0x2a));
        g.FillRectangle(bgBrush, pb);

        if (_progress.Value > 0)
        {
            int fillW = (int)(pb.Width * (_progress.Value / 100.0));
            using var grad = new LinearGradientBrush(
                new Rectangle(pb.X, pb.Y, Math.Max(fillW, 1), pb.Height),
                GreenPrimary, GreenDark, LinearGradientMode.Horizontal);
            g.FillRectangle(grad, pb.X, pb.Y, fillW, pb.Height);
        }
    }

    // ── posicionamiento ────────────────────────────────────────────────────────

    private void PositionNearTray()
    {
        // Área de trabajo = pantalla menos taskbar
        var workArea = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
        Location = new Point(
            workArea.Right  - Width  - 12,
            workArea.Bottom - Height - 12
        );
    }

    private void AdjustHeight(bool withSale)
    {
        Height = withSale ? 160 : 130;
        if (withSale)
        {
            _separator.Location = new Point(14, 86);
            _lblSale.Location   = new Point(14, 94);
        }
    }

    // ── fade ───────────────────────────────────────────────────────────────────

    private void FadeIn()
    {
        if (!Visible) Show();
        _fadingIn = true;
        if (!_fadeTimer.Enabled) _fadeTimer.Start();
    }

    private void FadeOut()
    {
        _fadingIn = false;
        if (!_fadeTimer.Enabled) _fadeTimer.Start();
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        if (_fadingIn)
        {
            _opacity = Math.Min(1f, _opacity + 0.08f);
            Opacity  = _opacity;
            if (_opacity >= 1f) _fadeTimer.Stop();
        }
        else
        {
            _opacity = Math.Max(0f, _opacity - 0.06f);
            Opacity  = _opacity;
            if (_opacity <= 0f) { _fadeTimer.Stop(); Hide(); }
        }
    }

    // ── bordes redondeados (Win10+) ────────────────────────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void SetRoundedCorners(int radius)
    {
        // DWMWA_WINDOW_CORNER_PREFERENCE = 33, valor 2 = DWMWCP_ROUND
        try
        {
            int pref = 2;
            DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
        }
        catch { }
    }

    // ── helper ─────────────────────────────────────────────────────────────────

    private void InvokeIfNeeded(Action action)
    {
        if (IsHandleCreated && InvokeRequired)
            BeginInvoke(action);   // asíncrono: no bloquea el thread de procesamiento
        else if (IsHandleCreated)
            action();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hideTimer.Dispose();
            _fadeTimer.Dispose();
            _logo.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
