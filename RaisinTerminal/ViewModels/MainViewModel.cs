using System.Collections.ObjectModel;
using System.Windows.Input;
using Raisin.WPF.Base;
using Raisin.WPF.Base.Models;
using RaisinTerminal.Core.Helpers;
using RaisinTerminal.Services;

namespace RaisinTerminal.ViewModels;

public class MainViewModel : ViewModelBase, IDisposable
{
    public ObservableCollection<ToolWindowViewModel> Documents { get; } = [];

    public ICommand NewSessionCommand { get; }
    public ICommand NewClaudeSessionCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand OptionsCommand { get; }
    public ICommand ClaudeCodeUpdateCommand { get; }
    public ICommand AboutCommand { get; }
    public ICommand AddProjectCommand => ProjectsPanel.AddProjectCommand;

    public ProjectsPanelViewModel ProjectsPanel { get; }

    private readonly RebuildGateService _rebuildGate;

    public MainViewModel()
    {
        NewSessionCommand = new RelayCommand(CreateNewSession);
        NewClaudeSessionCommand = new RelayCommand(CreateNewClaudeSession);
        ExitCommand = new RelayCommand(() => System.Windows.Application.Current.MainWindow?.Close());
        OptionsCommand = new RelayCommand(ShowOptions);
        ClaudeCodeUpdateCommand = new RelayCommand(ShowClaudeCodeUpdate);
        AboutCommand = new RelayCommand(ShowAbout);
        ProjectsPanel = new ProjectsPanelViewModel(
            Documents,
            createSessionInDirectory: CreateNewSessionInDirectory,
            createClaudeSessionInDirectory: CreateNewClaudeSessionInDirectory,
            activateSession: session => session.IsActive = true,
            openAttachmentsWindow: OpenAttachmentsWindow);

        _rebuildGate = new RebuildGateService(Documents);
        _rebuildGate.Start();
    }

    public void RestoreState(AppLayoutState? state)
    {
        if (state is null) return;

        foreach (var doc in state.Documents)
        {
            if (doc.Type == nameof(TerminalSessionViewModel))
            {
                var session = new TerminalSessionViewModel();
                session.ContentId = doc.ContentId;

                // Restore working directory
                session.WorkingDirectory = doc.WorkingDirectory;

                // Restore Claude session name for resume
                session.ClaudeSessionName = doc.ClaudeSessionName;

                // If a command was running (TUI app or interactive program), set it up for replay
                if (!string.IsNullOrEmpty(doc.LastCommand) && (doc.WasInAlternateScreen || doc.IsCommandRunning))
                {
                    session.RestoreCommand = TransformRestoreCommand(doc.LastCommand, doc.ClaudeSessionName);
                }

                session.CloseAction = () =>
                {
                    Documents.Remove(session);
                    session.OnClose();
                };
                session.GenerateClaudeName = () => GenerateClaudeNameForSession(session);

                // Start the session eagerly with default dimensions so that
                // restore commands run immediately — not just when the tab becomes visible.
                // The view will resize to actual dimensions when activated.
                session.StartSession(120, 30);

                Documents.Add(session);
            }
        }
    }

    public ToolWindowViewModel? ResolveContent(string contentId)
    {
        foreach (var doc in Documents)
        {
            if (doc.ContentId == contentId)
                return doc;
        }
        return null;
    }

