using System.Reflection;
using System.Windows;

namespace RaisinTerminal.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {version?.Major}.{version?.Minor}.{version?.Build}";
    }

    private void OnOk(object sender, RoutedEventArgs e) => Close();
}
