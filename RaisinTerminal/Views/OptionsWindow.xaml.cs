using System.Windows;
using RaisinTerminal.ViewModels;

namespace RaisinTerminal.Views;

public partial class OptionsWindow : Window
{
    private OptionsWindowViewModel ViewModel => (OptionsWindowViewModel)DataContext;

    public OptionsWindow()
    {
        DataContext = new OptionsWindowViewModel();
        InitializeComponent();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        ViewModel.Apply();
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
