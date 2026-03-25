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
using RaisinTerminal.Models;
using RaisinTerminal.Services;
using RaisinTerminal.Views;

namespace RaisinTerminal.ViewModels;

public enum TerminalStatus
{
    Idle,
    Working,
    WaitingForInput
}

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

public class ProjectsPanelViewModel : ViewModelBase
{
    private readonly ObservableCollection<ToolWindowViewModel> _documents;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Action<string>? _createSessionInDirectory;
    private readonly Action<string>? _createClaudeSessionInDirectory;
    private readonly Action<TerminalSessionViewModel>? _activateSession;
    private readonly Action<string, string>? _openAttachmentsWindow;

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
    public ICommand DeleteAttachmentCommand { get; }
    public ICommand OpenAttachmentCommand { get; }
    public ICommand OpenAttachmentsFolderCommand { get; }
    public ICommand OpenSlnxCommand { get; }

    private readonly List<Project> _projectList = [];

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
        DeleteAttachmentCommand = new RelayCommand(o => DeleteAttachment(o as AttachmentItemViewModel));
        OpenAttachmentCommand = new RelayCommand(o => OpenAttachment(o as AttachmentItemViewModel));
        OpenAttachmentsFolderCommand = new RelayCommand(o => OpenAttachmentsFolder(o as ProjectNodeViewModel));
        OpenSlnxCommand = new RelayCommand(o => OpenSlnx(o as ProjectNodeViewModel));

