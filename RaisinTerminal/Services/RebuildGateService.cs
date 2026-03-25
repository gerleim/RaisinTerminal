using System.IO.Pipes;
using System.Text;
using Raisin.WPF.Base;
using RaisinTerminal.ViewModels;
using System.Collections.ObjectModel;

namespace RaisinTerminal.Services;

/// <summary>
/// Hosts a named pipe server that responds to "canRestart" queries from rebuild.bat.
/// Returns "OK" if all terminal sessions are idle, or "BUSY:n" if n sessions are busy.
/// </summary>
public sealed class RebuildGateService : IDisposable
{
    public const string PipeName = "RaisinTerminal.RebuildGate";

    private readonly ObservableCollection<ToolWindowViewModel> _documents;
    private CancellationTokenSource? _cts;

    public RebuildGateService(ObservableCollection<ToolWindowViewModel> documents)
    {
        _documents = documents;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = ListenLoop(_cts.Token);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                var buf = new byte[256];
                int bytesRead = await server.ReadAsync(buf, ct);
                var request = Encoding.UTF8.GetString(buf, 0, bytesRead).Trim();

                string response;
                if (request == "canRestart")
                {
                    int busyCount = CountBusySessions();
                    response = busyCount == 0 ? "OK" : $"BUSY:{busyCount}";
                }
                else if (request == "quit")
                {
                    response = "OK";
                    // Send response before shutting down
                    var quitBytes = Encoding.UTF8.GetBytes(response);
                    await server.WriteAsync(quitBytes, ct);
                    await server.FlushAsync(ct);
                    server.Disconnect();

                    // Gracefully close the main window on the UI thread
                    // This triggers OnClosing → SaveLayout → session IDs are persisted
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        System.Windows.Application.Current.MainWindow?.Close();
                    });
                    return; // Stop the listen loop
                }
                else
                {
                    response = "UNKNOWN";
                }

                var responseBytes = Encoding.UTF8.GetBytes(response);
                await server.WriteAsync(responseBytes, ct);
                await server.FlushAsync(ct);

                server.Disconnect();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Pipe error — brief pause then retry
                try { await Task.Delay(500, ct); } catch { break; }
            }
        }
    }

    private int CountBusySessions()
    {
        int busy = 0;
        // Must access the collection on the UI thread since it's an ObservableCollection
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var doc in _documents)
            {
                if (doc is TerminalSessionViewModel session &&
                    ProjectsPanelViewModel.DetermineStatus(session) == TerminalStatus.Working)
                {
                    // Session is actively working (not just idle at a prompt)
                    busy++;
                }
            }
        });
        return busy;
    }
}
