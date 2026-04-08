using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using AvalonDock.Themes;
using Raisin.WPF.Base;
using RaisinTerminal.Services;
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkWindowHelper.Apply(this);
        DockingManager.Theme = new Vs2013DarkTheme();

        _restoredState = LayoutService.LoadState();
        if (_restoredState is not null)
            LayoutService.RestoreWindowPlacement(this, _restoredState);
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
