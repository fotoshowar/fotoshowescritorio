using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Fotoshow.Services;
using Fotoshow.Data;
using Fotoshow.Models;
using Microsoft.EntityFrameworkCore;
using System.IO;
using System.Collections.ObjectModel;
using System.Threading;

namespace Fotoshow;

public partial class MainWindow : Window
{
    private readonly PhotoService    _photoService;
    private readonly FotoshowContext _context;
    private readonly AppSettings     _settings;
    private readonly AiService       _aiService;

    private int    _currentPage    = 0;
    private bool   _isLoading      = false;
    private string _currentFolder  = "";
    private int?   _filterPersonId = null;
    private bool   _logVisible     = false;
    private CancellationTokenSource? _aiCts;
    private readonly ObservableCollection<string> _logLines = new();

    public MainWindow()
    {
        InitializeComponent();

        _settings = new AppSettings();
        _currentFolder = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, _settings.WatchDirectory);
        Directory.CreateDirectory(_currentFolder);

        var optionsBuilder = new DbContextOptionsBuilder<FotoshowContext>();
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "database.db");
        optionsBuilder.UseSqlite($"Data Source={dbPath}");
        _context      = new FotoshowContext(optionsBuilder.Options);
        _photoService = new PhotoService(_context, _settings);
        _aiService    = new AiService();

        _aiService.OnLog      += msg  => Dispatcher.Invoke(() => AppendLog(msg));
        _aiService.OnProgress += (done, total) => Dispatcher.Invoke(() =>
        {
            UpdateStatus($"🧠 IA: {done}/{total}...");
            AiProgressBar.Value        = total > 0 ? (double)done / total * 100 : 0;
            AiProgressPanel.Visibility = Visibility.Visible;
        });
        _aiService.OnReady += ready => Dispatcher.Invoke(() =>
        {
            AiStatusDot.Fill  = ready
                ? new SolidColorBrush(Color.FromRgb(52, 211, 153))
                : new SolidColorBrush(Color.FromRgb(239, 68, 68));
            AiStatusText.Text = ready ? "IA lista" : "IA no disponible";
        });

        LogList.ItemsSource = _logLines;
        Loaded              += MainWindow_Loaded;
        PhotoGallery.Loaded += PhotoGallery_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            AppendLog("🚀 Iniciando Fotoshow...");
            await _context.InitializeDatabaseAsync();
            AppendLog("✅ Base de datos lista");
            LoadFolderTree();
            await LoadPhotosAsync(0);
            UpdateStatus("Listo");
            AppendLog($"📂 Carpeta: {_currentFolder}");

            _ = Task.Run(async () =>
            {
                Dispatcher.Invoke(() => AppendLog("🔧 Iniciando motor de IA..."));
                await _aiService.StartAsync();
                var pending = await _context.Photos.CountAsync(p => p.Status == "pending");
                if (pending > 0)
                {
                    Dispatcher.Invoke(() => AppendLog($"📋 {pending} foto(s) pendientes de IA..."));
                    await RunAiAsync();
                }
            });
        }
        catch (Exception ex)
        {
            AppendLog($"❌ Error: {ex.Message}");
            MessageBox.Show($"Error al inicializar: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PhotoGallery_Loaded(object sender, RoutedEventArgs e)
    {
        var sv = FindVisualChild<ScrollViewer>(PhotoGallery);
        if (sv != null) sv.ScrollChanged += PhotoGallery_ScrollChanged;
    }

    private async Task LoadPhotosAsync(int page)
    {
        if (_isLoading) return;
        try
        {
            _isLoading = true;
            ShowLoading(true);

            List<Photo> photos;
            if (_filterPersonId.HasValue)
            {
                photos = await _context.Photos
                    .AsNoTracking()
                    .Where(p => p.PersonId == _filterPersonId.Value
                             && p.LocalPath.StartsWith(_currentFolder))
                    .OrderByDescending(p => p.UploadDate)
                    .Skip(page * _settings.PageSize)
                    .Take(_settings.PageSize)
                    .ToListAsync();
            }
            else
            {
                photos = await _photoService.GetPhotosAsync(
                    page, _settings.PageSize, _currentFolder);
            }

            if (page == 0) PhotoGallery.ItemsSource = photos;
            else
            {
                var current = (PhotoGallery.ItemsSource as List<Photo>) ?? new();
                current.AddRange(photos);
                PhotoGallery.ItemsSource = null;
                PhotoGallery.ItemsSource = current;
            }

            _currentPage = page;
            var total = _filterPersonId.HasValue
                ? await _context.Photos.CountAsync(p => p.PersonId == _filterPersonId.Value)
                : await _photoService.GetPhotoCountAsync(_currentFolder);

            PhotoCountText.Text = _filterPersonId.HasValue
                ? $"{total} foto(s) — Persona #{_filterPersonId}"
                : $"{total} foto(s)";

            UpdateStatus($"Mostrando {Math.Min((page+1)*_settings.PageSize, total)} de {total}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error cargando fotos: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _isLoading = false; ShowLoading(false); }
    }

    private async void PhotoGallery_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isLoading) return;
        var sv = e.OriginalSource as ScrollViewer;
        if (sv == null || sv.ScrollableHeight == 0) return;
        if (sv.VerticalOffset / sv.ScrollableHeight > 0.75)
            await LoadPhotosAsync(_currentPage + 1);
    }

    private void LoadFolderTree()
    {
        FolderTree.Items.Clear();
        var root = new TreeViewItem
        {
            Header = "📁  fotoshow", Tag = _currentFolder, IsExpanded = true
        };
        LoadSubfolders(root, _currentFolder);
        FolderTree.Items.Add(root);
        root.Selected += FolderTreeItem_Selected;
    }

    private void LoadSubfolders(TreeViewItem parent, string path)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                var di   = new DirectoryInfo(dir);
                var item = new TreeViewItem
                {
                    Header = $"📂  {di.Name}", Tag = dir
                };
                item.Selected += FolderTreeItem_Selected;
                parent.Items.Add(item);
                LoadSubfolders(item, dir);
            }
        }
        catch { }
    }

    private async void FolderTreeItem_Selected(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem item && item.Tag is string path)
        {
            _currentFolder  = path;
            _filterPersonId = null;
            _currentPage    = 0;
            ClearPersonFilter();
            await LoadPhotosAsync(0);
        }
    }

    private async void UploadPhotos_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Imágenes|*.jpg;*.jpeg;*.png;*.webp;*.bmp",
            Multiselect = true, Title = "Seleccionar fotos"
        };
        if (dlg.ShowDialog() == true)
            await ProcessFilesAsync(dlg.FileNames.ToList());
    }

    private async Task ProcessFilesAsync(List<string> files)
    {
        try
        {
            ShowLoading(true);
            AppendLog($"📥 Importando {files.Count} foto(s)...");

            var progress = new Progress<(int processed, int total)>(p =>
                Dispatcher.Invoke(() => UpdateStatus($"Copiando {p.processed}/{p.total}...")));

            var processed = await _photoService.ProcessFilesAsync(files, progress);
            await LoadPhotosAsync(0);

            AppendLog($"✅ {processed} foto(s) copiadas");
            UpdateStatus($"✅ {processed} foto(s) procesadas");
            await RunAiAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ShowLoading(false); }
    }

    private async Task RunAiAsync()
    {
        _aiCts?.Cancel();
        _aiCts = new CancellationTokenSource();
        var token = _aiCts.Token;
        try
        {
            AiProgressPanel.Visibility = Visibility.Visible;
            AiProgressBar.Value        = 0;
            await _aiService.ProcessPendingAsync(_context, token);
            if (!token.IsCancellationRequested)
            {
                await LoadPhotosAsync(0);
                await LoadPersonListAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppendLog($"❌ IA: {ex.Message}"); }
        finally
        {
            Dispatcher.Invoke(() =>
            {
                AiProgressBar.Value        = 100;
                AiProgressPanel.Visibility = Visibility.Collapsed;
            });
        }
    }

    private async Task LoadPersonListAsync()
    {
        try
        {
            var groups = await _context.Photos
                .Where(p => p.PersonId > 0)
                .GroupBy(p => p.PersonId)
                .Select(g => new { PersonId = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .ToListAsync();

            PersonList.Items.Clear();

            var allItem = new ListBoxItem
            {
                Content    = "👥  Todas las personas",
                Tag        = (int?)null,
                Foreground = new SolidColorBrush(Colors.White)
            };
            allItem.Selected += PersonItem_Selected;
            PersonList.Items.Add(allItem);

            foreach (var g in groups)
            {
                var item = new ListBoxItem
                {
                    Content    = $"🧑  Persona #{g.PersonId}  ({g.Count})",
                    Tag        = (int?)g.PersonId,
                    Foreground = new SolidColorBrush(Colors.White)
                };
                item.Selected += PersonItem_Selected;
                PersonList.Items.Add(item);
            }

            PersonPanel.Visibility = groups.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { AppendLog($"⚠️  Personas: {ex.Message}"); }
    }

    private async void PersonItem_Selected(object sender, RoutedEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            _filterPersonId = item.Tag as int?;
            _currentPage    = 0;
            if (_filterPersonId.HasValue)
            {
                PersonFilterBadge.Text       = $"Persona #{_filterPersonId}";
                PersonFilterPanel.Visibility = Visibility.Visible;
            }
            else ClearPersonFilter();
            await LoadPhotosAsync(0);
        }
    }

    private void ClearPersonFilter()
    {
        _filterPersonId              = null;
        PersonFilterPanel.Visibility = Visibility.Collapsed;
    }

    private async void ClearPersonFilter_Click(object sender, RoutedEventArgs e)
    {
        ClearPersonFilter(); await LoadPhotosAsync(0);
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Imágenes|*.jpg;*.jpeg;*.png;*.webp",
            Title  = "Foto de referencia para buscar cara"
        };
        if (dlg.ShowDialog() != true) return;

        ShowLoading(true);
        AppendLog($"🔍 Buscando: {Path.GetFileName(dlg.FileName)}");
        try
        {
            var results = await _aiService.SearchByFaceAsync(dlg.FileName, _context);
            if (results.Count == 0)
            {
                MessageBox.Show(
                    "No se encontraron fotos con esa persona.\n\n" +
                    "• Verificá que la foto tenga una cara visible\n" +
                    "• Las fotos deben estar procesadas por la IA",
                    "Sin resultados", MessageBoxButton.OK, MessageBoxImage.Information);
                AppendLog("🔍 Sin resultados");
                return;
            }

            AppendLog($"🔍 {results.Count} foto(s) encontradas");
            PhotoGallery.ItemsSource     = results.Select(r => r.Photo).ToList();
            PhotoCountText.Text          = $"{results.Count} resultado(s)";
            PersonFilterBadge.Text       = $"Búsqueda por cara ({results.Count})";
            PersonFilterPanel.Visibility = Visibility.Visible;
            UpdateStatus($"🔍 {results.Count} foto(s) encontradas");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error búsqueda: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ShowLoading(false); }
    }

    private void AppendLog(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {msg}";
        _logLines.Add(line);
        while (_logLines.Count > 500) _logLines.RemoveAt(0);
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
        
        // Auto-abrir el log si hay errores o es el primer mensaje
        if ((msg.Contains("❌") || msg.Contains("⚠️") || _logLines.Count == 1) && !_logVisible)
        {
            ToggleLog_Click(null!, null!);
        }
    }

    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        _logVisible = !_logVisible;
        var anim = new DoubleAnimation(_logVisible ? 160.0 : 0.0,
            TimeSpan.FromMilliseconds(200));
        LogPanel.BeginAnimation(HeightProperty, anim);
        if (sender is Button btn)
            btn.Content = _logVisible ? "▼ LOG" : "▲ LOG";
    }

    private void PhotoGallery_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PhotoGallery.SelectedItem is Photo photo) ViewPhoto(photo);
    }
    private void PrintPhoto_Click(object sender, RoutedEventArgs e)
    {
        if (PhotoGallery.SelectedItem is Photo photo) PrintPhoto(photo);
    }
    private void ViewPhoto_Click(object sender, RoutedEventArgs e)
    {
        if (PhotoGallery.SelectedItem is Photo photo) ViewPhoto(photo);
    }
    private void DeletePhoto_Click(object sender, RoutedEventArgs e)
    {
        if (PhotoGallery.SelectedItem is Photo photo)
        {
            var r = MessageBox.Show($"¿Eliminar {photo.Filename}?\nNo se puede deshacer.",
                "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes) _ = DeletePhotoAsync(photo);
        }
    }

    private void PrintPhoto(Photo photo)
    {
        if (!File.Exists(photo.LocalPath))
        {
            MessageBox.Show($"No encontrado:\n{photo.LocalPath}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        new PrintDialog(photo.LocalPath) { Owner = this }.ShowDialog();
    }

    private void ViewPhoto(Photo photo)
    {
        if (!File.Exists(photo.LocalPath))
        {
            MessageBox.Show($"No encontrado:\n{photo.LocalPath}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = photo.LocalPath, UseShellExecute = true
        });
    }

    private async Task DeletePhotoAsync(Photo photo)
    {
        ShowLoading(true);
        try
        {
            if (await _photoService.DeletePhotoAsync(photo.Id))
            {
                await LoadPhotosAsync(0);
                AppendLog($"🗑  {photo.Filename}");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ShowLoading(false); }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var r = MessageBox.Show($"📁 {baseDir}\n\n¿Abrir carpeta?",
            "Ubicación", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (r == MessageBoxResult.Yes && Directory.Exists(baseDir))
            System.Diagnostics.Process.Start("explorer.exe", baseDir);
    }

    private void CloudSync_Click(object sender, RoutedEventArgs e)
        => MessageBox.Show("Función de nube en desarrollo", "Info");

    private void CreateFolder_Click(object sender, RoutedEventArgs e) { }
    private void Gallery_Click(object sender, RoutedEventArgs e) { }
    private void ViewMode_Changed(object sender, SelectionChangedEventArgs e) { }
    private void ThumbnailSize_Changed(object sender, SelectionChangedEventArgs e) { }

    private void ShowLoading(bool show)
    {
        LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            var a = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1))
            { RepeatBehavior = RepeatBehavior.Forever };
            LoadingRotation.BeginAnimation(RotateTransform.AngleProperty, a);
        }
        else LoadingRotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void UpdateStatus(string msg)
    {
        var folder = Path.GetFileName(_currentFolder);
        if (string.IsNullOrEmpty(folder)) folder = _currentFolder;
        StatusText.Text = $"{DateTime.Now:HH:mm:ss}  {msg}  ·  📁 {folder}";
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var r = FindVisualChild<T>(child);
            if (r != null) return r;
        }
        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _aiCts?.Cancel();
        _aiService.Dispose();
        _context?.Dispose();
        base.OnClosed(e);
    }
}
