using System.Windows;
using System.Windows.Controls;
using Raisin.WPF.Base.Settings;
using RaisinTerminal.ViewModels;

namespace RaisinTerminal.Views;

/// <summary>
/// Picks the right template for the Key Bindings tab: a category header for
/// <see cref="CategoryHeaderItem"/> entries, and a row for
/// <see cref="KeyBindingItemViewModel"/> entries.
/// </summary>
public class KeyBindingDisplayItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? CategoryHeaderTemplate { get; set; }
    public DataTemplate? RowTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        CategoryHeaderItem => CategoryHeaderTemplate,
        KeyBindingItemViewModel => RowTemplate,
        _ => base.SelectTemplate(item, container),
    };
}
