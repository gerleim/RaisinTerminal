using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Raisin.EventSystem;
using RaisinTerminal.Core.Terminal;
using RaisinTerminal.Services;

namespace RaisinTerminal.Views;

public partial class TerminalView
{
    private bool _overlayActive;
    private bool _lastAlternateScreen;

    private static readonly byte[] BracketedPasteStart = { 0x1B, (byte)'[', (byte)'2', (byte)'0', (byte)'0', (byte)'~' };
    private static readonly byte[] BracketedPasteEnd = { 0x1B, (byte)'[', (byte)'2', (byte)'0', (byte)'1', (byte)'~' };

    private void ShowOverlay()
    {
        App.Events.Log(this, "Overlay shown (alternate screen entered)", category: "Terminal");
        _overlayActive = true;
        InputOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => OverlayInput.Focus(), DispatcherPriority.Input);
    }

    private void HideOverlay()
    {
        App.Events.Log(this, "Overlay hidden (alternate screen exited)", category: "Terminal");
        _overlayActive = false;
        InputOverlay.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(() => Canvas.Focus(), DispatcherPriority.Input);
    }

    private void HandleOverlayKeyDown(KeyEventArgs e)
    {
        if (_vm == null) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (e.Key == Key.Enter && !shift && !ctrl)
        {
            SubmitOverlayInput();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && (shift || ctrl))
        {
            OverlayInput.SelectedText = "\n";
            OverlayInput.CaretIndex = OverlayInput.SelectionStart + 1;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            OverlayInput.Clear();
            _vm.WriteUserInput(InputEncoder.EncodeKey(ConsoleKey.Escape));
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.Up || e.Key == Key.Down) && string.IsNullOrEmpty(OverlayInput.Text))
        {
            var key = e.Key == Key.Up ? ConsoleKey.UpArrow : ConsoleKey.DownArrow;
            _vm.WriteUserInput(InputEncoder.EncodeKey(key, ctrl, shift: shift));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab)
        {
            _vm.WriteUserInput(InputEncoder.EncodeKey(ConsoleKey.Tab, shift: shift));
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.C)
        {
            OverlayInput.Clear();
            _vm.WriteUserInput(new byte[] { 0x03 });
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.V)
        {
            if (Clipboard.ContainsText())
            {
                OverlayInput.SelectedText = Clipboard.GetText();
            }
            e.Handled = true;
            return;
        }
    }

    private void SubmitOverlayInput()
    {
        if (_vm == null) return;

        var text = OverlayInput.Text;

        if (!string.IsNullOrEmpty(text))
        {
            if (text.Contains('\n') && _vm.Emulator?.BracketedPasteMode == true)
            {
                _vm.WriteUserInput(BracketedPasteStart);
                _vm.WriteUserInput(InputEncoder.EncodeText(text));
                _vm.WriteUserInput(BracketedPasteEnd);
            }
            else
            {
                _vm.WriteUserInput(InputEncoder.EncodeText(text));
            }

            if (!(_vm.Emulator?.AlternateScreen ?? false))
            {
                CommandHistoryService.Instance.Add(text);
                _vm.LastCommand = text;
            }
        }

        _vm.WriteUserInput(new byte[] { (byte)'\r' });

        OverlayInput.Clear();
    }
}
