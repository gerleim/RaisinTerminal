using System.IO;
using System.Text.Json;
using System.Windows;
using AvalonDock;
using Raisin.Core;
using Raisin.WPF.Base;
using Raisin.WPF.Base.Models;
using RaisinTerminal.Core.Helpers;

namespace RaisinTerminal.Services;

public static class LayoutService
{
    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RaisinTerminal");

    private static string StatePath => Path.Combine(DataDir, "layout-state.json");
    private static string DockLayoutPath => Path.ChangeExtension(StatePath, ".xml");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void SaveLayout(DockingManager dockingManager, ViewModels.MainViewModel viewModel, Window mainWindow)
    {
        try
        {
            viewModel.ProjectsPanel.SaveState();
            DockLayoutHelper.SaveDockLayout(dockingManager, DockLayoutPath);

            var state = new AppLayoutState();
            var wp = WindowPlacementHelper.Capture(mainWindow);
            state.WindowLeft = wp.Left;
            state.WindowTop = wp.Top;
            state.WindowWidth = wp.Width;
            state.WindowHeight = wp.Height;
            state.WindowMaximized = wp.Maximized;

            var savedIds = new HashSet<string>();
            foreach (var doc in viewModel.Documents)
            {
                if (doc is ViewModels.TerminalSessionViewModel session
                    && session.IsConnected && savedIds.Add(doc.ContentId))
                {
                    session.UpdateWorkingDirectoryFromProcess();
                    var childName = session.RunningChildName;
                    var isRunning = childName != null;
                    // When a child process is running, use its name as the restore command
                    // (more reliable than input tracking which can capture stale/wrong commands)
                    var lastCommand = isRunning ? childName! : session.LastCommand;

                    // For Claude sessions, extract the session name from the tab title.
                    // After /rename, Claude sets the title to "<status-glyph> <session-name>".
                    // Default unnamed sessions show "Claude Code" — skip those.
                    if (isRunning && string.Equals(childName, "claude", StringComparison.OrdinalIgnoreCase))
                    {
                        var title = doc.Title;
                        if (!string.IsNullOrEmpty(title))
                        {
                            var cleanTitle = ClaudeTitleHelper.StripDedupSuffix(title);
                            var parsedName = ClaudeTitleHelper.ExtractSessionName(cleanTitle);
                            if (parsedName != null &&
                                !parsedName.Equals("Claude Code", StringComparison.OrdinalIgnoreCase))
                            {
                                session.ClaudeSessionName = parsedName;
                            }
                        }
                    }

                    state.Documents.Add(new DocumentState
                    {
                        Type = nameof(ViewModels.TerminalSessionViewModel),
                        ContentId = doc.ContentId,
                        WorkingDirectory = session.WorkingDirectory,
                        LastCommand = lastCommand ?? "",
                        WasInAlternateScreen = session.IsInAlternateScreen,
                        IsCommandRunning = isRunning,
                        ClaudeSessionName = session.ClaudeSessionName,
                    });
                }
            }

            var json = JsonSerializer.Serialize(state, JsonOptions);
            SafeFile.WriteAllText(StatePath, json);
        }
        catch { }
    }

    public static AppLayoutState? LoadState()
    {
        try
        {
            if (!File.Exists(StatePath))
                return null;
            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<AppLayoutState>(json);
        }
        catch { return null; }
    }

    public static void RestoreWindowPlacement(Window window, AppLayoutState state)
    {
        var p = WindowPlacementHelper.FromNullable(
            state.WindowLeft, state.WindowTop, state.WindowWidth, state.WindowHeight, state.WindowMaximized);
        if (p is not null)
            WindowPlacementHelper.Restore(window, p);
    }

    public static bool RestoreDockLayout(DockingManager dockingManager, Func<string, object?> contentResolver)
    {
        try
        {
            return DockLayoutHelper.RestoreDockLayout(dockingManager, contentResolver, DockLayoutPath);
        }
        catch { return false; }
    }
}
