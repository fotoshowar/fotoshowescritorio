using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Fotoshow;

public class CameraSettingsDialog : Window
{
    private readonly ListBox _cameraList;
    private readonly List<string> _cameras;
    public bool Changed { get; private set; }

    public List<string> Cameras => _cameras.ToList();

    public CameraSettingsDialog(List<string> cameras)
    {
        _cameras = new List<string>(cameras);

        Title = "Configuración de Cámaras";
        Width = 400;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(243, 243, 243));

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

        // Header
        var header = new TextBlock
        {
            Text = "Cámaras configuradas",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(16, 16, 16, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(50, 49, 48))
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // Camera list
        _cameraList = new ListBox
        {
            Margin = new Thickness(16, 0, 16, 0),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(229, 229, 229)),
            BorderThickness = new Thickness(1)
        };
        Grid.SetRow(_cameraList, 1);
        root.Children.Add(_cameraList);

        RefreshList();

        // Add/Remove buttons
        var actionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(16, 8, 16, 8)
        };
        Grid.SetRow(actionButtons, 2);

        var addBtn = CreateButton("Agregar", Color.FromRgb(0, 103, 192), Colors.White);
        addBtn.Click += AddCamera_Click;
        actionButtons.Children.Add(addBtn);

        var removeBtn = CreateButton("Quitar", Color.FromRgb(243, 243, 243), Colors.Black);
        removeBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(229, 229, 229));
        removeBtn.BorderThickness = new Thickness(1);
        removeBtn.Margin = new Thickness(8, 0, 0, 0);
        removeBtn.Click += RemoveCamera_Click;
        actionButtons.Children.Add(removeBtn);

        root.Children.Add(actionButtons);

        // Save/Cancel buttons
        var dialogButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16)
        };
        Grid.SetRow(dialogButtons, 3);

        var saveBtn = CreateButton("Guardar", Color.FromRgb(0, 103, 192), Colors.White);
        saveBtn.IsDefault = true;
        saveBtn.Click += (_, _) =>
        {
            CommitEdits();
            Changed = true;
            DialogResult = true;
        };
        dialogButtons.Children.Add(saveBtn);

        var cancelBtn = CreateButton("Cancelar", Color.FromRgb(243, 243, 243), Colors.Black);
        cancelBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(229, 229, 229));
        cancelBtn.BorderThickness = new Thickness(1);
        cancelBtn.IsCancel = true;
        cancelBtn.Margin = new Thickness(8, 0, 0, 0);
        dialogButtons.Children.Add(cancelBtn);

        root.Children.Add(dialogButtons);
        Content = root;
    }

    private void RefreshList()
    {
        _cameraList.Items.Clear();
        for (int i = 0; i < _cameras.Count; i++)
        {
            var tb = new TextBox
            {
                Text = _cameras[i],
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(2),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(229, 229, 229)),
                Tag = i
            };
            tb.LostFocus += (s, _) =>
            {
                if (s is TextBox t && t.Tag is int idx && idx < _cameras.Count)
                    _cameras[idx] = t.Text.Trim();
            };
            _cameraList.Items.Add(tb);
        }
    }

    private void CommitEdits()
    {
        for (int i = 0; i < _cameraList.Items.Count && i < _cameras.Count; i++)
        {
            if (_cameraList.Items[i] is TextBox tb)
                _cameras[i] = tb.Text.Trim();
        }
        _cameras.RemoveAll(string.IsNullOrWhiteSpace);
    }

    private void AddCamera_Click(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        _cameras.Add($"Camara {_cameras.Count + 1}");
        RefreshList();
    }

    private void RemoveCamera_Click(object sender, RoutedEventArgs e)
    {
        var idx = _cameraList.SelectedIndex;
        if (idx < 0)
        {
            MessageBox.Show("Seleccioná una cámara para quitar.", "Aviso",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = _cameras[idx];
        var r = MessageBox.Show($"¿Quitar la cámara \"{name}\"?",
            "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes)
        {
            CommitEdits();
            _cameras.RemoveAt(idx);
            RefreshList();
        }
    }

    private static Button CreateButton(string text, Color bg, Color fg)
    {
        return new Button
        {
            Content = text,
            Width = 90,
            Padding = new Thickness(0, 6, 0, 6),
            Background = new SolidColorBrush(bg),
            Foreground = new SolidColorBrush(fg),
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Cursor = Cursors.Hand
        };
    }
}
