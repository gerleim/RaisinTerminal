using System.IO;
using System.Text.Json;
using RaisinTerminal.Models;

namespace RaisinTerminal.Services;

public static class ProjectService
{
    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RaisinTerminal");

    private static string ProjectsPath => Path.Combine(DataDir, "projects.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ProjectsState Load()
    {
        try
        {
            if (!File.Exists(ProjectsPath))
                return new ProjectsState();
            var json = File.ReadAllText(ProjectsPath);
            return JsonSerializer.Deserialize<ProjectsState>(json) ?? new ProjectsState();
        }
        catch { return new ProjectsState(); }
    }

    public static void Save(ProjectsState state)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(state, JsonOptions);
            var tempPath = ProjectsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(ProjectsPath))
                File.Replace(tempPath, ProjectsPath, destinationBackupFileName: null);
            else
                File.Move(tempPath, ProjectsPath);
        }
        catch { }
    }
}

public class ProjectsState
{
    public List<Project> Projects { get; set; } = [];
    public bool PanelVisible { get; set; } = true;
    public double PanelWidth { get; set; } = 240;
}
