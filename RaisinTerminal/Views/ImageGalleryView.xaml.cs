using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace RaisinTerminal.Views;

public partial class ImageGalleryView : UserControl
{
    private Point _dragStartPoint;
    private bool _dragReady;

    public ImageGalleryView()
    {
        InitializeComponent();
        ImageListBox.PreviewMouseLeftButtonDown += OnItemMouseDown;
        ImageListBox.PreviewMouseMove += OnItemMouseMove;
        ImageListBox.PreviewMouseLeftButtonUp += (_, _) => _dragReady = false;
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Only start drag if clicking on an actual item
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item == null) return;

        _dragStartPoint = e.GetPosition(null);
        _dragReady = true;
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragReady || e.LeftButton != MouseButtonState.Pressed) return;

        var diff = e.GetPosition(null) - _dragStartPoint;
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var selectedPath = ImageListBox.SelectedItem as string;
        if (selectedPath == null) return;

        _dragReady = false;
        var data = new DataObject(DataFormats.FileDrop, new[] { selectedPath });
        DragDrop.DoDragDrop(ImageListBox, data, DragDropEffects.Copy);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}

public class PathToFileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is string path ? Path.GetFileName(path) : "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
