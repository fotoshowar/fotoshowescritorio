namespace FotoshowTray;

/// <summary>
/// Diálogo para seleccionar una galería existente o crear una nueva con todos los datos.
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

    private readonly ComboBox         _cmbGallery;
    private readonly Panel            _panelNew;
    private readonly TextBox          _txtName;
    private readonly TextBox          _txtLocation;
    private readonly DateTimePicker   _dtpEventDate;
    private readonly CheckBox         _chkDate;
    private readonly NumericUpDown    _numPrice;
    private readonly Button           _btnOk;
    private readonly Button           _btnCancel;

    /// <summary>
    /// Si se seleccionó una galería existente: su ID (string).
    /// Si se creó una nueva: null aquí — usar NewGalleryData.
    /// </summary>
    public string? SelectedGalleryId { get; private set; }

    /// <summary>Datos para crear galería nueva. Null si se eligió una existente.</summary>
    public NewGalleryData? NewGalleryData { get; private set; }

    public GalleryDialog(string folderName, List<GalleryInfo> galleries)
    {
        Text            = "FotoShow — Galería";
        Width           = 420;
        Height          = 420;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterScreen;
        MaximizeBox     = false;
        MinimizeBox     = false;
        BackColor       = BgPrimary;
        ForeColor       = TextPrimary;
        Font            = new Font("Segoe UI", 9);

        int y = 16;

        // ── carpeta ────────────────────────────────────────────────────────────
        var lblFolder = new Label
        {
            Text      = $"Carpeta: {folderName}",
            ForeColor = GreenPrimary,
            Font      = new Font("Segoe UI", 9, FontStyle.Bold),
            Location  = new Point(16, y),
            AutoSize  = false,
            Size      = new Size(380, 20),
        };

        y += 32;
        var lblGallery = new Label
        {
            Text      = "¿A qué galería subir las fotos?",
            ForeColor = TextPrimary,
            Location  = new Point(16, y),
            AutoSize  = true,
        };

        y += 22;
        _cmbGallery = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location      = new Point(16, y),
            Size          = new Size(380, 24),
            BackColor     = BgCard,
            ForeColor     = TextPrimary,
            FlatStyle     = FlatStyle.Flat,
        };
        _cmbGallery.Items.Add(new GalleryItem("", "— Crear nueva galería —"));
        foreach (var g in galleries)
            _cmbGallery.Items.Add(new GalleryItem(g.Id, g.Name));
        _cmbGallery.SelectedIndex = 0;
        _cmbGallery.SelectedIndexChanged += (_, _) => UpdatePanelVisibility();

        y += 32;

        // ── panel: campos para galería nueva ──────────────────────────────────
        _panelNew = new Panel
        {
            Location  = new Point(0, y),
            Size      = new Size(420, 240),
            BackColor = BgPrimary,
        };

        int py = 0;

        var lblName = MakeLabel("Nombre de la galería:", py); py += 20;
        _txtName = new TextBox
        {
            Text        = folderName,
            Location    = new Point(16, py),
            Size        = new Size(380, 24),
            BackColor   = BgCard,
            ForeColor   = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
        };
        py += 32;

        var lblLocation = MakeLabel("Lugar del evento (opcional):", py); py += 20;
        _txtLocation = new TextBox
        {
            Location    = new Point(16, py),
            Size        = new Size(380, 24),
            BackColor   = BgCard,
            ForeColor   = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
        };
        py += 32;

        _chkDate = new CheckBox
        {
            Text      = "Fecha del evento:",
            Location  = new Point(16, py),
            AutoSize  = true,
            ForeColor = TextMuted,
            BackColor = BgPrimary,
        };
        _chkDate.CheckedChanged += (_, _) => _dtpEventDate.Enabled = _chkDate.Checked;
        py += 24;

        _dtpEventDate = new DateTimePicker
        {
            Location   = new Point(16, py),
            Size       = new Size(200, 24),
            Format     = DateTimePickerFormat.Short,
            CalendarForeColor = TextPrimary,
            Enabled    = false,
        };
        py += 36;

        var lblPrice = MakeLabel("Precio por foto (ARS, 0 = gratis):", py); py += 20;
        _numPrice = new NumericUpDown
        {
            Location      = new Point(16, py),
            Size          = new Size(140, 24),
            Minimum       = 0,
            Maximum       = 999999,
            DecimalPlaces = 0,
            BackColor     = BgCard,
            ForeColor     = TextPrimary,
            BorderStyle   = BorderStyle.FixedSingle,
        };

        _panelNew.Controls.AddRange([
            lblName, _txtName,
            lblLocation, _txtLocation,
            _chkDate, _dtpEventDate,
            lblPrice, _numPrice,
        ]);

        // ── botones ────────────────────────────────────────────────────────────
        _btnOk = new Button
        {
            Text      = "Crear galería y agregar fotos",
            Location  = new Point(16, 360),
            Size      = new Size(220, 34),
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
            Location  = new Point(248, 360),
            Size      = new Size(80, 34),
            BackColor = BgCard,
            ForeColor = TextMuted,
            FlatStyle = FlatStyle.Flat,
        };
        _btnCancel.FlatAppearance.BorderColor = BorderColor;
        _btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange([lblFolder, lblGallery, _cmbGallery, _panelNew, _btnOk, _btnCancel]);
        AcceptButton = _btnOk;
        CancelButton = _btnCancel;

        UpdatePanelVisibility();
    }

    private Label MakeLabel(string text, int y) => new Label
    {
        Text      = text,
        ForeColor = TextMuted,
        Location  = new Point(16, y),
        AutoSize  = true,
    };

    private void UpdatePanelVisibility()
    {
        bool isNew = _cmbGallery.SelectedIndex == 0;
        _panelNew.Visible = isNew;
        _btnOk.Text = isNew ? "Crear galería y agregar fotos" : "Agregar fotos";
    }

    private void OnOkClick(object? sender, EventArgs e)
    {
        if (_cmbGallery.SelectedIndex == 0)
        {
            var name = _txtName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("El nombre de la galería es requerido.", "FotoShow",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            NewGalleryData = new NewGalleryData(
                Name:       name,
                Location:   _txtLocation.Text.Trim().NullIfEmpty(),
                EventDate:  _chkDate.Checked ? _dtpEventDate.Value.Date : (DateTime?)null,
                PricePerPhoto: (double)_numPrice.Value > 0 ? (double)_numPrice.Value : (double?)null
            );
            SelectedGalleryId = null;
        }
        else if (_cmbGallery.SelectedItem is GalleryItem item)
        {
            SelectedGalleryId = item.Id;
            NewGalleryData    = null;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private record GalleryItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}

/// <summary>Datos para crear una galería nueva con todos los campos.</summary>
public record NewGalleryData(
    string    Name,
    string?   Location,
    DateTime? EventDate,
    double?   PricePerPhoto
);

file static class StringEx
{
    public static string? NullIfEmpty(this string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}
