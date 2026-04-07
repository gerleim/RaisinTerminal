using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Raisin.WPF.Base;
using RaisinTerminal.Core.Helpers;
using RaisinTerminal.Core.Terminal;
using RaisinTerminal.Models;
using RaisinTerminal.Services;
using RaisinTerminal.Views;

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

    public string? SlnxPath => Directory.GetFiles(HomePath, "*.slnx").FirstOrDefault();
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

public class ProjectsPanelViewModel : ViewModelBase
{
    private readonly ObservableCollection<ToolWindowViewModel> _documents;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Action<string>? _createSessionInDirectory;
    private readonly Action<string>? _createClaudeSessionInDirectory;
    private readonly Action<TerminalSessionViewModel>? _activateSession;
    private readonly Action<string, string>? _openAttachmentsWindow;

    public ObservableCollection<ProjectGroupNodeViewModel> Groups { get; } = [];
    public ObservableCollection<ProjectNodeViewModel> Projects { get; } = [];

    private ProjectNodeViewModel? _ungroupedNode;
    public ProjectNodeViewModel? UngroupedNode
    {
        get => _ungroupedNode;
        private set => SetProperty(ref _ungroupedNode, value);
    }

    private bool _isPanelVisible = true;
    public bool IsPanelVisible
    {
        get => _isPanelVisible;
        set => SetProperty(ref _isPanelVisible, value);
    }

    private double _panelWidth = 240;
    public double PanelWidth
    {
        get => _panelWidth;
        set => SetProperty(ref _panelWidth, value);
    }

    public ICommand AddProjectCommand { get; }
    public ICommand RemoveProjectCommand { get; }
    public ICommand NewTerminalCommand { get; }
    public ICommand NewClaudeTerminalCommand { get; }
    public ICommand ActivateTerminalCommand { get; }
    public ICommand TogglePanelCommand { get; }
    public ICommand ProjectPropertiesCommand { get; }
    public ICommand CopyAttachmentPathCommand { get; }
    public ICommand RenameAttachmentCommand { get; }
    public ICommand DeleteAttachmentCommand { get; }
    public ICommand OpenAttachmentCommand { get; }
    public ICommand OpenAttachmentsFolderCommand { get; }
    public ICommand OpenSlnxCommand { get; }
    public ICommand OpenSessionTextCommand { get; }
    public ICommand OpenSessionRawCommand { get; }
    public ICommand AddGroupCommand { get; }
    public ICommand RemoveGroupCommand { get; }
    public ICommand RenameGroupCommand { get; }
    public ICommand MoveProjectToGroupCommand { get; }

    private readonly List<Project> _projectList = [];
    private readonly List<ProjectGroup> _groupList = [];

