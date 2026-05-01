using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using AvalonDock.Themes;
using Raisin.WPF.Base;
using RaisinTerminal.Services;
using RaisinTerminal.Settings;
using RaisinTerminal.ViewModels;
using RaisinTerminal.Views;

namespace RaisinTerminal;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private Raisin.WPF.Base.Models.AppLayoutState? _restoredState;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        _viewModel.ProjectsPanel.PropertyChanged += ProjectsPanel_PropertyChanged;
    }

    public static bool IsUserResizing { get; private set; }
    public static event Action? WindowResizeCompleted;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkWindowHelper.Apply(this);
        DockingManager.Theme = new Vs2013DarkTheme();

        var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        hwndSource?.AddHook(WndProc);

        _restoredState = LayoutService.LoadState();
        if (_restoredState is not null)
            LayoutService.RestoreWindowPlacement(this, _restoredState);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;

        if (msg == WM_ENTERSIZEMOVE)
        {
            IsUserResizing = true;
        }
        else if (msg == WM_EXITSIZEMOVE)
        {
            IsUserResizing = false;
            WindowResizeCompleted?.Invoke();
        }
        return IntPtr.Zero;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (_restoredState is not null)
        {
            _viewModel.RestoreState(_restoredState);
            LayoutService.RestoreDockLayout(DockingManager, contentId => _viewModel.ResolveContent(contentId));
        }

        DockingManager.DocumentsSource = _viewModel.Documents;

        _restoredState = null;

        SyncPanelColumnWidth();
        CheckForAppUpdateAsync();
    }

    private async void CheckForAppUpdateAsync()
    {
        var info = await AppUpdateService.CheckForUpdateAsync();
        if (info.IsUpdateAvailable)
        {
            var window = new AppUpdateWindow(info) { Owner = this };
            window.ShowDialog();
        }
    }

    /// <summary>
    /// Window-level dispatcher for user-rebindable commands. Resolves the chord
    /// against the Window scope of <see cref="KeyBindingsService"/> and invokes the
    /// matching ViewModel command. Handlers run in the tunneling phase so they fire
    /// regardless of which control currently has focus.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        var key = e.Key == Key.System ? e.SystemKey
                : e.Key == Key.ImeProcessed ? e.ImeProcessedKey
                : e.Key;

        var commandId = KeyBindingsService.TryResolve(key, Keyboard.Modifiers, KeyBindingScope.Window);
        if (commandId == null) return;

        ICommand? command = commandId switch
        {
            KeyBindingIds.NewSession => _viewModel.NewSessionCommand,
            KeyBindingIds.NewClaudeSession => _viewModel.NewClaudeSessionCommand,
            KeyBindingIds.ToggleSplitView => _viewModel.ToggleSplitViewCommand,
            KeyBindingIds.ClearScrollback => _viewModel.ClearScrollbackCommand,
            _ => null,
        };

        if (command != null && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // Force repaint all terminal canvases — WPF may not
        // invalidate custom OnRender elements after monitor sleep/wake.
        foreach (var doc in _viewModel.Documents)
        {
            if (doc is TerminalSessionViewModel session)
                session.RequestRepaint();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.ProjectsPanel.StopTimer();
        _viewModel.Dispose();
        LayoutService.SaveLayout(DockingManager, _viewModel, this);
        SettingsService.Save(SettingsService.Current);
        SessionDimensionsService.Save(_viewModel.Documents.Select(d => d.ContentId));
        base.OnClosing(e);
    }

    private void ProjectsPanel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectsPanelViewModel.IsPanelVisible))
            SyncPanelColumnWidth();
    }

    private void SyncPanelColumnWidth()
    {
        if (_viewModel.ProjectsPanel.IsPanelVisible)
        {
            PanelColumn.Width = new GridLength(_viewModel.ProjectsPanel.PanelWidth, GridUnitType.Pixel);
            PanelColumn.MinWidth = 150;
        }
        else
        {
            PanelColumn.Width = new GridLength(0);
            PanelColumn.MinWidth = 0;
        }
    }

    private void PanelSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _viewModel.ProjectsPanel.PanelWidth = PanelColumn.ActualWidth;
    }
}
