using System.IO;
using System.Windows;

namespace RaisinTerminal.Views;

public partial class RenameDialog : Window
{
    private readonly string _directory;
    private readonly string _extension;

    public string NewFileName { get; private set; } = "";

    public RenameDialog(string currentFilePath)
    {
        InitializeComponent();

        _directory = Path.GetDirectoryName(currentFilePath)!;
        _extension = Path.GetExtension(currentFilePath);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(currentFilePath);

        ExtensionText.Text = _extension;
        NameBox.Text = nameWithoutExt;
        NameBox.SelectAll();
        NameBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ShowError("Name cannot be empty.");
            return;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ShowError("Name contains invalid characters.");
            return;
        }

        var newPath = Path.Combine(_directory, name + _extension);
        if (File.Exists(newPath))
        {
            ShowError("A file with this name already exists.");
            return;
        }

        NewFileName = name + _extension;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