    public ProjectsPanelViewModel(
        ObservableCollection<ToolWindowViewModel> documents,
        Action<string>? createSessionInDirectory = null,
        Action<string>? createClaudeSessionInDirectory = null,
        Action<TerminalSessionViewModel>? activateSession = null,
        Action<string, string>? openAttachmentsWindow = null)
    {
        _documents = documents;
        _createSessionInDirectory = createSessionInDirectory;
        _createClaudeSessionInDirectory = createClaudeSessionInDirectory;
        _activateSession = activateSession;
        _openAttachmentsWindow = openAttachmentsWindow;

        AddProjectCommand = new RelayCommand(AddProject);
        RemoveProjectCommand = new RelayCommand(o => RemoveProject(o as ProjectNodeViewModel));
        NewTerminalCommand = new RelayCommand(o => NewTerminalHere(o as ProjectNodeViewModel));
        NewClaudeTerminalCommand = new RelayCommand(o => NewClaudeTerminalHere(o as ProjectNodeViewModel));
        ActivateTerminalCommand = new RelayCommand(o => ActivateTerminal(o as TerminalNodeViewModel));
        TogglePanelCommand = new RelayCommand(() => IsPanelVisible = !IsPanelVisible);
        ProjectPropertiesCommand = new RelayCommand(o => ShowProjectProperties(o as ProjectNodeViewModel));
        CopyAttachmentPathCommand = new RelayCommand(o => CopyAttachmentPath(o as AttachmentItemViewModel));
        RenameAttachmentCommand = new RelayCommand(o => RenameAttachment(o as AttachmentItemViewModel));
        DeleteAttachmentCommand = new RelayCommand(o => DeleteAttachment(o as AttachmentItemViewModel));
        OpenAttachmentCommand = new RelayCommand(o => OpenAttachment(o as AttachmentItemViewModel));
        OpenAttachmentsFolderCommand = new RelayCommand(o => OpenAttachmentsFolder(o as ProjectNodeViewModel));
        OpenSlnxCommand = new RelayCommand(o => OpenSlnx(o as ProjectNodeViewModel));
        OpenSessionTextCommand = new RelayCommand(o => OpenSessionTranscript(o as TerminalNodeViewModel, ".txt"));
        OpenSessionRawCommand = new RelayCommand(o => OpenSessionTranscript(o as TerminalNodeViewModel, ".raw"));
        AddGroupCommand = new RelayCommand(AddGroup);
        RemoveGroupCommand = new RelayCommand(o => RemoveGroup(o as ProjectGroupNodeViewModel));
        RenameGroupCommand = new RelayCommand(o => RenameGroup(o as ProjectGroupNodeViewModel));
        MoveProjectToGroupCommand = new RelayCommand(o => MoveProjectToGroup(o as object[]));

        // Load persisted state
        var state = ProjectService.Load();
        _projectList.AddRange(state.Projects);
        _groupList.AddRange(state.ProjectGroups);
        IsPanelVisible = state.PanelVisible;
        PanelWidth = state.PanelWidth > 0 ? state.PanelWidth : 240;

        // Auto-detect icons for projects that don't have one yet
        bool iconChanged = false;
        foreach (var project in _projectList)
        {
            if (project.IconPath == null && Directory.Exists(project.HomePath))
            {
                project.IconPath = FindBestIcon(project.HomePath);
                if (project.IconPath != null) iconChanged = true;
            }
        }
        if (iconChanged) SaveState();

        Rebuild();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => Rebuild();
        _refreshTimer.Start();
    }

    public void SaveState()
    {
        var state = new ProjectsState
        {
            Projects = _projectList.ToList(),
            ProjectGroups = _groupList.ToList(),
            PanelVisible = IsPanelVisible,
            PanelWidth = PanelWidth
        };
        ProjectService.Save(state);
    }

    private void Rebuild()
    {
        var sessions = new List<TerminalSessionViewModel>();
        foreach (var doc in _documents)
        {
            if (doc is TerminalSessionViewModel session)
                sessions.Add(session);
        }

        // Match sessions to projects by longest HomePath prefix
        var matched = new Dictionary<string, List<TerminalSessionViewModel>>();
        var unmatched = new List<TerminalSessionViewModel>();

        foreach (var session in sessions)
        {
            var wd = session.WorkingDirectory;
            Project? bestMatch = null;
            int bestLen = 0;

            if (!string.IsNullOrEmpty(wd))
            {
                foreach (var project in _projectList)
                {
                    if (IsSubPath(wd, project.HomePath) && project.HomePath.Length > bestLen)
                    {
                        bestMatch = project;
                        bestLen = project.HomePath.Length;
                    }
                }
            }

            if (bestMatch != null)
            {
                if (!matched.ContainsKey(bestMatch.Id))
                    matched[bestMatch.Id] = [];
                matched[bestMatch.Id].Add(session);
            }
            else
            {
                unmatched.Add(session);
            }

            // Set up PasteImage callback on each session
            SetupPasteImageHandler(session);
        }

        // Update project nodes
        SyncProjectNodes(matched);

        // Update ungrouped node
        if (unmatched.Count > 0)
        {
            if (_ungroupedNode == null)
            {
                UngroupedNode = new ProjectNodeViewModel(new Project { Name = "Ungrouped", HomePath = "" });
            }
            _ungroupedNode!.Project.AlertOnWaitingForInput = SettingsService.Current.AlertOnWaitingForInput;
            SyncTerminalNodes(_ungroupedNode!, unmatched);
        }
        else
        {
            UngroupedNode = null;
        }

        // Evict stale entries from process-debounce cache
        var activeIds = new HashSet<string>();
        foreach (var s in sessions)
            activeIds.Add(s.ContentId);
        foreach (var key in _lastSeenRunning.Keys)
        {
            if (!activeIds.Contains(key))
                _lastSeenRunning.TryRemove(key, out _);
        }
    }

