// REEMPLAZA el método ViewPhoto (línea 441) con este código:

private void ViewPhoto(Photo photo)
{
    if (!File.Exists(photo.LocalPath))
    {
        MessageBox.Show($"No encontrado:\n{photo.LocalPath}", "Error",
            MessageBoxButton.OK, MessageBoxImage.Error);
        return;
    }

    // Obtener todas las fotos actuales de la galería
    var allPhotos = (PhotoGallery.ItemsSource as List<Photo>) ?? new List<Photo>();
    
    if (allPhotos.Count == 0)
    {
        MessageBox.Show("No hay fotos para mostrar", "Info",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return;
    }

    // Encontrar el índice de la foto seleccionada
    int startIndex = allPhotos.FindIndex(p => p.Id == photo.Id);
    if (startIndex < 0) startIndex = 0;

    // Abrir el visor en pantalla completa
    var viewer = new PhotoViewerWindow(allPhotos, startIndex)
    {
        Owner = this,
        WindowStartupLocation = WindowStartupLocation.CenterScreen
    };
    viewer.ShowDialog();
}
