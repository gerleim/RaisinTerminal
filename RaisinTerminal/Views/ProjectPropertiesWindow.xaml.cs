using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using RaisinTerminal.Models;
using RaisinTerminal.ViewModels;

namespace RaisinTerminal.Views;

public partial class ProjectPropertiesWindow : Window
{
    public Project Project { get; }
    private string? _iconPath;

    public ProjectPropertiesWindow(Project project)
    {
        InitializeComponent();
        Project = project;

        IdBox.Text = project.Id;
        NameBox.Text = project.Name;
        HomePathBox.Text = project.HomePath;
        SetIconPath(project.IconPath);
    }

    private void SetIconPath(string? path)
    {
        _iconPath = path;
        IconPathBox.Text = path ?? "(none)";

        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.DecodePixelWidth = 32;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                IconPreview.Source = bmp;
            }
            catch
            {
                IconPreview.Source = null;
            }
        }
        else
        {
            IconPreview.Source = null;
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Project Folder",
            InitialDirectory = HomePathBox.Text
        };

        if (dialog.ShowDialog(this) == true)
        {
            HomePathBox.Text = dialog.FolderName;
        }
    }

    private void OnBrowseIcon(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Icon",
            Filter = "Icon files (*.ico)|*.ico|All files (*.*)|*.*",
            InitialDirectory = HomePathBox.Text
        };

        if (dialog.ShowDialog(this) == true)
        {
            SetIconPath(dialog.FileName);
        }
    }

    private void OnAutoDetectIcon(object sender, RoutedEventArgs e)
    {
        var homePath = HomePathBox.Text.Trim();
        if (string.IsNullOrEmpty(homePath) || !Directory.Exists(homePath))
            return;

        var found = ProjectsPanelViewModel.FindBestIcon(homePath);
        SetIconPath(found);
    }

    private void OnClearIcon(object sender, RoutedEventArgs e)
    {
        SetIconPath(null);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Project.Name = NameBox.Text.Trim();
        Project.HomePath = HomePathBox.Text.Trim();
        Project.IconPath = _iconPath;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