    /// <summary>
    /// Rewrites a command for restore. For Claude CLI, appends --resume with the session name
    /// to resume the exact conversation. Falls back to --continue if no session name is available.
    /// </summary>
    private static string TransformRestoreCommand(string command, string? claudeSessionName)
    {
        var trimmed = command.Trim();

        // Match "claude" bare or "claude <args>" but not if already has --continue or --resume
        if (trimmed.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("claude ", StringComparison.OrdinalIgnoreCase))
        {
            if (!trimmed.Contains("--continue", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.Contains("--resume", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(claudeSessionName))
                    return trimmed + " --resume \"" + claudeSessionName + "\" --name \"" + claudeSessionName + "\"";
                return trimmed + " --continue";
            }
        }

        return command;
    }

    private void OpenAttachmentsWindow(string projectId, string projectName)
    {
        // Check if an attachments pane for this project already exists
        var contentId = $"attachments-{projectId}";
        foreach (var doc in Documents)
        {
            if (doc is ImageGalleryViewModel existing && existing.ContentId == contentId)
            {
                existing.Refresh();
                existing.IsActive = true;
                return;
            }
        }

        var vm = new ImageGalleryViewModel(projectId, projectName);
        vm.CloseAction = () =>
        {
            Documents.Remove(vm);
            vm.OnClose();
        };
        Documents.Add(vm);
        vm.IsActive = true;
    }

    private TerminalSessionViewModel AddSession(
        string? workingDir = null, string? claudeSessionName = null, string? restoreCommand = null)
    {
        var session = new TerminalSessionViewModel();
        if (workingDir != null)
            session.WorkingDirectory = workingDir;
        if (claudeSessionName != null)
            session.ClaudeSessionName = claudeSessionName;
        if (restoreCommand != null)
            session.RestoreCommand = restoreCommand;
        session.CloseAction = () =>
        {
            Documents.Remove(session);
            session.OnClose();
        };
        session.GenerateClaudeName = () => GenerateClaudeNameForSession(session);
        Documents.Add(session);
        session.IsActive = true;
        return session;
    }

    private void CreateNewSession() => AddSession();

    private void CreateNewClaudeSession()
    {
        // Find the active terminal session's working directory
        string? workingDir = null;
        foreach (var doc in Documents)
        {
            if (doc is TerminalSessionViewModel session && session.IsActive &&
                !string.IsNullOrEmpty(session.WorkingDirectory))
            {
                workingDir = session.WorkingDirectory;
                break;
            }
        }

        // Match to a project to determine the project name and home path
        string? projectHomePath = null;
        string? projectName = null;
        if (!string.IsNullOrEmpty(workingDir))
        {
            projectHomePath = workingDir;
            projectName = System.IO.Path.GetFileName(workingDir);

            // Check if ProjectsPanel has a matching project with a better home path
            foreach (var projectNode in ProjectsPanel.Projects)
            {
                var homePath = projectNode.HomePath;
                if (!string.IsNullOrEmpty(homePath) &&
                    PathHelper.IsSubPath(workingDir, homePath))
                {
                    projectHomePath = homePath;
                    projectName = projectNode.Name;
                    break;
                }
            }
        }

        var sessionName = GenerateUniqueClaudeName(projectName);
        AddSession(workingDir: projectHomePath, claudeSessionName: sessionName,
            restoreCommand: $"claude --name \"{sessionName}\"");
    }

    private string? GetProjectNameForDirectory(string? workingDir)
    {
        if (string.IsNullOrEmpty(workingDir))
            return null;

        foreach (var projectNode in ProjectsPanel.Projects)
        {
            var homePath = projectNode.HomePath;
            if (!string.IsNullOrEmpty(homePath) &&
                PathHelper.IsSubPath(workingDir, homePath))
            {
                return projectNode.Name;
            }
        }

        return System.IO.Path.GetFileName(workingDir);
    }

    private string GenerateClaudeNameForSession(TerminalSessionViewModel session)
    {
        var projectName = GetProjectNameForDirectory(session.WorkingDirectory);
        return GenerateUniqueClaudeName(projectName);
    }

    private string GenerateUniqueClaudeName(string? projectName)
    {
        // Build abbreviated prefix from project name
        var allProjectNames = new List<string>();
        foreach (var p in ProjectsPanel.Projects)
            allProjectNames.Add(p.Name);

        var prefix = !string.IsNullOrEmpty(projectName)
            ? ProjectNameHelper.AbbreviateWithDisambiguation(projectName, allProjectNames)
            : "S";

        // Collect existing Claude session names
        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in Documents)
        {
            if (doc is TerminalSessionViewModel session && !string.IsNullOrEmpty(session.ClaudeSessionName))
                existingNames.Add(session.ClaudeSessionName);
        }

        // Find next available number: "prefix 1", "prefix 2", ...
        for (int i = 1; ; i++)
        {
            var candidate = ProjectNameHelper.GenerateSessionName(prefix, i);
            if (!existingNames.Contains(candidate))
                return candidate;
        }
    }

    public void CreateNewSessionInDirectory(string path) => AddSession(workingDir: path);

    public void CreateNewClaudeSessionInDirectory(string path)
    {
        var projectName = GetProjectNameForDirectory(path);
        var sessionName = GenerateUniqueClaudeName(projectName);
        AddSession(workingDir: path, claudeSessionName: sessionName,
            restoreCommand: $"claude --name \"{sessionName}\"");
    }

    private static void ShowOptions()
    {
        var window = new Views.OptionsWindow();
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();
    }

    private static void ShowClaudeCodeUpdate()
    {
        var window = new Views.ClaudeCodeUpdateWindow();
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();
    }

    private static void ShowAbout()
    {
        var window = new Views.AboutWindow();
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();
    }

    public void Dispose()
    {
        _rebuildGate.Dispose();
    }
}
