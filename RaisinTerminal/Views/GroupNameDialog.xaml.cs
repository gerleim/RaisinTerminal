using System.Windows;

namespace RaisinTerminal.Views;

public partial class GroupNameDialog : Window
{
    public string GroupName { get; private set; } = "";

    public GroupNameDialog(string initialName)
    {
        InitializeComponent();
        NameBox.Text = initialName;
        NameBox.SelectAll();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        GroupName = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(GroupName)) return;
        DialogResult = true;
        Close();
    }
}
