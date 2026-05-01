using System.IO;
using System.Text.Json;

namespace RaisinTerminal.Services;

/// <summary>
/// Persists the most recent terminal canvas dimensions per session ContentId
/// so restored sessions can be eagerly started at the size they'll actually be
/// when activated, avoiding a buffer truncate/reflow race.
/// </summary>
public static class SessionDimensionsService
{
    public record Dims(int Cols, int Rows);

    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RaisinTerminal");

    private static string DimsPath => Path.Combine(DataDir, "session-dims.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static Dictionary<string, Dims> _dims = Load();

    public static Dims? Get(string contentId) =>
        _dims.TryGetValue(contentId, out var d) ? d : null;

    public static void Set(string contentId, int cols, int rows)
    {
        if (string.IsNullOrEmpty(contentId) || cols <= 0 || rows <= 0) return;
        _dims[contentId] = new Dims(cols, rows);
    }

    /// <summary>
    /// Writes the current dimension map to disk, dropping any entries whose
    /// ContentId is not in <paramref name="liveContentIds"/> so closed tabs
    /// don't accumulate indefinitely.
    /// </summary>
    public static void Save(IEnumerable<string> liveContentIds)
    {
        try
        {
            var live = new HashSet<string>(liveContentIds);
            var pruned = new Dictionary<string, Dims>();
            foreach (var (id, dims) in _dims)
                if (live.Contains(id)) pruned[id] = dims;
            _dims = pruned;

            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(_dims, JsonOptions);
            var tempPath = DimsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(DimsPath))
                File.Replace(tempPath, DimsPath, destinationBackupFileName: null);
            else
                File.Move(tempPath, DimsPath);
        }
        catch { }
    }

    private static Dictionary<string, Dims> Load()
    {
        try
        {
            if (!File.Exists(DimsPath)) return new();
            var json = File.ReadAllText(DimsPath);
            return JsonSerializer.Deserialize<Dictionary<string, Dims>>(json) ?? new();
        }
        catch { return new(); }
    }
}
