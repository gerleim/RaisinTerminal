using System.Windows;
using RaisinTerminal.Services;

namespace RaisinTerminal.Views;

public partial class ClaudeCodeUpdateWindow : Window
{
    private ClaudeCodeVersionInfo? _versionInfo;

    public ClaudeCodeUpdateWindow()
    {
        InitializeComponent();
        InstalledVersionText.Text = "—";
        LatestVersionText.Text = "—";
        StatusText.Text = "Click 'Check for Updates' to get version information.";
    }

    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Checking versions...");

        _versionInfo = await ClaudeCodeUpdateService.CheckVersionsAsync();

        InstalledVersionText.Text = _versionInfo.InstalledVersion ?? "Not found";
        LatestVersionText.Text = _versionInfo.LatestVersion ?? "Not found";

        if (_versionInfo.InstalledVersion is null)
        {
            StatusText.Text = "Claude Code CLI is not installed. Install with: npm install -g @anthropic-ai/claude-code";
            InstalledVersionText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xFF, 0x66, 0x66));
        }
        else if (_versionInfo.IsUpdateAvailable)
        {
            StatusText.Text = "A newer version is available. Click 'Update' to install it.";
            LatestVersionText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x66, 0xFF, 0x66));
        }
        else if (_versionInfo.LatestVersion is null)
        {
            StatusText.Text = "Could not determine the latest version.";
        }
        else
        {
            StatusText.Text = "You are running the latest version.";
        }

        UpdateButton.IsEnabled = _versionInfo.IsUpdateAvailable;
        SetBusy(false);
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Updating Claude Code CLI... This may take a moment.");
        UpdateButton.IsEnabled = false;

        var (success, output) = await ClaudeCodeUpdateService.UpdateAsync();

        if (success)
        {
            var newVersion = await ClaudeCodeUpdateService.GetInstalledVersionAsync();
            InstalledVersionText.Text = newVersion ?? "Unknown";
            InstalledVersionText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x66, 0xFF, 0x66));
            StatusText.Text = "Update completed successfully.";
            UpdateButton.IsEnabled = false;
        }
        else
        {
            StatusText.Text = $"Update failed: {output}";
            UpdateButton.IsEnabled = true;
        }

        SetBusy(false);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy, string? message = null)
    {
        CheckButton.IsEnabled = !busy;
        if (busy)
            UpdateButton.IsEnabled = false;
        if (message is not null)
            StatusText.Text = message;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }
}
