using System.IO;
using System.Text.Json;
using System.Windows.Input;
using RaisinTerminal.Settings;

namespace RaisinTerminal.Services;

/// <summary>
/// Loads, persists, and resolves user key bindings. Bindings are stored as a
/// flat { commandId: "Ctrl+Shift+K" } map at %AppData%/RaisinTerminal/keybindings.json.
/// Anything not present in the file falls back to the registry default.
/// </summary>
public static class KeyBindingsService
{
    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RaisinTerminal");

    private static string SettingsPath => Path.Combine(DataDir, "keybindings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static Dictionary<string, KeyChord>? _overrides;

    /// <summary>Raised after Save() completes so listeners (menus, dispatchers) can refresh.</summary>
    public static event Action? BindingsChanged;

    private static Dictionary<string, KeyChord> Overrides => _overrides ??= Load();

    private static Dictionary<string, KeyChord> Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new();
            var json = File.ReadAllText(SettingsPath);
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            var result = new Dictionary<string, KeyChord>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, text) in raw)
                result[id] = KeyChord.Parse(text);
            return result;
        }
        catch
        {
            return new();
        }
    }

    /// <summary>Resolves the chord currently bound to the given command id.</summary>
    public static KeyChord Get(string commandId)
    {
        if (Overrides.TryGetValue(commandId, out var chord)) return chord;
        var def = KeyBindingsRegistry.All.FirstOrDefault(d => d.Id == commandId);
        return def?.Default ?? KeyChord.None;
    }

    /// <summary>
    /// Returns the command id bound to (key, modifiers), or null if none matches.
    /// When <paramref name="scope"/> is provided, only commands at that scope are considered.
    /// </summary>
    public static string? TryResolve(Key key, ModifierKeys modifiers, KeyBindingScope? scope = null)
    {
        if (key == Key.None) return null;
        foreach (var def in KeyBindingsRegistry.All)
        {
            if (scope.HasValue && def.Scope != scope.Value) continue;
            var chord = Get(def.Id);
            if (chord.IsNone) continue;
            if (chord.Key == key && chord.Modifiers == modifiers)
                return def.Id;
        }
        return null;
    }

    /// <summary>
    /// Replaces all overrides with the given map and persists to disk.
    /// Bindings equal to the registered default are omitted from the file.
    /// </summary>
    public static void Save(IReadOnlyDictionary<string, KeyChord> bindings)
    {
        var toPersist = new Dictionary<string, string>();
        foreach (var def in KeyBindingsRegistry.All)
        {
            if (!bindings.TryGetValue(def.Id, out var chord)) continue;
            if (chord == def.Default) continue;
            toPersist[def.Id] = chord.ToString();
        }

        _overrides = new Dictionary<string, KeyChord>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in KeyBindingsRegistry.All)
        {
            if (bindings.TryGetValue(def.Id, out var chord))
                _overrides[def.Id] = chord;
        }

        try
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(toPersist, JsonOptions);
            var tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(SettingsPath))
                File.Replace(tempPath, SettingsPath, destinationBackupFileName: null);
            else
                File.Move(tempPath, SettingsPath);
        }
        catch { }

        BindingsChanged?.Invoke();
    }
}
