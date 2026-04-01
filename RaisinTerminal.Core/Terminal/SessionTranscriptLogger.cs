using System.Text;

namespace RaisinTerminal.Core.Terminal;

/// <summary>
/// Manages per-session transcript files: a raw byte log (.raw) and a text-only
/// transcript (.txt). Both are append-only and survive /clear commands and app restarts.
/// </summary>
public class SessionTranscriptLogger : IDisposable
{
    private readonly FileStream _rawStream;
    private readonly StreamWriter _textWriter;
    private readonly object _lock = new();
    private bool _disposed;

    public SessionTranscriptLogger(string sessionsDir, string contentId)
    {
        Directory.CreateDirectory(sessionsDir);

        var rawPath = Path.Combine(sessionsDir, $"{contentId}.raw");
        var textPath = Path.Combine(sessionsDir, $"{contentId}.txt");

        _rawStream = new FileStream(rawPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096);
        _textWriter = new StreamWriter(
            new FileStream(textPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096),
            Encoding.UTF8) { AutoFlush = false };
    }

    public void WriteRaw(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _rawStream.Write(buffer, offset, count);
        }
    }

    public void WriteText(char c)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _textWriter.Write(c);
        }
    }

    public void WriteTextNewline()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _textWriter.WriteLine();
            _textWriter.Flush();
        }
    }

    public void WriteMarker(string label)
    {
        var marker = $"\n--- {label}: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---\n";
        var markerBytes = Encoding.UTF8.GetBytes(marker);

        lock (_lock)
        {
            if (_disposed) return;
            _rawStream.Write(markerBytes, 0, markerBytes.Length);
            _rawStream.Flush();
            _textWriter.Write(marker);
            _textWriter.Flush();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _rawStream.Flush();
            _rawStream.Close();
            _textWriter.Flush();
            _textWriter.Close();
        }
    }
}