    private void SetupPasteImageHandler(TerminalSessionViewModel session)
    {
        session.PasteImage = image =>
        {
            var projectId = FindProjectIdForPath(session.WorkingDirectory);
            if (projectId == null) return null;

            var path = AttachmentService.SaveImage(image, projectId);

            // Find the project node and refresh its attachments
            var projectNode = FindProjectNode(projectId);
            if (projectNode != null)
                SyncAttachments(projectNode);

            return path;
        };
    }

    private string? FindProjectIdForPath(string? workingDirectory)
    {
        if (string.IsNullOrEmpty(workingDirectory)) return null;

        string? bestId = null;
        int bestLen = 0;

        foreach (var project in _projectList)
        {
            if (IsSubPath(workingDirectory, project.HomePath) && project.HomePath.Length > bestLen)
            {
                bestId = project.Id;
                bestLen = project.HomePath.Length;
            }
        }

        return bestId;
    }

    private void SyncProjectNodes(Dictionary<string, List<TerminalSessionViewModel>> matched)
    {
        // Sync group nodes
        SyncGroupNodes();

        // Partition projects: ungrouped (GroupId == null) go to root Projects,
        // grouped projects go into their group's Projects collection
        var ungroupedProjects = _projectList.Where(p => p.GroupId == null).ToList();
        var validGroupIds = new HashSet<string>(_groupList.Select(g => g.Id));

        // Projects whose GroupId references a deleted group become ungrouped
        foreach (var project in _projectList)
        {
            if (project.GroupId != null && !validGroupIds.Contains(project.GroupId))
            {
                project.GroupId = null;
                ungroupedProjects.Add(project);
            }
        }

        SyncProjectCollection(Projects, ungroupedProjects, matched);

        foreach (var groupNode in Groups)
        {
            var groupProjects = _projectList.Where(p => p.GroupId == groupNode.Group.Id).ToList();
            SyncProjectCollection(groupNode.Projects, groupProjects, matched);
        }
    }

    private void SyncGroupNodes()
    {
        // Remove groups no longer in _groupList
        for (int i = Groups.Count - 1; i >= 0; i--)
        {
            if (!_groupList.Any(g => g.Id == Groups[i].Group.Id))
                Groups.RemoveAt(i);
        }

        // Add new groups
        foreach (var group in _groupList)
        {
            if (!Groups.Any(g => g.Group.Id == group.Id))
                Groups.Add(new ProjectGroupNodeViewModel(group));
        }
    }

    private static void SyncProjectCollection(
        ObservableCollection<ProjectNodeViewModel> collection,
        List<Project> projects,
        Dictionary<string, List<TerminalSessionViewModel>> matched)
    {
        // Remove projects that no longer belong in this collection
        for (int i = collection.Count - 1; i >= 0; i--)
        {
            if (!projects.Any(p => p.Id == collection[i].Project.Id))
                collection.RemoveAt(i);
        }

        // Add/update project nodes
        foreach (var project in projects)
        {
            var node = collection.FirstOrDefault(p => p.Project.Id == project.Id);
            if (node == null)
            {
                node = new ProjectNodeViewModel(project);
                collection.Add(node);
            }

            var terminalList = matched.TryGetValue(project.Id, out var list) ? list : [];
            SyncTerminalNodes(node, terminalList);
            SyncAttachments(node);
        }
    }

    private static void SyncTerminalNodes(ProjectNodeViewModel projectNode, List<TerminalSessionViewModel> sessions)
    {
        // Remove terminals no longer in session list
        for (int i = projectNode.Terminals.Count - 1; i >= 0; i--)
        {
            if (!sessions.Any(s => s.ContentId == projectNode.Terminals[i].Session.ContentId))
                projectNode.Terminals.RemoveAt(i);
        }

        // Add new terminals and update existing
        foreach (var session in sessions)
        {
            var existing = projectNode.Terminals.FirstOrDefault(t => t.Session.ContentId == session.ContentId);
            if (existing == null)
            {
                existing = new TerminalNodeViewModel(session);
                projectNode.Terminals.Add(existing);
            }

            existing.Title = session.Title;
            var newStatus = DetermineStatus(session);
            if (newStatus == TerminalStatus.WaitingForInput && existing.Status != TerminalStatus.WaitingForInput
                && projectNode.Project.AlertOnWaitingForInput)
                AlertSoundPlayer.Play(SettingsService.Current.AlertSound);
            existing.Status = newStatus;
        }
    }

