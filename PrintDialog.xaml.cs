using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using Fotoshow.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Fotoshow;

public partial class PrintDialog : Window
{
    private readonly string _photoPath;
    private readonly string _marcosDirectory;
    private string? _selectedFramePath;
    private bool _isUpdating = false;
    private Image<Rgba32>? _currentComposed;
    // Token para cancelar previews en curso cuando llega uno nuevo
    private CancellationTokenSource? _previewCts;
    // Debounce: evita disparar un preview por cada tick del slider
    private System.Windows.Threading.DispatcherTimer? _debounceTimer;

    public PrintDialog(string photoPath)
    {
        _isUpdating = true; // Bloquear eventos hasta terminar init
        
        try
        {
            InitializeComponent();
            
            _photoPath = photoPath;
            _marcosDirectory = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, 
                "marcos"
            );
            Directory.CreateDirectory(_marcosDirectory);

            // Validar que la foto existe
            if (!File.Exists(_photoPath))
            {
                MessageBox.Show($"Archivo no encontrado:\n{_photoPath}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            // Detectar orientación
            var isLandscape = PrintService.IsLandscape(photoPath);
            if (OrientationText != null)
            {
                OrientationText.Text = isLandscape ? "Landscape" : "Portrait";
            }

            // Cargar impresoras
            LoadPrinters();

            // Cargar marcos
            LoadFrames(isLandscape);

            _isUpdating = false; // Ahora sí, permitir eventos

            // Generar preview inicial (inmediato, sin debounce)
            _ = UpdatePreviewAsync();
        }
        catch (Exception ex)
        {
            _isUpdating = false;
            MessageBox.Show($"Error al inicializar diálogo de impresión:\n{ex.Message}\n\nStackTrace:\n{ex.StackTrace}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void LoadPrinters()
    {
        try
        {
            var printers = PrintService.GetAvailablePrinters();
            foreach (var printer in printers)
            {
                PrinterCombo.Items.Add(printer);
            }

            if (PrinterCombo.Items.Count > 0)
            {
                PrinterCombo.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al cargar impresoras: {ex.Message}", "Error", 
                MessageBoxButton.OK, MessageBoxImage.Warning);
            PrinterCombo.Items.Add("(Predeterminada)");
            PrinterCombo.SelectedIndex = 0;
        }
    }

    private void LoadFrames(bool isLandscape)
    {
        FramesPanel.Children.Clear();

        // Botón "Sin marco"
        AddFrameButton(null, "(Sin marco)");

        // Cargar marcos de la carpeta
        var frames = PrintService.GetFramesForOrientation(isLandscape, _marcosDirectory);
        
        foreach (var framePath in frames)
        {
            var frameName = Path.GetFileNameWithoutExtension(framePath);
            AddFrameButton(framePath, frameName);
        }
    }

    private void AddFrameButton(string? framePath, string label)
    {
        var button = new Border
        {
            Width = 90,
            Height = 100,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(34, 34, 34)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(51, 51, 51)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(4),
            Tag = framePath
        };

        var stackPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };

        // Preview del marco o texto
        if (framePath != null && File.Exists(framePath))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(framePath);
                bitmap.DecodePixelWidth = 72;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();

                var image = new System.Windows.Controls.Image
                {
                    Source = bitmap,
                    Width = 72,
                    Height = 72,
                    Stretch = System.Windows.Media.Stretch.Uniform
                };
                stackPanel.Children.Add(image);
            }
            catch
            {
                var placeholder = new TextBlock
                {
                    Text = "🖼",
                    FontSize = 32,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = System.Windows.Media.Brushes.Gray
                };
                stackPanel.Children.Add(placeholder);
            }
        }
        else
        {
            var noFrameText = new TextBlock
            {
                Text = "✕\nSin\nmarco",
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Foreground = System.Windows.Media.Brushes.Gray
            };
            stackPanel.Children.Add(noFrameText);
        }

        // Label
        var nameLabel = new TextBlock
        {
            Text = label.Length > 12 ? label.Substring(0, 12) : label,
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 0)
        };
        stackPanel.Children.Add(nameLabel);

        button.Child = stackPanel;

        // Click handler
        button.MouseLeftButtonDown += (s, e) =>
        {
            SelectFrame(framePath, button);
        };

        FramesPanel.Children.Add(button);

        // Seleccionar el primero por defecto
        if (FramesPanel.Children.Count == 1)
        {
            SelectFrame(framePath, button);
        }
    }

    private void SelectFrame(string? framePath, Border button)
    {
        _selectedFramePath = framePath;

        // Actualizar estilos
        foreach (var child in FramesPanel.Children)
        {
            if (child is Border border)
            {
                border.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    border == button 
                        ? System.Windows.Media.Color.FromRgb(255, 255, 255)
                        : System.Windows.Media.Color.FromRgb(51, 51, 51));
                border.BorderThickness = new Thickness(border == button ? 2 : 2);
            }
        }

