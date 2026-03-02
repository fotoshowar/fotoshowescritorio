using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fotoshow.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace Fotoshow;

public partial class PhotoViewerWindow : Window
{
    private readonly List<Photo> _photos;
    private int _currentIndex;
    private DispatcherTimer? _slideshowTimer;
    private bool _isPlaying = false;
    private DispatcherTimer? _controlsTimer;

    // Zoom & pan
    private double _zoomLevel = 1.0;
    private const double ZoomMin = 1.0;
    private const double ZoomMax = 8.0;
    private const double ZoomStep = 0.15;
    private bool _isDragging = false;
    private System.Windows.Point _dragStart;

    public PhotoViewerWindow(List<Photo> photos, int startIndex = 0)
    {
        InitializeComponent();
        _photos = photos ?? new List<Photo>();
        _currentIndex = startIndex;

        // Timer para ocultar controles
        _controlsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _controlsTimer.Tick += (s, e) => HideControls();

        // Evento mouse move para mostrar controles
        MouseMove += (s, e) => ShowControls();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ShowPhoto(_currentIndex);
        ShowControls();
    }

    private void ShowPhoto(int index)
    {
        if (_photos.Count == 0) return;

        // Asegurar índice válido
        if (index < 0) index = _photos.Count - 1;
        if (index >= _photos.Count) index = 0;

        _currentIndex = index;
        ResetZoom();
        var photo = _photos[_currentIndex];

        if (!File.Exists(photo.LocalPath))
        {
            PhotoNameText.Text = "Archivo no encontrado";
            PhotoIndexText.Text = "";
            MainImage.Source = null;
            return;
        }

        try
        {
            MainImage.Source = LoadOrientedImage(photo.LocalPath);

            PhotoNameText.Text = photo.OriginalName;
            PhotoIndexText.Text = $"{_currentIndex + 1} de {_photos.Count}";
        }
        catch (Exception ex)
        {
            PhotoNameText.Text = $"Error: {ex.Message}";
            MainImage.Source = null;
        }
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        ShowPhoto(_currentIndex - 1);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        ShowPhoto(_currentIndex + 1);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            StopSlideshow();
        }
        else
        {
            StartSlideshow();
        }
    }

    private void StartSlideshow()
    {
        _isPlaying = true;
        PlayPauseIcon.Text = "\uE769"; // Pause icon

        _slideshowTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _slideshowTimer.Tick += (s, e) =>
        {
            ShowPhoto(_currentIndex + 1);
        };
        _slideshowTimer.Start();

        HideControls();
    }

    private void StopSlideshow()
    {
        _isPlaying = false;
        PlayPauseIcon.Text = "\uE768"; // Play icon

        _slideshowTimer?.Stop();
        _slideshowTimer = null;

        ShowControls();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
            case Key.Q:
                Close();
                break;

            case Key.Left:
            case Key.Up:
                ShowPhoto(_currentIndex - 1);
                break;

            case Key.Right:
            case Key.Down:
            case Key.Space:
                ShowPhoto(_currentIndex + 1);
                break;

            case Key.Home:
                ShowPhoto(0);
                break;

            case Key.End:
                ShowPhoto(_photos.Count - 1);
                break;

            case Key.P:
            case Key.Enter:
                PlayPause_Click(sender, new RoutedEventArgs());
                break;

            case Key.D0:
            case Key.NumPad0:
                ResetZoom();
                break;

            case Key.OemPlus:
            case Key.Add:
                _zoomLevel = Math.Min(ZoomMax, _zoomLevel + ZoomStep * 2);
                ImageScale.ScaleX = _zoomLevel;
                ImageScale.ScaleY = _zoomLevel;
                break;

            case Key.OemMinus:
            case Key.Subtract:
                _zoomLevel = Math.Max(ZoomMin, _zoomLevel - ZoomStep * 2);
                ImageScale.ScaleX = _zoomLevel;
                ImageScale.ScaleY = _zoomLevel;
                if (_zoomLevel <= ZoomMin) { ImageTranslate.X = 0; ImageTranslate.Y = 0; }
                break;
        }
    }

    private void ShowControls()
    {
        _controlsTimer?.Stop();

        var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(200));
        ControlsOverlay.BeginAnimation(OpacityProperty, fadeIn);

        Cursor = Cursors.Arrow;

        _controlsTimer?.Start();
    }

    private void HideControls()
    {
        _controlsTimer?.Stop();

        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        ControlsOverlay.BeginAnimation(OpacityProperty, fadeOut);

        Cursor = Cursors.None;
    }

    /// <summary>
    /// Carga una imagen aplicando rotación EXIF con ImageSharp (AutoOrient)
    /// y la convierte a BitmapSource para WPF. Redimensiona a max 1920px de ancho.
    /// </summary>
    private static BitmapSource LoadOrientedImage(string path)
    {
        using var image = SixLabors.ImageSharp.Image.Load(path);
        image.Mutate(x => x.AutoOrient());

        // Redimensionar si es muy grande para no gastar memoria
        if (image.Width > 1920)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(1920, 0),
                Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max
            }));
        }

        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = 92 });
        ms.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // ═══════════════════════════════════════════
    // ZOOM & PAN
    // ═══════════════════════════════════════════

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var oldZoom = _zoomLevel;

        if (e.Delta > 0)
            _zoomLevel = Math.Min(ZoomMax, _zoomLevel + ZoomStep);
        else
            _zoomLevel = Math.Max(ZoomMin, _zoomLevel - ZoomStep);

        if (Math.Abs(oldZoom - _zoomLevel) < 0.001) return;

        // Zoom hacia donde está el mouse: mantener el punto bajo el cursor fijo
        var mouseOnImage = e.GetPosition(MainImage);

        // Posición actual del mouse en coordenadas reales (antes del zoom)
        var absX = (mouseOnImage.X * oldZoom) + ImageTranslate.X;
        var absY = (mouseOnImage.Y * oldZoom) + ImageTranslate.Y;

        // Nueva posición del translate para que el mismo punto quede bajo el cursor
        var offsetX = absX - (mouseOnImage.X * _zoomLevel);
        var offsetY = absY - (mouseOnImage.Y * _zoomLevel);

        ImageScale.ScaleX = _zoomLevel;
        ImageScale.ScaleY = _zoomLevel;

        if (_zoomLevel <= ZoomMin)
        {
            ImageTranslate.X = 0;
            ImageTranslate.Y = 0;
        }
        else
        {
            ImageTranslate.X = offsetX;
            ImageTranslate.Y = offsetY;
        }

        e.Handled = true;
    }

    private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_zoomLevel <= ZoomMin) return;
        _isDragging = true;
        _dragStart = e.GetPosition(this);
        ((UIElement)sender).CaptureMouse();
    }

    private void Image_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            ((UIElement)sender).ReleaseMouseCapture();
        }
    }

    private void Image_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(this);
        ImageTranslate.X += pos.X - _dragStart.X;
        ImageTranslate.Y += pos.Y - _dragStart.Y;
        _dragStart = pos;
    }

    private void ResetZoom()
    {
        _zoomLevel = 1.0;
        ImageScale.ScaleX = 1;
        ImageScale.ScaleY = 1;
        ImageTranslate.X = 0;
        ImageTranslate.Y = 0;
    }

    protected override void OnClosed(EventArgs e)
    {
        _slideshowTimer?.Stop();
        _controlsTimer?.Stop();
        base.OnClosed(e);
    }
}
