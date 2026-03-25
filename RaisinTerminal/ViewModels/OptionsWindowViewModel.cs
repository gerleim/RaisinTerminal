using System.Collections.ObjectModel;
using Raisin.WPF.Base;
using Raisin.WPF.Base.Settings;
using RaisinTerminal.Services;
using RaisinTerminal.Settings;

namespace RaisinTerminal.ViewModels;

public class OptionsWindowViewModel : ViewModelBase
{
    public List<SettingItemViewModel> AllSettings { get; } = [];
    public ObservableCollection<object> DisplayItems { get; } = [];

    private static readonly Func<object> DefaultFactory = () => new AppSettings();

    public OptionsWindowViewModel()
    {
        foreach (var def in TerminalSettingsRegistry.All.OrderBy(d => d.Order))
        {
            var item = SettingItemFactory.TryCreate(def, DefaultFactory);
            if (item is null) continue;

            item.LoadFrom(SettingsService.Current);
            item.UpdateIsModified();
            AllSettings.Add(item);
        }

        RefreshDisplay();
    }

    public void RefreshDisplay()
    {
        DisplayItems.Clear();

        foreach (var category in TerminalSettingsRegistry.CategoryOrder)
        {
            var items = AllSettings.Where(s => s.Category == category).ToList();
            if (items.Count == 0) continue;

            DisplayItems.Add(new CategoryHeaderItem(category));
            foreach (var item in items)
                DisplayItems.Add(item);
        }
    }

    public void Apply()
    {
        var settings = new AppSettings();
        foreach (var item in AllSettings)
            item.ApplyTo(settings);
        SettingsService.Save(settings);

        foreach (var item in AllSettings)
            item.UpdateIsModified();
    }
}
