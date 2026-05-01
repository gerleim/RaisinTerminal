using Raisin.WPF.Base;
using RaisinTerminal.Settings;

namespace RaisinTerminal.ViewModels;

/// <summary>
/// One editable row in the Key Bindings tab. Holds the in-flight chord for a
/// single command. Persists to KeyBindingsService when the parent dialog applies.
/// </summary>
public class KeyBindingItemViewModel : ViewModelBase
{
    public KeyBindingDefinition Definition { get; }

    public string Id => Definition.Id;
    public string DisplayName => Definition.DisplayName;
    public string Description => Definition.Description;
    public string Category => Definition.Category;

    private KeyChord _chord;
    public KeyChord Chord
    {
        get => _chord;
        set
        {
            if (SetProperty(ref _chord, value))
            {
                OnPropertyChanged(nameof(ChordText));
                UpdateIsModified();
            }
        }
    }

    public string ChordText => Chord.IsNone ? "(unbound)" : Chord.ToString();

    private bool _isCapturing;
    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (SetProperty(ref _isCapturing, value))
                OnPropertyChanged(nameof(DisplayText));
        }
    }

    /// <summary>What the capture button shows: either the chord or a prompt while capturing.</summary>
    public string DisplayText => IsCapturing ? "Press a key…" : ChordText;

    private bool _isModified;
    public bool IsModified
    {
        get => _isModified;
        private set => SetProperty(ref _isModified, value);
    }

    public RelayCommand ResetCommand { get; }
    public RelayCommand ClearCommand { get; }

    public KeyBindingItemViewModel(KeyBindingDefinition definition, KeyChord initial)
    {
        Definition = definition;
        _chord = initial;
        ResetCommand = new RelayCommand(() => Chord = Definition.Default);
        ClearCommand = new RelayCommand(() => Chord = KeyChord.None);
        UpdateIsModified();
    }

    private void UpdateIsModified() => IsModified = Chord != Definition.Default;
}
