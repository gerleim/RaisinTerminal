using System.Windows;
using RaisinTerminal.Services;

namespace RaisinTerminal.Views;

public partial class AppUpdateWindow : Window
{
    private readonly AppUpdateInfo _updateInfo;

    public AppUpdateWindow(AppUpdateInfo updateInfo)
    {
        InitializeComponent();
        _updateInfo = updateInfo;

        CurrentVersionText.Text = FormatVersion(updateInfo.CurrentVersion);
        NewVersionText.Text = FormatVersion(updateInfo.LatestVersion!);
        StatusText.Text = "A new version of RaisinTerminal is available.";

        if (!string.IsNullOrWhiteSpace(updateInfo.ReleaseNotes))
        {
            ReleaseNotesLabel.Visibility = Visibility.Visible;
            ReleaseNotesScroller.Visibility = Visibility.Visible;
            ReleaseNotesText.Text = updateInfo.ReleaseNotes;
        }
    }

    private async void OnDownloadAndInstall(object sender, RoutedEventArgs e)
    {
        if (_updateInfo.DownloadUrl is null)
        {
            StatusText.Text = "No download available for this release.";
            return;
        }

        DownloadButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        ProgressBar.Visibility = Visibility.Visible;
        StatusText.Text = "Downloading update...";

        var progress = new Progress<double>(pct =>
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = pct;
        });

        var zipPath = await AppUpdateService.DownloadUpdateAsync(_updateInfo.DownloadUrl, progress);

        if (zipPath is null)
        {
            StatusText.Text = "Download failed. Please try again later.";
            DownloadButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
            ProgressBar.Visibility = Visibility.Collapsed;
            return;
        }

        StatusText.Text = "Installing update and restarting...";
        ProgressBar.IsIndeterminate = true;

        if (AppUpdateService.LaunchUpdateAndExit(zipPath))
        {
            Application.Current.Shutdown();
        }
        else
        {
            StatusText.Text = "Failed to launch updater. Please update manually.";
            DownloadButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
            ProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Close();

    private static string FormatVersion(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";
}