        // Load persisted state
        var state = ProjectService.Load();
        _projectList.AddRange(state.Projects);
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
            SyncTerminalNodes(_ungroupedNode!, unmatched);
        }
        else
        {
            UngroupedNode = null;
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
            var projectNode = Projects.FirstOrDefault(p => p.Project.Id == projectId);
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
        // Remove projects that no longer exist in _projectList
        for (int i = Projects.Count - 1; i >= 0; i--)
        {
            if (!_projectList.Any(p => p.Id == Projects[i].Project.Id))
                Projects.RemoveAt(i);
        }

        // Add/update project nodes
        foreach (var project in _projectList)
        {
            var node = Projects.FirstOrDefault(p => p.Project.Id == project.Id);
            if (node == null)
            {
                node = new ProjectNodeViewModel(project);
                Projects.Add(node);
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
            existing.Status = DetermineStatus(session);
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

    // Claude Code's thinking spinner cycles through these glyphs:
    // · (U+00B7) ✢ (U+2722) ✳ (U+2733) ✶ (U+2736) ✻ (U+273B) ✽ (U+273D)
    private static readonly HashSet<char> ClaudeSpinnerGlyphs =
        ['\u00B7', '\u2722', '\u2733', '\u2736', '\u273B', '\u273D'];

    private static readonly TimeSpan OutputIdleThreshold = TimeSpan.FromSeconds(2);

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
                var screenStatus = ClassifyClaudeScreenState(session);
                return screenStatus == TerminalStatus.Idle ? TerminalStatus.Working : screenStatus;
            }
            return TerminalStatus.Idle;
        }

        _lastSeenRunning[sessionId] = DateTime.UtcNow;

        var childName = session.RunningChildName;

        // Claude Code: always scan screen content — Claude's TUI produces frequent
        // status bar updates that would keep LastOutputTime permanently fresh
        if (string.Equals(childName, "claude", StringComparison.OrdinalIgnoreCase))
            return ClassifyClaudeScreenState(session);

        if (session.IsInAlternateScreen)
            return TerminalStatus.WaitingForInput;
        return TerminalStatus.Working;
    }

    /// <summary>
    /// Scans the terminal screen to classify Claude Code's state:
    /// - Idle: main input prompt visible (line starts with ">")
    /// - WaitingForInput: positively detected approval/input prompt
    /// - Working: default when output has settled but no prompt detected
    /// </summary>
    private static TerminalStatus ClassifyClaudeScreenState(TerminalSessionViewModel session)
    {
        int cursorRow = session.CursorRow;
        int screenRows = session.ScreenRows;
        if (cursorRow < 0 || screenRows <= 0) return TerminalStatus.Working;

        // Claude's Ink TUI may place the cursor on the status bar at the screen
        // bottom, far from the actual "❯" prompt. Scan all visible rows, but use
        // targeted checks to avoid false positives from normal output text.
        TerminalStatus? result = null;
        bool foundIdlePrompt = false;
        bool foundWorkingIndicator = false;
        bool foundCompletionSummary = false;
        int completionSummaryRow = -1;
        int idlePromptRow = -1;
        bool foundNumberedOptions = false;
        int foundOption1Row = -1;
        int foundOption2Row = -1;
        for (int row = 0; row < screenRows; row++)
        {
            var line = session.ReadScreenLine(row);
            var trimmed = line.TrimStart();

            // Claude's idle prompt uses "❯" (U+276F). Safe to scan all rows
            // since quoted user messages use ">" (ASCII), not "❯".
            if (trimmed.StartsWith("\u276F"))
            {
                foundIdlePrompt = true;
                idlePromptRow = row;
            }

            // Also check ">" but only on short lines to avoid matching quoted text
            if (trimmed.StartsWith(">") && trimmed.Length <= 3)
            {
                foundIdlePrompt = true;
                idlePromptRow = row;
            }

            // Working indicator: Claude Code's spinner cycles through these
            // star-like glyphs before the status text (e.g. "✳ Ionizing...")
            // Exclude the completion summary (e.g. "✻ Brewed for 5m 43s") which
            // reuses the same glyphs but shows a duration — the verb varies randomly.
            // The working spinner always contains '…' (ellipsis) e.g. "✻ Sketching… (1m 44s · ↓ 269 tokens)"
            // while the completion summary does not (e.g. "✻ Brewed for 5m 43s.").
            if (trimmed.Length > 0 && ClaudeSpinnerGlyphs.Contains(trimmed[0]))
            {
                // Check both Unicode ellipsis '…' (U+2026) and ASCII "..." — Claude Code
                // may emit either depending on the Ink renderer path.
                if (trimmed.Contains('…') || trimmed.Contains("...") || !HasDurationPattern(trimmed))
                    foundWorkingIndicator = true;
                else
                {
                    foundCompletionSummary = true; // e.g. "✻ Churned for 45s" — Claude finished
                    completionSummaryRow = row;
                }
            }

            // Broader detection: Claude's working status bar shows
            // "glyph Verb… (duration · ↓ N tokens · ...)" — detect this even
            // when the leading glyph isn't in our known spinner set (Claude Code
            // may add new spinner characters). The ↓ + ellipsis combo is distinctive.
            if (!foundWorkingIndicator && trimmed.Length > 2
                && !char.IsLetterOrDigit(trimmed[0])
                && trimmed[1] == ' '
                && (trimmed.Contains('…') || trimmed.Contains("..."))
                && trimmed.Contains('\u2193')) // ↓
            {
                foundWorkingIndicator = true;
            }

            // Track numbered options (e.g. "1. Paste from clipboard", "2. Open a new tab")
            // These indicate Claude asked a question with choices.
            // Record the row so we can later check if they're near the idle prompt
            // (Claude places numbered options just above the ❯ prompt).
            // The selected option may have a "> " prefix from Ink's selection cursor.
            var optLine = trimmed;
            if (optLine.StartsWith("> "))
                optLine = optLine.Substring(2);
            if (optLine.Length > 2 && optLine[0] == '1' && optLine[1] == '.')
                foundOption1Row = row;
            if (optLine.Length > 2 && optLine[0] == '2' && optLine[1] == '.')
                foundOption2Row = row;

            // Tool approval / yes-no prompts (scan near cursor only)
            if (Math.Abs(row - cursorRow) <= 5)
            {
                if (trimmed.Contains("Yes") && trimmed.Contains("No"))
                    result = TerminalStatus.WaitingForInput;

                if (trimmed.Contains("Enter to select"))
                    result = TerminalStatus.WaitingForInput;

                // "Esc to cancel" footer appears on tool approval and interactive prompts.
                // Also check near the cursor — when the terminal has more rows than content,
                // the footer won't be at the very bottom of the screen buffer.
                if (trimmed.Contains("Esc to cancel"))
                    result = TerminalStatus.WaitingForInput;
            }

            // "Esc to cancel" footer near the bottom of the screen.
            if (row >= screenRows - 5 && trimmed.Contains("Esc to cancel"))
                result = TerminalStatus.WaitingForInput;

            // "ctrl-g to edit" footer appears in plan mode selection UI
            if (row >= screenRows - 5 && trimmed.Contains("ctrl-g to edit"))
                result = TerminalStatus.WaitingForInput;
        }

        // Only treat numbered options as a question if both "1." and "2." appear
        // near the idle prompt or cursor (within 10 rows). This prevents false positives
        // from numbered lists in regular Claude output.
        // When no idle prompt is found (e.g. plan mode selection UI), fall back to
        // cursor row as anchor.
        // Suppress numbered options that appear ABOVE a completion summary (they're
        // old response text). But options BELOW the summary are from a new interaction
        // (e.g. tool approval after the previous task finished).
        bool completionSuppressesOptions = foundCompletionSummary
            && completionSummaryRow > foundOption1Row;
        if (foundOption1Row >= 0 && foundOption2Row >= 0 && !completionSuppressesOptions)
        {
            if (idlePromptRow >= 0)
            {
                // Options must be within 10 rows above the idle prompt
                foundNumberedOptions = idlePromptRow - foundOption1Row is >= 0 and <= 10
                    && idlePromptRow - foundOption2Row is >= 0 and <= 10;
            }
            else
            {
                // No idle prompt (Ink TUI screens like plan mode) — just verify
                // the options are close to each other (not scattered in output)
                foundNumberedOptions = Math.Abs(foundOption1Row - foundOption2Row) <= 5;
            }
        }

        // WaitingForInput takes priority, then idle prompt with numbered
        // options (Claude asked a question), then plain idle prompt, then Working
        if (result != TerminalStatus.WaitingForInput && foundIdlePrompt && !foundWorkingIndicator)
        {
            if (foundNumberedOptions)
                result = TerminalStatus.WaitingForInput;
            else
                result = TerminalStatus.Idle;
        }

        // Numbered options near cursor without idle prompt (e.g. plan mode selection UI)
        if (result == null && foundNumberedOptions && !foundWorkingIndicator)
            result = TerminalStatus.WaitingForInput;

        if (result == null)
            result = TerminalStatus.Working;

        return result.Value;
    }

    /// <summary>
    /// Returns true if the line contains a duration like "5m 43s", "30s", "1h 2m", etc.
    /// Used to distinguish Claude's completion summary from the working spinner.
    /// </summary>
    private static bool HasDurationPattern(string line)
    {
        // Look for digit(s) followed by 's', 'm', or 'h' (time units)
        for (int i = 0; i < line.Length - 1; i++)
        {
            if (char.IsDigit(line[i]))
            {
                char next = line[i + 1];
                if (next is 's' or 'm' or 'h')
                {
                    // Ensure it's not part of a longer word (check char after unit)
                    if (i + 2 >= line.Length || line[i + 2] == ' ' || char.IsDigit(line[i + 2]))
                        return true;
                }
            }
        }
        return false;
    }

    private static bool IsSubPath(string path, string basePath)
    {
        if (string.IsNullOrEmpty(basePath)) return false;
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedBase = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase);
    }

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
                IconPath = FindBestIcon(folderPath)
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

    private void DeleteAttachment(AttachmentItemViewModel? item)
    {
        if (item == null) return;
        AttachmentService.DeleteAttachment(item.FilePath);

        // Remove from the owning project node
        foreach (var project in Projects)
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

    private void OpenAttachmentsFolder(ProjectNodeViewModel? node)
    {
        if (node == null) return;
        _openAttachmentsWindow?.Invoke(node.Project.Id, node.Name);
    }

    public void StopTimer()
    {
        _refreshTimer.Stop();
    }
}