    private static void SyncAttachments(ProjectNodeViewModel projectNode)
    {
        var diskFiles = AttachmentService.GetAttachments(projectNode.Project.Id);
        var diskSet = new HashSet<string>(diskFiles, StringComparer.OrdinalIgnoreCase);

        // Remove attachments no longer on disk
        for (int i = projectNode.Attachments.Count - 1; i >= 0; i--)
        {
            if (!diskSet.Contains(projectNode.Attachments[i].FilePath))
                projectNode.Attachments.RemoveAt(i);
        }

        // Add new attachments (maintain order: newest first)
        var existingSet = new HashSet<string>(
            projectNode.Attachments.Select(a => a.FilePath), StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < diskFiles.Count; i++)
        {
            if (!existingSet.Contains(diskFiles[i]))
            {
                projectNode.Attachments.Insert(i, new AttachmentItemViewModel(diskFiles[i]));
            }
        }
    }

    // Debounce: track when each session was last seen with a running child process.
    // Toolhelp32 snapshots can transiently miss processes during tree transitions,
    // so we require ~4s of consecutive "not running" before transitioning to Idle.
    private static readonly ConcurrentDictionary<string, DateTime> _lastSeenRunning = new();

    internal static TerminalStatus DetermineStatus(TerminalSessionViewModel session)
    {
        var sessionId = session.ContentId;

        if (!session.HasRunningCommand)
        {
            // Debounce: if session was running recently, don't immediately switch to Idle
            // — Toolhelp32 snapshots can transiently miss processes during tree transitions.
            // Use screen classification but suppress Idle (the prompt may be visible even
            // while Claude is still producing output above it).
            if (_lastSeenRunning.TryGetValue(sessionId, out var lastRun)
                && DateTime.UtcNow - lastRun < TimeSpan.FromSeconds(4))
            {
                var screenStatus = ClaudeScreenStateClassifier.Classify(
                    row => session.ReadScreenLine(row), session.CursorRow, session.ScreenRows);
                return screenStatus is TerminalStatus.Idle or TerminalStatus.AgentsRunning
                    ? TerminalStatus.Working : screenStatus;
            }
            return TerminalStatus.Idle;
        }

        _lastSeenRunning[sessionId] = DateTime.UtcNow;

        var childName = session.RunningChildName;

        // Claude Code: always scan screen content — Claude's TUI produces frequent
        // status bar updates that would keep LastOutputTime permanently fresh
        if (string.Equals(childName, "claude", StringComparison.OrdinalIgnoreCase))
            return ClaudeScreenStateClassifier.Classify(
                row => session.ReadScreenLine(row), session.CursorRow, session.ScreenRows);

        if (session.IsInAlternateScreen)
            return TerminalStatus.WaitingForInput;
        return TerminalStatus.Working;
    }

