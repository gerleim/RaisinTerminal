using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Raisin.WPF.Base;
using RaisinTerminal.Core.Terminal;
using RaisinTerminal.Models;

namespace RaisinTerminal.ViewModels;

public class TerminalNodeViewModel : ViewModelBase
{
    public TerminalSessionViewModel Session { get; }

    private string _title = "";
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private TerminalStatus _status;
    public TerminalStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public TerminalNodeViewModel(TerminalSessionViewModel session)
    {
        Session = session;
        Title = session.Title;
    }
}

public class AttachmentItemViewModel : ViewModelBase
{
    public string FilePath { get; }
    public string FileName => Path.GetFileName(FilePath);

    public AttachmentItemViewModel(string filePath)
    {
        FilePath = filePath;
    }
}

public class ProjectNodeViewModel : ViewModelBase
{
    public Project Project { get; }

    public string Name => Project.Name;
    public string HomePath => Project.HomePath;

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    private ImageSource? _iconSource;
    public ImageSource? IconSource
    {
        get => _iconSource;
        set => SetProperty(ref _iconSource, value);
    }

    public string? SlnxPath => Directory.Exists(HomePath) ? Directory.GetFiles(HomePath, "*.slnx").FirstOrDefault() : null;
    public bool HasSlnx => SlnxPath != null;

    public ObservableCollection<TerminalNodeViewModel> Terminals { get; } = [];
    public ObservableCollection<AttachmentItemViewModel> Attachments { get; } = [];

    public ProjectNodeViewModel(Project project)
    {
        Project = project;
        LoadIcon();
    }

    public void RaiseNameChanged() => OnPropertyChanged(nameof(Name));

    public void LoadIcon()
    {
        IconSource = null;
        if (string.IsNullOrEmpty(Project.IconPath) || !File.Exists(Project.IconPath))
            return;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(Project.IconPath, UriKind.Absolute);
            bmp.DecodePixelWidth = 32;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            IconSource = bmp;
        }
        catch
        {
            // Icon file corrupt or unreadable — keep folder fallback
        }
    }
}

public class ProjectGroupNodeViewModel : ViewModelBase
{
    public ProjectGroup Group { get; }

    public string Name => Group.Name;

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public ObservableCollection<ProjectNodeViewModel> Projects { get; } = [];

    public ProjectGroupNodeViewModel(ProjectGroup group)
    {
        Group = group;
    }

    public void RaiseNameChanged() => OnPropertyChanged(nameof(Name));
}
