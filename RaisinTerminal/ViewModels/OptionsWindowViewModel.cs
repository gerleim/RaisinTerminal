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
    public List<KeyBindingItemViewModel> KeyBindings { get; } = [];
    public ObservableCollection<object> KeyBindingDisplayItems { get; } = [];

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

        foreach (var def in KeyBindingsRegistry.All.OrderBy(d => d.Order))
            KeyBindings.Add(new KeyBindingItemViewModel(def, KeyBindingsService.Get(def.Id)));

        RefreshDisplay();
        RefreshKeyBindingDisplay();
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

    /// <summary>
    /// Builds a flat list of category headers + binding rows for the Key Bindings
    /// tab, mirroring the layout used by the General tab.
    /// </summary>
    private void RefreshKeyBindingDisplay()
    {
        KeyBindingDisplayItems.Clear();

        foreach (var category in KeyBindingsRegistry.CategoryOrder)
        {
            var items = KeyBindings.Where(b => b.Category == category).ToList();
            if (items.Count == 0) continue;

            KeyBindingDisplayItems.Add(new CategoryHeaderItem(category));
            foreach (var item in items)
                KeyBindingDisplayItems.Add(item);
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

        var bindings = new Dictionary<string, KeyChord>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in KeyBindings)
            bindings[item.Id] = item.Chord;
        KeyBindingsService.Save(bindings);
    }
}
