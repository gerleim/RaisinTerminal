using System.Windows;
using System.Windows.Controls;
using AvalonDock.Controls;
using Raisin.WPF.Base;

namespace RaisinTerminal.Views;

public class PaneStyleSelector : StyleSelector
{
    public override Style? SelectStyle(object item, DependencyObject container)
    {
        if (item is ToolWindowViewModel vm)
        {
            var style = new Style(container is LayoutAnchorableItem
                ? typeof(LayoutAnchorableItem)
                : typeof(LayoutDocumentItem));

            style.Setters.Add(new Setter(LayoutItem.TitleProperty,
                new System.Windows.Data.Binding("Title") { Source = vm }));
            style.Setters.Add(new Setter(LayoutItem.ContentIdProperty,
                new System.Windows.Data.Binding("ContentId") { Source = vm }));
            style.Setters.Add(new Setter(LayoutItem.IsActiveProperty,
                new System.Windows.Data.Binding("IsActive") { Source = vm, Mode = System.Windows.Data.BindingMode.TwoWay }));
            style.Setters.Add(new Setter(LayoutItem.CloseCommandProperty,
                new RelayCommand(() =>
                {
                    vm.OnClose();
                    vm.CloseAction?.Invoke();
                })));

            return style;
        }
        return base.SelectStyle(item, container);
    }
}