        UpdatePreviewDebounced();
    }

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdating || ScaleValue == null) return;
        ScaleValue.Text = $"{(int)ScaleSlider.Value}%";
        UpdatePreviewDebounced();
    }

    private void MarginCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdating || MarginCombo?.SelectedItem == null) return;
        UpdatePreviewDebounced();
    }

    private void Adjustment_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdating) return;
        if (BrightnessValue == null || ContrastValue == null || GammaValue == null ||
            SaturationValue == null || SharpnessValue == null || TemperatureValue == null) return;

        // Actualizar labels
        if (sender == BrightnessSlider) BrightnessValue.Text = ((int)BrightnessSlider.Value).ToString();
        else if (sender == ContrastSlider) ContrastValue.Text = ((int)ContrastSlider.Value).ToString();
        else if (sender == GammaSlider) GammaValue.Text = (GammaSlider.Value / 100.0).ToString("F1");
        else if (sender == SaturationSlider) SaturationValue.Text = ((int)SaturationSlider.Value).ToString();
        else if (sender == SharpnessSlider) SharpnessValue.Text = ((int)SharpnessSlider.Value).ToString();
        else if (sender == TemperatureSlider) TemperatureValue.Text = ((int)TemperatureSlider.Value).ToString();

        UpdatePreviewDebounced();
    }

    private void ResetAdjustments_Click(object sender, RoutedEventArgs e)
    {
        _isUpdating = true;
        
        BrightnessSlider.Value = 0;
        ContrastSlider.Value = 0;
        GammaSlider.Value = 100;
        SaturationSlider.Value = 0;
        SharpnessSlider.Value = 0;
        TemperatureSlider.Value = 0;

        _isUpdating = false;
        UpdatePreviewDebounced();
    }

    /// <summary>
    /// Programa la actualización del preview con debounce (300ms).
    /// Cancela cualquier preview en curso antes de iniciar uno nuevo.
    /// Así no se acumulan tasks aunque el usuario mueva sliders rápido.
    /// </summary>
    private void UpdatePreviewDebounced()
    {
        if (_isUpdating) return;

        // Reiniciar timer de debounce
        if (_debounceTimer == null)
        {
            _debounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _debounceTimer.Tick += async (s, e) =>
            {
                _debounceTimer.Stop();
                await UpdatePreviewAsync();
            };
        }

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private async Task UpdatePreviewAsync()
    {
        if (_isUpdating) return;

        // Cancelar preview anterior si todavía está corriendo
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        LoadingOverlay.Visibility = Visibility.Visible;
        PreviewStatus.Text = "Generando preview...";

        try
        {
            // Leer opciones en el UI thread ANTES de ir al background
            var options = GetPrintOptions();

            Image<Rgba32>? composed = null;

            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                composed = PrintService.ComposeForPrint(options);
                token.ThrowIfCancellationRequested();
            }, token);

            // Si fue cancelado mientras procesaba, descartar resultado
            if (token.IsCancellationRequested)
            {
                composed?.Dispose();
                return;
            }

            // Convertir a BitmapSource en background (no bloquea UI)
            var bitmapSource = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return ConvertToBitmapSource(composed!);
            }, token);

            if (token.IsCancellationRequested)
            {
                composed?.Dispose();
                return;
            }

            // Actualizar UI — reemplazar imagen anterior
            _currentComposed?.Dispose();
            _currentComposed = composed;

            PreviewImage.Source = bitmapSource;
            PreviewStatus.Text = "Preview listo";
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // Normal — fue cancelado por un preview más nuevo, ignorar
            PreviewStatus.Text = "...";
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                PreviewStatus.Text = $"Error: {ex.Message}";
                LoadingOverlay.Visibility = Visibility.Collapsed;
                // NO mostrar MessageBox por cada error de preview rápido
                // Solo loguear en la barra de estado
            }
        }
    }

    private PrintService.PrintOptions GetPrintOptions()
    {
        // Obtener valores de UI (debe ejecutarse en el thread de UI)
        var marginItem = MarginCombo.SelectedItem as ComboBoxItem;
        var marginPixels = marginItem != null ? int.Parse(marginItem.Tag?.ToString() ?? "95") : 95;

        return new PrintService.PrintOptions
        {
            PhotoPath = _photoPath,
            FramePath = _selectedFramePath,
            Scale = (float)(ScaleSlider.Value / 100.0),
            MarginPixels = marginPixels,
            Adjustments = new ImageAdjustments.AdjustmentParams
            {
                Brightness = (float)BrightnessSlider.Value,
                Contrast = (float)ContrastSlider.Value,
                Gamma = (float)(GammaSlider.Value / 100.0),
                Saturation = (float)SaturationSlider.Value,
                Sharpness = (float)SharpnessSlider.Value,
                Temperature = (float)TemperatureSlider.Value
            },
            OffsetX = 0,
            OffsetY = 0,
            IsLandscape = PrintService.IsLandscape(_photoPath)  // respeta EXIF
        };
    }

    private BitmapSource ConvertToBitmapSource(Image<Rgba32> image)
    {
        using var memoryStream = new MemoryStream();
        image.SaveAsBmp(memoryStream);
        memoryStream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = memoryStream;
        bitmap.EndInit();
        bitmap.Freeze();

        return bitmap;
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_currentComposed == null)
        {
            MessageBox.Show("Por favor espera a que se genere el preview", "Espera",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PrintButton.IsEnabled = false;
        PrintButton.Content = "Enviando...";
        LoadingOverlay.Visibility = Visibility.Visible;

        try
        {
            // Guardar imagen temporal
            var tempPath = Path.Combine(Path.GetTempPath(), $"fotoshow_print_{Guid.NewGuid()}.jpg");
            PrintService.SaveForPrint(_currentComposed, tempPath);

            // Obtener impresora seleccionada
            var printerName = PrinterCombo.SelectedItem?.ToString();

            // Enviar a imprimir
            var success = await Task.Run(() => PrintService.PrintWithWindows(tempPath, printerName));

            if (success)
            {
                MessageBox.Show("✅ Trabajo enviado a la impresora", "Listo",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show("❌ No se pudo enviar a la impresora.\nVerifica que esté conectada y encendida.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al imprimir: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            PrintButton.IsEnabled = true;
            PrintButton.Content = "🖨  IMPRIMIR";
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _debounceTimer?.Stop();
        _currentComposed?.Dispose();
        base.OnClosed(e);
    }
}
