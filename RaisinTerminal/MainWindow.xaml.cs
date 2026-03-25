using System.ComponentModel;
using System.Windows;
using AvalonDock.Themes;
using Raisin.WPF.Base;
using RaisinTerminal.Services;
using RaisinTerminal.ViewModels;

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
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.ProjectsPanel.StopTimer();
        _viewModel.Dispose();
        LayoutService.SaveLayout(DockingManager, _viewModel, this);
        base.OnClosing(e);
    }
}
