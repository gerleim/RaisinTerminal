using RaisinTerminal.Core.Models;

namespace RaisinTerminal.Core.Terminal;

/// <summary>
/// Spawns and tracks multiple Claude CLI sessions.
/// </summary>
public class ClaudeCliManager : IDisposable
{
    private readonly List<TerminalSession> _sessions = [];
    private readonly object _lock = new();
    private bool _disposed;

    public IReadOnlyList<TerminalSession> Sessions
    {
        get { lock (_lock) return _sessions.ToList(); }
    }

    public event Action<TerminalSession>? SessionStarted;
    public event Action<TerminalSession>? SessionEnded;

    public TerminalSession CreateSession(string? workingDirectory = null, int cols = 120, int rows = 30)
    {
        var session = new TerminalSession
        {
            Id = Guid.NewGuid(),
            Title = "Claude",
            WorkingDirectory = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            CreatedAt = DateTime.UtcNow
        };

        var conPty = new ConPtySession();
        conPty.Start("cmd.exe /c claude", cols, rows, workingDirectory);
        conPty.Exited += (_, _) =>
        {
            session.IsRunning = false;
            SessionEnded?.Invoke(session);
        };

        session.ConPty = conPty;
        session.IsRunning = true;

        lock (_lock) _sessions.Add(session);
        SessionStarted?.Invoke(session);

        return session;
    }

    public void CloseSession(Guid id)
    {
        TerminalSession? session;
        lock (_lock)
        {
            session = _sessions.FirstOrDefault(s => s.Id == id);
            if (session != null) _sessions.Remove(session);
        }
        session?.ConPty?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        List<TerminalSession> snapshot;
        lock (_lock) { snapshot = _sessions.ToList(); _sessions.Clear(); }
        foreach (var s in snapshot)
            s.ConPty?.Dispose();
    }
}
