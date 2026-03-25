using RaisinTerminal.Core.Terminal;

namespace RaisinTerminal.Core.Models;

/// <summary>
/// State model for a terminal session.
/// </summary>
public class TerminalSession
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool IsRunning { get; set; }
    public ConPtySession? ConPty { get; set; }
}
