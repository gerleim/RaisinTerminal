namespace RaisinTerminal.Settings;

/// <summary>
/// Where the binding is dispatched. Window-scoped bindings fire from MainWindow
/// and stay live even when no terminal is focused; Terminal-scoped bindings fire
/// from the focused TerminalView.
/// </summary>
public enum KeyBindingScope { Window, Terminal }

/// <summary>
/// Declarative description of a user-rebindable command.
/// </summary>
public record KeyBindingDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public required KeyChord Default { get; init; }
    public KeyBindingScope Scope { get; init; } = KeyBindingScope.Terminal;
    public int Order { get; init; }
}

public static class KeyBindingIds
{
    // Window
    public const string NewSession = "window.new-session";
    public const string NewClaudeSession = "window.new-claude-session";
    public const string ToggleSplitView = "window.toggle-split";
    public const string ClearScrollback = "terminal.clear-scrollback";

    // View
    public const string ToggleSearch = "view.toggle-search";
    public const string ScrollPageUp = "view.scroll-page-up";
    public const string ScrollPageDown = "view.scroll-page-down";

    // Edit
    public const string UndoInput = "edit.undo-input";
    public const string RedoInput = "edit.redo-input";
    public const string CopySelection = "edit.copy-selection";
    public const string Paste = "edit.paste";
    public const string SelectInputLine = "edit.select-input-line";
}

public static class KeyBindingsRegistry
{
    public static readonly string[] CategoryOrder = ["Window", "View", "Edit"];

    public static readonly List<KeyBindingDefinition> All =
    [
        // ── Window ──────────────────────────────────────────────────────────
        new()
        {
            Id = KeyBindingIds.NewSession,
            DisplayName = "New terminal",
            Description = "Open a new terminal tab using the default shell.",
            Category = "Window",
            Scope = KeyBindingScope.Window,
            Default = new KeyChord(System.Windows.Input.Key.N, System.Windows.Input.ModifierKeys.Control),
            Order = 0,
        },
        new()
        {
            Id = KeyBindingIds.NewClaudeSession,
            DisplayName = "New Claude terminal",
            Description = "Open a new terminal that auto-launches the Claude CLI in the active project's directory.",
            Category = "Window",
            Scope = KeyBindingScope.Window,
            Default = new KeyChord(System.Windows.Input.Key.N,
                System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift),
            Order = 1,
        },
        new()
        {
            Id = KeyBindingIds.ToggleSplitView,
            DisplayName = "Toggle split view",
            Description = "Show or hide the pinned pane above the live terminal. The pinned pane scrolls independently and can be used to keep earlier output in view.",
            Category = "Window",
            Scope = KeyBindingScope.Window,
            Default = new KeyChord(System.Windows.Input.Key.E,
                System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift),
            Order = 2,
        },
        new()
        {
            Id = KeyBindingIds.ClearScrollback,
            DisplayName = "Clear scrollback",
            Description = "Drop all saved scrollback lines from the active terminal. The visible screen is left untouched.",
            Category = "Window",
            Scope = KeyBindingScope.Window,
            Default = new KeyChord(System.Windows.Input.Key.K,
                System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift),
            Order = 3,
        },

        // ── View ────────────────────────────────────────────────────────────
        new()
        {
            Id = KeyBindingIds.ToggleSearch,
            DisplayName = "Find in buffer",
            Description = "Open or close the search bar to find text in the buffer and scrollback.",
            Category = "View",
            Default = new KeyChord(System.Windows.Input.Key.F, System.Windows.Input.ModifierKeys.Control),
            Order = 0,
        },
        new()
        {
            Id = KeyBindingIds.ScrollPageUp,
            DisplayName = "Scroll page up",
            Description = "Scroll the viewport one page towards the top of the scrollback.",
            Category = "View",
            Default = new KeyChord(System.Windows.Input.Key.PageUp, System.Windows.Input.ModifierKeys.Shift),
            Order = 1,
        },
        new()
        {
            Id = KeyBindingIds.ScrollPageDown,
            DisplayName = "Scroll page down",
            Description = "Scroll the viewport one page towards the bottom of the scrollback.",
            Category = "View",
            Default = new KeyChord(System.Windows.Input.Key.PageDown, System.Windows.Input.ModifierKeys.Shift),
            Order = 2,
        },

        // ── Edit ────────────────────────────────────────────────────────────
        new()
        {
            Id = KeyBindingIds.UndoInput,
            DisplayName = "Undo input",
            Description = "Revert the last typed character or paste in the current input line.",
            Category = "Edit",
            Default = new KeyChord(System.Windows.Input.Key.Z, System.Windows.Input.ModifierKeys.Control),
            Order = 0,
        },
        new()
        {
            Id = KeyBindingIds.RedoInput,
            DisplayName = "Redo input",
            Description = "Reapply an input change that was undone with Undo input.",
            Category = "Edit",
            Default = new KeyChord(System.Windows.Input.Key.Y, System.Windows.Input.ModifierKeys.Control),
            Order = 1,
        },
        new()
        {
            Id = KeyBindingIds.CopySelection,
            DisplayName = "Copy selection",
            Description = "Copy the current text selection to the clipboard. When there is no selection the keystroke falls through to the terminal (e.g. Ctrl+C sends interrupt).",
            Category = "Edit",
            Default = new KeyChord(System.Windows.Input.Key.C, System.Windows.Input.ModifierKeys.Control),
            Order = 2,
        },
        new()
        {
            Id = KeyBindingIds.Paste,
            DisplayName = "Paste",
            Description = "Paste clipboard contents into the terminal. Images become saved file paths; text is pasted as-is (with bracketed paste markers when the program supports it).",
            Category = "Edit",
            Default = new KeyChord(System.Windows.Input.Key.V, System.Windows.Input.ModifierKeys.Control),
            Order = 3,
        },
        new()
        {
            Id = KeyBindingIds.SelectInputLine,
            DisplayName = "Select input line",
            Description = "Select the text currently typed at the shell prompt. Has no effect inside TUI applications (alternate screen).",
            Category = "Edit",
            Default = new KeyChord(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Control),
            Order = 4,
        },
    ];
}
