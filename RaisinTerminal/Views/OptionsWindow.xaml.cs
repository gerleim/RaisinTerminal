using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RaisinTerminal.Settings;
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

    /// <summary>Click on a binding's chord button enters capture mode.</summary>
    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is KeyBindingItemViewModel item)
        {
            item.IsCapturing = true;
            btn.Focus();
        }
    }

    /// <summary>
    /// While in capture mode, the next non-modifier keystroke becomes the chord.
    /// Esc cancels without changing the binding. Tab and other navigation keys
    /// are captured too — the user explicitly opted in by clicking the button.
    /// </summary>
    private void OnCaptureKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not KeyBindingItemViewModel item) return;
        if (!item.IsCapturing) return;

        var key = e.Key == Key.System ? e.SystemKey
                : e.Key == Key.ImeProcessed ? e.ImeProcessedKey
                : e.Key;

        // Wait for a non-modifier key — modifier-only presses are part of the chord, not the chord itself.
        if (IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            // Cancel capture without changing the binding.
            item.IsCapturing = false;
            e.Handled = true;
            return;
        }

        item.Chord = new KeyChord(key, Keyboard.Modifiers);
        item.IsCapturing = false;
        e.Handled = true;
    }

    private void OnCaptureLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is KeyBindingItemViewModel item)
            item.IsCapturing = false;
    }

    private static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin
        or Key.System;
}
