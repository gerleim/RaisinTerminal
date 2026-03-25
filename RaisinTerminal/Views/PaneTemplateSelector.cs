using System.Windows;
using System.Windows.Controls;
using RaisinTerminal.ViewModels;

namespace RaisinTerminal.Views;

public class PaneTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TerminalTemplate { get; set; }
    public DataTemplate? ImageGalleryTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        return item switch
        {
            TerminalSessionViewModel => TerminalTemplate,
            ImageGalleryViewModel => ImageGalleryTemplate,
            _ => base.SelectTemplate(item, container)
        };
    }
}
