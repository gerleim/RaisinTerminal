namespace RaisinTerminal.Core.Terminal;

/// <summary>
/// View-layer scroll state for a single canvas onto a TerminalBuffer.
/// A buffer can be rendered by multiple viewports (e.g. split-pane view)
/// with independent scroll positions.
/// </summary>
public class TerminalViewport
{
    /// <summary>Lines scrolled back from the bottom. 0 = live.</summary>
    public int ScrollOffset { get; set; }

    /// <summary>
    /// True when the user explicitly scrolled back (wheel, Shift+PgUp,
    /// scrollbar). Distinguishes user intent from ScrollOffset auto-bumps
    /// caused by scrollback growth while this viewport was off-screen.
    /// </summary>
    public bool UserScrolledBack { get; set; }

    /// <summary>
    /// When true, new output auto-scrolls this viewport back to the bottom.
    /// A pinned pane in a split view has IsLive=false.
    /// </summary>
    public bool IsLive { get; set; } = true;

    /// <summary>
    /// Number of rows this viewport's canvas can display.
    /// Used during buffer resize to compute the correct scroll offset adjustment.
    /// When 0 (default), the adjustment uses buffer.Rows (full-buffer behavior).
    /// </summary>
    public int CanvasRows { get; set; }
}
