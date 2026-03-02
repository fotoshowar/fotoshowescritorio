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
    private readonly string          _dbPath;
    private readonly string          _rootFolder;

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

        _settings = AppSettings.Load();
        _rootFolder = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, _settings.WatchDirectory);
        _currentFolder = _rootFolder;
        Directory.CreateDirectory(_currentFolder);

        var optionsBuilder = new DbContextOptionsBuilder<FotoshowContext>();
        _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "database.db");
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        _context      = new FotoshowContext(optionsBuilder.Options);
        _photoService = new PhotoService(_context, _settings);
        _aiService    = new AiService();

        _aiService.OnLog      += msg  => Dispatcher.Invoke(() => AppendLog(msg));
        _aiService.OnProgress += (done, total) => Dispatcher.Invoke(() =>
        {
            UpdateStatus($"IA: {done}/{total}...");
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

    // ═══════════════════════════════════════════════════════════
    // WINDOW CHROME
    // ═══════════════════════════════════════════════════════════

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            MaximizeRestore();
        }
        else
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => MaximizeRestore();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    private void MaximizeRestore()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaximizeBtn.Content = "\uE739"; // Maximize icon
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaximizeBtn.Content = "\uE923"; // Restore icon
        }
    }

    // ═══════════════════════════════════════════════════════════
    // TAB SWITCHING
    // ═══════════════════════════════════════════════════════════

    private async void MainTab_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || MainTabControl == null || ExplorarCommandBar == null) return;

        var isExplorar = MainTabControl.SelectedIndex == 0;

        // Swap command bar visibility
        ExplorarCommandBar.Visibility = isExplorar ? Visibility.Visible : Visibility.Collapsed;

        // Load person cards when switching to Personas tab
        if (!isExplorar)
        {
            await LoadPersonCardsAsync();
        }
    }

    // ═══════════════════════════════════════════════════════════
    // INITIALIZATION
    // ═══════════════════════════════════════════════════════════

    private void CreateDailyFolders()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var dayFolder = Path.Combine(_rootFolder, today);
        foreach (var camera in _settings.Cameras)
        {
            Directory.CreateDirectory(Path.Combine(dayFolder, camera));
        }
    }

    private void CameraSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new CameraSettingsDialog(_settings.Cameras) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Changed)
        {
            _settings.Cameras = dialog.Cameras;
            _settings.Save();
            CreateDailyFolders();
            LoadFolderTree();
            AppendLog("Configuración de cámaras actualizada");
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            AppendLog("Iniciando Fotoshow...");
            await _context.InitializeDatabaseAsync();
            AppendLog("Base de datos lista");
            CreateDailyFolders();
            LoadFolderTree();
            await LoadPhotosAsync(0);
            UpdateStatus("Listo");
            AppendLog($"Carpeta: {_currentFolder}");

            _ = Task.Run(async () =>
            {
                Dispatcher.Invoke(() => AppendLog("Iniciando motor de IA..."));
                await _aiService.StartAsync();
                using var bgContext = CreateBackgroundContext();
                var pending = await bgContext.Photos.CountAsync(p => p.Status == "pending");
                if (pending > 0)
                {
                    Dispatcher.Invoke(() => AppendLog($"{pending} foto(s) pendientes de IA..."));
                    await Dispatcher.InvokeAsync(() => RunAiAsync());
                }
            });
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            MessageBox.Show($"Error al inicializar: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PhotoGallery_Loaded(object sender, RoutedEventArgs e)
    {
        var sv = FindVisualChild<ScrollViewer>(PhotoGallery);
        if (sv != null) sv.ScrollChanged += PhotoGallery_ScrollChanged;
    }

    // ═══════════════════════════════════════════════════════════
    // PHOTO LOADING
    // ═══════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════
    // FOLDER TREE (with image counts)
    // ═══════════════════════════════════════════════════════════

    private void LoadFolderTree()
    {
        FolderTree.Items.Clear();
        var imageCount = PhotoService.CountImagesInFolder(_rootFolder);
        var root = new TreeViewItem
        {
            Header = $"fotoshow ({imageCount})", Tag = _rootFolder, IsExpanded = true
        };

        var today = DateTime.Now.ToString("yyyy-MM-dd");

        try
        {
            var dirs = Directory.GetDirectories(_rootFolder)
                .Select(d => new DirectoryInfo(d))
                .OrderByDescending(d => d.Name)
                .ToList();

            foreach (var di in dirs)
            {
                var dirCount = PhotoService.CountImagesInFolder(di.FullName);
                var isToday = di.Name == today;
                var dateItem = new TreeViewItem
                {
                    Header = $"{di.Name} ({dirCount})",
                    Tag = di.FullName,
                    IsExpanded = isToday
                };

                LoadSubfolders(dateItem, di.FullName);
                root.Items.Add(dateItem);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error cargando carpetas: {ex.Message}");
        }

        FolderTree.Items.Add(root);
    }

    private void LoadSubfolders(TreeViewItem parent, string path)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                if (Path.GetFileName(dir).StartsWith(".")) continue; // skip .thumbnails
                var di = new DirectoryInfo(dir);
                var imageCount = PhotoService.CountImagesInFolder(dir);
                var item = new TreeViewItem
                {
                    Header = $"{di.Name} ({imageCount})", Tag = dir
                };
                parent.Items.Add(item);
                LoadSubfolders(item, dir);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error cargando subcarpetas de {path}: {ex.Message}"); }
    }

    private async void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem item && item.Tag is string path)
        {
            _currentFolder  = path;
            _filterPersonId = null;
            _currentPage    = 0;
            ClearPersonFilter();

            // Auto-discover photos in this folder
            try
            {
                var discovered = await _photoService.DiscoverPhotosInFolderAsync(path);
                if (discovered > 0)
                {
                    AppendLog($"Descubiertas {discovered} foto(s) nuevas en {Path.GetFileName(path)}");
                    LoadFolderTree();
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Error descubriendo fotos: {ex.Message}");
            }

            await LoadPhotosAsync(0);

            if (await HasPendingPhotosAsync())
                _ = RunAiAsync();
        }
    }

    private async Task<bool> HasPendingPhotosAsync()
    {
        return await _context.Photos.AnyAsync(p => p.Status == "pending");
    }

    // ═══════════════════════════════════════════════════════════
    // COMMAND BAR ACTIONS
    // ═══════════════════════════════════════════════════════════

    private async void ImportPhotos_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Imágenes|*.jpg;*.jpeg;*.png;*.webp;*.bmp",
            Multiselect = true, Title = "Seleccionar fotos para importar"
        };
        if (dlg.ShowDialog() == true)
            await ProcessFilesAsync(dlg.FileNames.ToList());
    }

    private void CreateFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputDialog("Nueva Carpeta", "Nombre de la carpeta:") { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResponseText))
        {
            var newPath = Path.Combine(_currentFolder, dialog.ResponseText.Trim());
            try
            {
                Directory.CreateDirectory(newPath);
                AppendLog($"Carpeta creada: {dialog.ResponseText.Trim()}");
                LoadFolderTree();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creando carpeta: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void ScanAll_Click(object sender, RoutedEventArgs e)
    {
        ShowLoading(true);
        AppendLog("Escaneando todas las carpetas...");
        try
        {
            var progress = new Progress<(int processed, int total)>(p =>
                Dispatcher.Invoke(() => UpdateStatus($"Escaneando {p.processed}/{p.total}...")));

            var discovered = await _photoService.DiscoverPhotosInFolderAsync(
                _rootFolder, recursive: true, progress: progress);

            AppendLog($"Escaneo completo: {discovered} foto(s) nuevas descubiertas");
            LoadFolderTree();
            await LoadPhotosAsync(0);

            if (discovered > 0)
                _ = RunAiAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error escaneando: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ShowLoading(false); }
    }

    private async void SearchByFace_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Imágenes|*.jpg;*.jpeg;*.png;*.webp",
            Title  = "Foto de referencia para buscar cara"
        };
        if (dlg.ShowDialog() != true) return;

        ShowLoading(true);
        AppendLog($"Buscando: {Path.GetFileName(dlg.FileName)}");
        try
        {
            var results = await _aiService.SearchByFaceAsync(dlg.FileName, _context);
            if (results.Count == 0)
            {
                MessageBox.Show(
                    "No se encontraron fotos con esa persona.\n\n" +
                    "Verificá que la foto tenga una cara visible\n" +
                    "Las fotos deben estar procesadas por la IA",
                    "Sin resultados", MessageBoxButton.OK, MessageBoxImage.Information);
                AppendLog("Sin resultados");
                return;
            }

            AppendLog($"{results.Count} foto(s) encontradas");

            // Show results in the Personas tab gallery
            PersonPhotoGallery.ItemsSource = results.Select(r => r.Photo).ToList();
            PersonPhotoCountText.Text = $"{results.Count} resultado(s) por búsqueda de cara";
            PersonFilterBadge.Text       = $"Búsqueda por cara ({results.Count})";
            PersonFilterPanel.Visibility = Visibility.Visible;
            UpdateStatus($"{results.Count} foto(s) encontradas");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error búsqueda: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ShowLoading(false); }
    }

    private async Task ProcessFilesAsync(List<string> files)
    {
        try
        {
            ShowLoading(true);
            AppendLog($"Importando {files.Count} foto(s) en {Path.GetFileName(_currentFolder)}...");

            var progress = new Progress<(int processed, int total)>(p =>
                Dispatcher.Invoke(() => UpdateStatus($"Copiando {p.processed}/{p.total}...")));

            var processed = await _photoService.ProcessFilesAsync(files, progress, _currentFolder);
            LoadFolderTree();
            await LoadPhotosAsync(0);

            AppendLog($"{processed} foto(s) importadas");
            UpdateStatus($"{processed} foto(s) procesadas");
            await RunAiAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ShowLoading(false); }
    }

    // ═══════════════════════════════════════════════════════════
    // PERSONAS TAB
    // ═══════════════════════════════════════════════════════════

    private async Task LoadPersonCardsAsync()
    {
        try
        {
            var groups = await _context.Photos
                .AsNoTracking()
                .Where(p => p.PersonId > 0)
                .GroupBy(p => p.PersonId)
                .Select(g => new
                {
                    PersonId = g.Key,
                    Count = g.Count(),
                    Thumbnail = g.OrderByDescending(p => p.UploadDate).Select(p => p.ThumbnailPath).FirstOrDefault()
                })
                .OrderByDescending(g => g.Count)
                .ToListAsync();

            var cards = groups.Select(g => new PersonCard
            {
                PersonId = g.PersonId,
                DisplayName = $"Persona #{g.PersonId}",
                PhotoCountText = $"({g.Count} fotos)",
                ThumbnailPath = g.Thumbnail ?? ""
            }).ToList();

            PersonCardsPanel.ItemsSource = cards;
            PersonSectionTitle.Text = cards.Count > 0
                ? $"Personas detectadas ({cards.Count})"
                : "No hay personas detectadas aún";
        }
        catch (Exception ex)
        {
            AppendLog($"Error cargando personas: {ex.Message}");
        }
    }

    private async void PersonCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PersonCard card)
        {
            _filterPersonId = card.PersonId;
            PersonFilterBadge.Text       = card.DisplayName;
            PersonFilterPanel.Visibility = Visibility.Visible;

            var photos = await _context.Photos
                .AsNoTracking()
                .Where(p => p.PersonId == card.PersonId)
                .OrderByDescending(p => p.UploadDate)
                .ToListAsync();

            PersonPhotoGallery.ItemsSource = photos;
            PersonPhotoCountText.Text = $"{photos.Count} foto(s) — {card.DisplayName}";
        }
    }

    private void PersonPhotoGallery_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PersonPhotoGallery.SelectedItem is Photo photo) ViewPhoto(photo, PersonPhotoGallery);
    }

    // ═══════════════════════════════════════════════════════════
    // AI PROCESSING
    // ═══════════════════════════════════════════════════════════

    private async Task RunAiAsync()
    {
        _aiCts?.Cancel();
        _aiCts = new CancellationTokenSource();
        var token = _aiCts.Token;
        try
        {
            AiProgressPanel.Visibility = Visibility.Visible;
            AiProgressBar.Value        = 0;
            using var aiContext = CreateBackgroundContext();
            await _aiService.ProcessPendingAsync(aiContext, token);
            if (!token.IsCancellationRequested)
            {
                await LoadPhotosAsync(0);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppendLog($"IA: {ex.Message}"); }
        finally
        {
            Dispatcher.Invoke(() =>
            {
                AiProgressBar.Value        = 100;
                AiProgressPanel.Visibility = Visibility.Collapsed;
            });
        }
    }

    // ═══════════════════════════════════════════════════════════
    // DRAG & DROP
    // ═══════════════════════════════════════════════════════════

    private void PhotoGallery_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void PhotoGallery_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var droppedFiles = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (droppedFiles == null || droppedFiles.Length == 0) return;

        var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

        var imageFiles = droppedFiles
            .Where(f => File.Exists(f) && imageExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        if (imageFiles.Count == 0)
        {
            MessageBox.Show("No se encontraron imágenes en los archivos arrastrados.",
                "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ProcessFilesAsync(imageFiles);
    }

    // ═══════════════════════════════════════════════════════════
    // PHOTO ACTIONS
    // ═══════════════════════════════════════════════════════════

    private void PhotoGallery_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PhotoGallery.SelectedItem is Photo photo) ViewPhoto(photo, PhotoGallery);
    }

    private void PrintPhoto_Click(object sender, RoutedEventArgs e)
    {
        if (PhotoGallery.SelectedItem is Photo photo) PrintPhoto(photo);
    }

    private void ViewPhoto_Click(object sender, RoutedEventArgs e)
    {
        if (PhotoGallery.SelectedItem is Photo photo) ViewPhoto(photo, PhotoGallery);
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

    private void ViewPhoto(Photo photo, ListView gallery)
    {
        if (!File.Exists(photo.LocalPath))
        {
            MessageBox.Show($"No encontrado:\n{photo.LocalPath}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var allPhotos = (gallery.ItemsSource as List<Photo>) ?? new List<Photo>();
        if (allPhotos.Count == 0) return;

        int startIndex = allPhotos.FindIndex(p => p.Id == photo.Id);
        if (startIndex < 0) startIndex = 0;

        var viewer = new PhotoViewerWindow(allPhotos, startIndex)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        viewer.ShowDialog();
    }

    private async Task DeletePhotoAsync(Photo photo)
    {
        ShowLoading(true);
        try
        {
            if (await _photoService.DeletePhotoAsync(photo.Id))
            {
                await LoadPhotosAsync(0);
                AppendLog($"Eliminada: {photo.Filename}");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ShowLoading(false); }
    }

    // ═══════════════════════════════════════════════════════════
    // FILTER
    // ═══════════════════════════════════════════════════════════

    private void ClearPersonFilter()
    {
        _filterPersonId              = null;
        PersonFilterPanel.Visibility = Visibility.Collapsed;
    }

    private async void ClearPersonFilter_Click(object sender, RoutedEventArgs e)
    {
        ClearPersonFilter();
        if (MainTabControl.SelectedIndex == 0)
            await LoadPhotosAsync(0);
        else
        {
            PersonPhotoGallery.ItemsSource = null;
            PersonPhotoCountText.Text = "Seleccioná una persona para ver sus fotos";
        }
    }

    // ═══════════════════════════════════════════════════════════
    // UI HELPERS
    // ═══════════════════════════════════════════════════════════

    private void AppendLog(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {msg}";
        _logLines.Add(line);
        while (_logLines.Count > 500) _logLines.RemoveAt(0);
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);

        if ((msg.Contains("Error") || _logLines.Count == 1) && !_logVisible)
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

    private void ThumbnailSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ThumbnailSizeCombo == null || PhotoGallery == null) return;

        double cardWidth, cardHeight, imgSize;
        switch (ThumbnailSizeCombo.SelectedIndex)
        {
            case 0: // Pequeño
                cardWidth = 120; cardHeight = 140; imgSize = 100;
                break;
            case 2: // Grande
                cardWidth = 260; cardHeight = 280; imgSize = 240;
                break;
            default: // Mediano
                cardWidth = 180; cardHeight = 200; imgSize = 160;
                break;
        }

        // Update the PhotoCardTemplate dynamically
        var template = CreatePhotoCardTemplate(cardWidth, cardHeight, imgSize);
        PhotoGallery.ItemTemplate = template;

        if (PersonPhotoGallery != null)
            PersonPhotoGallery.ItemTemplate = template;
    }

    private DataTemplate CreatePhotoCardTemplate(double cardWidth, double cardHeight, double imgSize)
    {
        var xaml = $@"
        <DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                      xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
            <Border Background=""White"" BorderBrush=""#E5E5E5"" BorderThickness=""1""
                    Margin=""6"" Width=""{cardWidth}"" Height=""{cardHeight}"" Cursor=""Hand"">
                <Grid>
                    <Image Source=""{{Binding ThumbnailPath}}"" Stretch=""UniformToFill""
                           Width=""{imgSize}"" Height=""{imgSize}""
                           Margin=""10,10,10,0"" VerticalAlignment=""Top""/>
                    <StackPanel VerticalAlignment=""Bottom"" Margin=""10"" Height=""20"">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width=""*""/>
                                <ColumnDefinition Width=""Auto""/>
                            </Grid.ColumnDefinitions>
                            <StackPanel Grid.Column=""0"" Orientation=""Horizontal"">
                                <TextBlock Text=""&#xE77B;"" FontFamily=""Segoe MDL2 Assets"" Foreground=""#605E5C"" FontSize=""12"" Margin=""0,0,4,0""/>
                                <TextBlock Text=""{{Binding FacesDetected}}"" Foreground=""#605E5C"" FontFamily=""Segoe UI"" FontSize=""11""/>
                            </StackPanel>
                            <TextBlock Grid.Column=""1"" Text=""&#xE753;"" FontFamily=""Segoe MDL2 Assets"" Foreground=""#0067C0"" FontSize=""12""
                                       Visibility=""{{Binding CloudSynced, Converter={{StaticResource BoolToVisibilityConverter}}}}""/>
                        </Grid>
                    </StackPanel>
                </Grid>
                <Border.Style>
                    <Style TargetType=""Border"">
                        <Style.Triggers>
                            <Trigger Property=""IsMouseOver"" Value=""True"">
                                <Setter Property=""Background"" Value=""#F3F2F1""/>
                                <Setter Property=""BorderBrush"" Value=""#0067C0""/>
                                <Setter Property=""BorderThickness"" Value=""2""/>
                            </Trigger>
                        </Style.Triggers>
                    </Style>
                </Border.Style>
            </Border>
        </DataTemplate>";

        return (DataTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

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
        StatusText.Text = $"{DateTime.Now:HH:mm:ss}  {msg}  ·  {folder}";
    }

    private FotoshowContext CreateBackgroundContext()
    {
        var opts = new DbContextOptionsBuilder<FotoshowContext>();
        opts.UseSqlite($"Data Source={_dbPath}");
        return new FotoshowContext(opts.Options);
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

/// <summary>
/// Simple data class for person cards in the Personas tab
/// </summary>
public class PersonCard
{
    public int PersonId { get; set; }
    public string DisplayName { get; set; } = "";
    public string PhotoCountText { get; set; } = "";
    public string ThumbnailPath { get; set; } = "";
}

/// <summary>
/// Simple input dialog for folder name entry
/// </summary>
public class InputDialog : Window
{
    private readonly TextBox _textBox;
    public string ResponseText => _textBox.Text;

    public InputDialog(string title, string prompt)
    {
        Title = title;
        Width = 350;
        Height = 160;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(243, 243, 243));

        var stack = new StackPanel { Margin = new Thickness(16) };

        stack.Children.Add(new TextBlock
        {
            Text = prompt,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8)
        });

        _textBox = new TextBox
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Padding = new Thickness(6, 4, 6, 4)
        };
        stack.Children.Add(_textBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var okBtn = new Button
        {
            Content = "Crear",
            Width = 80,
            Padding = new Thickness(0, 6, 0, 6),
            IsDefault = true,
            Background = new SolidColorBrush(Color.FromRgb(0, 103, 192)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            Cursor = Cursors.Hand
        };
        okBtn.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(okBtn);

        var cancelBtn = new Button
        {
            Content = "Cancelar",
            Width = 80,
            Padding = new Thickness(0, 6, 0, 6),
            IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0),
            FontFamily = new FontFamily("Segoe UI"),
            Cursor = Cursors.Hand
        };
        buttons.Children.Add(cancelBtn);

        stack.Children.Add(buttons);
        Content = stack;

        Loaded += (_, _) => _textBox.Focus();
    }
}
