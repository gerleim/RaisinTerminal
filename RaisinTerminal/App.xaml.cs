using System.IO;
using System.Windows;
using Raisin.Core;
using Raisin.EventSystem;
using RaisinTerminal.Services;

namespace RaisinTerminal;

public partial class App : Application
{
    private static Mutex? _mutex;
    private static FileLogger? _fileLogger;

    public static EventSystem Events { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "RaisinTerminal_SingleInstance";
        _mutex = new Mutex(true, mutexName, out bool createdNew);

        if (!createdNew)
        {
            MessageBox.Show("RaisinTerminal is already running.", "RaisinTerminal",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RaisinTerminal", "logs", "terminal.log");
        _fileLogger = new FileLogger(Events, logPath);
        Events.Log(this, "RaisinTerminal started", category: "App");

        SettingsService.CleanupOldAttachments();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Events.Log(this, "RaisinTerminal exiting", category: "App");
        _fileLogger?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