    private static bool IsSubPath(string path, string basePath) =>
        PathHelper.IsSubPath(path, basePath);

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".git", "packages", ".vs", ".idea", "TestResults" };

    internal static string? FindBestIcon(string rootPath)
    {
        var candidates = new List<string>();
        CollectIcoFiles(rootPath, candidates, depth: 0, maxDepth: 4);

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        // Score each candidate
        string? best = null;
        int bestScore = int.MinValue;

        foreach (var icoPath in candidates)
        {
            int score = 0;
            var dir = Path.GetDirectoryName(icoPath)!;
            var fileName = Path.GetFileName(icoPath);

            // Prefer app.ico over other names
            if (string.Equals(fileName, "app.ico", StringComparison.OrdinalIgnoreCase))
                score += 2;

            // Check path segments for hints
            var relativePath = icoPath.Substring(rootPath.Length).ToLowerInvariant();
            if (relativePath.Contains("wpf") || relativePath.Contains("desktop") || relativePath.Contains(".ui"))
                score += 3;
            if (relativePath.Contains("test"))
                score -= 5;

            // Fewer directory levels = closer to root = better
            int depth = relativePath.Count(c => c == '\\' || c == '/');
            score += Math.Max(0, 5 - depth);

            // Find nearest .csproj and check for WPF/WinExe indicators
            var csproj = FindNearestCsproj(dir, rootPath);
            if (csproj != null)
            {
                try
                {
                    var content = File.ReadAllText(csproj);
                    if (content.Contains("<UseWPF>true</UseWPF>", StringComparison.OrdinalIgnoreCase))
                        score += 10;
                    if (content.Contains("<OutputType>WinExe</OutputType>", StringComparison.OrdinalIgnoreCase))
                        score += 5;
                    if (content.Contains("<OutputType>Exe</OutputType>", StringComparison.OrdinalIgnoreCase))
                        score += 2;
                }
                catch { }
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = icoPath;
            }
        }

        return best;
    }

    private static void CollectIcoFiles(string dir, List<string> results, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.ico"))
                results.Add(file);

            foreach (var subDir in Directory.EnumerateDirectories(dir))
            {
                var dirName = Path.GetFileName(subDir);
                if (!SkipDirs.Contains(dirName))
                    CollectIcoFiles(subDir, results, depth + 1, maxDepth);
            }
        }
        catch { } // Access denied, etc.
    }

    private static string? FindNearestCsproj(string dir, string rootPath)
    {
        var current = dir;
        while (current != null && current.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var csprojs = Directory.GetFiles(current, "*.csproj");
                if (csprojs.Length > 0) return csprojs[0];
            }
            catch { }
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    private void AddProject()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Project Folder"
        };

        if (dialog.ShowDialog() == true)
        {
            var folderPath = dialog.FolderName;
            // Don't add duplicates
            if (_projectList.Any(p => string.Equals(
                    Path.GetFullPath(p.HomePath).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)))
                return;

            var project = new Project
            {
                Name = Path.GetFileName(folderPath),
                HomePath = folderPath,
                IconPath = FindBestIcon(folderPath),
                AlertOnWaitingForInput = SettingsService.Current.AlertOnWaitingForInput
            };
            _projectList.Add(project);
            SaveState();
            Rebuild();
        }
    }

    private void RemoveProject(ProjectNodeViewModel? node)
    {
        if (node == null) return;
        _projectList.RemoveAll(p => p.Id == node.Project.Id);
        Projects.Remove(node);
        foreach (var g in Groups)
            g.Projects.Remove(node);
        SaveState();
        Rebuild();
    }

    private void ShowProjectProperties(ProjectNodeViewModel? node)
    {
        if (node == null) return;
        var window = new Views.ProjectPropertiesWindow(node.Project)
        {
            Owner = Application.Current.MainWindow
        };
        if (window.ShowDialog() == true)
        {
            node.LoadIcon();
            node.RaiseNameChanged();
            SaveState();
            Rebuild();
        }
    }

    private void NewTerminalHere(ProjectNodeViewModel? node)
    {
        if (node == null || string.IsNullOrEmpty(node.HomePath)) return;
        _createSessionInDirectory?.Invoke(node.HomePath);
    }

    private void NewClaudeTerminalHere(ProjectNodeViewModel? node)
    {
        if (node == null || string.IsNullOrEmpty(node.HomePath)) return;
        _createClaudeSessionInDirectory?.Invoke(node.HomePath);
    }

    private void ActivateTerminal(TerminalNodeViewModel? node)
    {
        if (node == null) return;
        _activateSession?.Invoke(node.Session);
    }

    private static void CopyAttachmentPath(AttachmentItemViewModel? item)
    {
        if (item == null) return;
        Clipboard.SetText(item.FilePath);
    }

    private void RenameAttachment(AttachmentItemViewModel? item)
    {
        if (item == null || !File.Exists(item.FilePath)) return;

        var dialog = new RenameDialog(item.FilePath)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true) return;

        var newPath = AttachmentService.RenameAttachment(item.FilePath, dialog.NewFileName);
        if (newPath == null) return;

        // Replace the item in the owning project's attachment list
        foreach (var project in AllProjectNodes())
        {
            var idx = project.Attachments.IndexOf(item);
            if (idx >= 0)
            {
                project.Attachments[idx] = new AttachmentItemViewModel(newPath);
                break;
            }
        }
    }

    private void DeleteAttachment(AttachmentItemViewModel? item)
    {
        if (item == null) return;
        AttachmentService.DeleteAttachment(item.FilePath);

        // Remove from the owning project node
        foreach (var project in AllProjectNodes())
        {
            if (project.Attachments.Remove(item))
                break;
        }
    }

    private static void OpenAttachment(AttachmentItemViewModel? item)
    {
        if (item == null || !File.Exists(item.FilePath)) return;
        Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
    }

    private static void OpenSlnx(ProjectNodeViewModel? node)
    {
        var path = node?.SlnxPath;
        if (path == null) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void OpenSessionTranscript(TerminalNodeViewModel? node, string extension)
    {
        if (node == null) return;
        var sessionsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RaisinTerminal", "sessions");
        var filePath = Path.Combine(sessionsDir, node.Session.ContentId + extension);
        if (!File.Exists(filePath)) return;
        Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
    }

    private void OpenAttachmentsFolder(ProjectNodeViewModel? node)
    {
        if (node == null) return;
        _openAttachmentsWindow?.Invoke(node.Project.Id, node.Name);
    }

    private IEnumerable<ProjectNodeViewModel> AllProjectNodes()
    {
        foreach (var p in Projects) yield return p;
        foreach (var g in Groups)
            foreach (var p in g.Projects) yield return p;
    }

    private ProjectNodeViewModel? FindProjectNode(string projectId)
    {
        return AllProjectNodes().FirstOrDefault(p => p.Project.Id == projectId);
    }

    private void AddGroup()
    {
        var dialog = new GroupNameDialog("New Group")
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true) return;

        var group = new ProjectGroup { Name = dialog.GroupName };
        _groupList.Add(group);
        SaveState();
        Rebuild();
    }

    private void RemoveGroup(ProjectGroupNodeViewModel? node)
    {
        if (node == null) return;
        // Ungroup all projects in this group
        foreach (var p in _projectList.Where(p => p.GroupId == node.Group.Id))
            p.GroupId = null;
        _groupList.RemoveAll(g => g.Id == node.Group.Id);
        SaveState();
        Rebuild();
    }

    private void RenameGroup(ProjectGroupNodeViewModel? node)
    {
        if (node == null) return;
        var dialog = new GroupNameDialog(node.Group.Name)
        {
            Owner = Application.Current.MainWindow
        };
        if (dialog.ShowDialog() != true) return;

        node.Group.Name = dialog.GroupName;
        node.RaiseNameChanged();
        SaveState();
    }

    private void MoveProjectToGroup(object[]? args)
    {
        if (args is not { Length: 2 }) return;
        if (args[0] is not ProjectNodeViewModel projectNode) return;
        var groupId = args[1] as string; // null means ungrouped
        projectNode.Project.GroupId = groupId;
        SaveState();
        Rebuild();
    }

    internal IReadOnlyList<ProjectGroup> GetGroups() => _groupList;

    private void AddProjectToGroup(ProjectGroupNodeViewModel? groupNode)
    {
        if (groupNode == null) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Project Folder"
        };

        if (dialog.ShowDialog() != true) return;

        var folderPath = dialog.FolderName;
        if (_projectList.Any(p => string.Equals(
                Path.GetFullPath(p.HomePath).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)))
            return;

        var project = new Project
        {
            Name = Path.GetFileName(folderPath),
            HomePath = folderPath,
            IconPath = FindBestIcon(folderPath),
            AlertOnWaitingForInput = SettingsService.Current.AlertOnWaitingForInput,
            GroupId = groupNode.Group.Id
        };
        _projectList.Add(project);
        SaveState();
        Rebuild();
    }

    internal void AddProjectToGroupCommand_Execute(ProjectGroupNodeViewModel? groupNode) =>
        AddProjectToGroup(groupNode);

    public void StopTimer()
    {
        _refreshTimer.Stop();
    }
}
