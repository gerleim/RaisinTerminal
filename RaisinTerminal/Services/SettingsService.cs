using System.IO;
using System.Text.Json;

namespace RaisinTerminal.Services;

public class AppSettings
{
    public bool AutoDeleteAttachments { get; set; }
    public int AutoDeleteAttachmentsDays { get; set; } = 30;
    public bool CompressEmptyLines { get; set; } = true;
}

public static class SettingsService
{
    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RaisinTerminal");

    private static string SettingsPath => Path.Combine(DataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static AppSettings? _current;

    public static AppSettings Current => _current ??= Load();

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return _current = new AppSettings();
            var json = File.ReadAllText(SettingsPath);
            return _current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch { return _current = new AppSettings(); }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            _current = settings;
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(SettingsPath))
                File.Replace(tempPath, SettingsPath, destinationBackupFileName: null);
            else
                File.Move(tempPath, SettingsPath);
        }
        catch { }
    }

    /// <summary>
    /// Deletes attachment images older than the configured number of days.
    /// Call once on startup.
    /// </summary>
    public static void CleanupOldAttachments()
    {
        var settings = Current;
        if (!settings.AutoDeleteAttachments || settings.AutoDeleteAttachmentsDays <= 0)
            return;

        var attachmentsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RaisinTerminal", "attachments");

        if (!Directory.Exists(attachmentsRoot))
            return;

        var cutoff = DateTime.Now.AddDays(-settings.AutoDeleteAttachmentsDays);

        try
        {
            foreach (var projectDir in Directory.GetDirectories(attachmentsRoot))
            {
                foreach (var file in Directory.GetFiles(projectDir))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
                        continue;

                    if (File.GetCreationTime(file) < cutoff)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
        }
        catch { }
    }
}
