namespace FotoshowTray;

/// <summary>
/// Diálogo para seleccionar o crear una galería antes de subir una carpeta.
/// Misma estética que TrayPopup: fondo oscuro, verde #7CFC00.
/// </summary>
public class GalleryDialog : Form
{
    private static readonly Color BgPrimary    = Color.FromArgb(0x0a, 0x0a, 0x0a);
    private static readonly Color BgCard       = Color.FromArgb(0x1a, 0x1a, 0x1a);
    private static readonly Color GreenPrimary = Color.FromArgb(0x7C, 0xFC, 0x00);
    private static readonly Color TextPrimary  = Color.White;
    private static readonly Color TextMuted    = Color.FromArgb(0xa0, 0xa0, 0xa0);
    private static readonly Color BorderColor  = Color.FromArgb(0x3a, 0x3a, 0x3a);

    private readonly ComboBox  _cmbGallery;
    private readonly TextBox   _txtNewName;
    private readonly Label     _lblNew;
    private readonly Button    _btnOk;
    private readonly Button    _btnCancel;
    private readonly Label     _lblFolder;

    /// <summary>ID de galería seleccionada (null si se canceló).</summary>
    public string? SelectedGalleryId { get; private set; }

    public GalleryDialog(string folderName, List<GalleryInfo> galleries)
    {
        Text            = "FotoShow — Seleccionar galería";
        Width           = 400;
        Height          = 280;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterScreen;
        MaximizeBox     = false;
        MinimizeBox     = false;
        BackColor       = BgPrimary;
        ForeColor       = TextPrimary;
        Font            = new Font("Segoe UI", 9);

        // ── carpeta seleccionada ────────────────────────────────────────────────
        _lblFolder = new Label
        {
            Text      = $"Carpeta: {folderName}",
            ForeColor = GreenPrimary,
            Font      = new Font("Segoe UI", 9, FontStyle.Bold),
            Location  = new Point(16, 16),
            AutoSize  = false,
            Size      = new Size(360, 20),
        };

        var lblGallery = new Label
        {
            Text      = "¿A qué galería subir las fotos?",
            ForeColor = TextPrimary,
            Location  = new Point(16, 48),
            AutoSize  = true,
        };

        // ── combo de galerías existentes ───────────────────────────────────────
        _cmbGallery = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location      = new Point(16, 70),
            Size          = new Size(360, 24),
            BackColor     = BgCard,
            ForeColor     = TextPrimary,
            FlatStyle     = FlatStyle.Flat,
        };
        _cmbGallery.Items.Add(new GalleryItem("", "— Crear nueva galería —"));
        foreach (var g in galleries)
            _cmbGallery.Items.Add(new GalleryItem(g.Id, g.Name));
        _cmbGallery.SelectedIndex = galleries.Count > 0 ? 1 : 0;
        _cmbGallery.SelectedIndexChanged += (_, _) => UpdateNewNameVisibility();

        // ── campo para nueva galería ───────────────────────────────────────────
        _lblNew = new Label
        {
            Text      = "Nombre de la nueva galería:",
            ForeColor = TextMuted,
            Location  = new Point(16, 108),
            AutoSize  = true,
        };

        _txtNewName = new TextBox
        {
            Text        = folderName,
            Location    = new Point(16, 128),
            Size        = new Size(360, 24),
            BackColor   = BgCard,
            ForeColor   = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
        };

        // ── botones ────────────────────────────────────────────────────────────
        _btnOk = new Button
        {
            Text      = "Agregar fotos",
            Location  = new Point(200, 200),
            Size      = new Size(120, 32),
            BackColor = GreenPrimary,
            ForeColor = Color.Black,
            FlatStyle = FlatStyle.Flat,
            Font      = new Font("Segoe UI", 9, FontStyle.Bold),
        };
        _btnOk.FlatAppearance.BorderSize = 0;
        _btnOk.Click += OnOkClick;

        _btnCancel = new Button
        {
            Text      = "Cancelar",
            Location  = new Point(330, 200),
            Size      = new Size(54, 32),
            BackColor = BgCard,
            ForeColor = TextMuted,
            FlatStyle = FlatStyle.Flat,
        };
        _btnCancel.FlatAppearance.BorderColor = BorderColor;
        _btnCancel.Click += (_, _) => { SelectedGalleryId = null; DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange([_lblFolder, lblGallery, _cmbGallery, _lblNew, _txtNewName, _btnOk, _btnCancel]);
        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        UpdateNewNameVisibility();
    }

    private void UpdateNewNameVisibility()
    {
        bool isNew = _cmbGallery.SelectedIndex == 0;
        _lblNew.Visible     = isNew;
        _txtNewName.Visible = isNew;
        _btnOk.Text = isNew ? "Crear y agregar fotos" : "Agregar fotos";
    }

    private void OnOkClick(object? sender, EventArgs e)
    {
        if (_cmbGallery.SelectedIndex == 0)
        {
            // Crear nueva galería — el ID se resuelve async en TrayApp
            SelectedGalleryId = $"new:{_txtNewName.Text.Trim()}";
        }
        else if (_cmbGallery.SelectedItem is GalleryItem item)
        {
            SelectedGalleryId = item.Id;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    // ── helper ─────────────────────────────────────────────────────────────────

    private record GalleryItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}
