using System.IO;
using System.Media;

namespace RaisinTerminal.Services;

public static class AlertSoundPlayer
{
    private static readonly string MediaFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");

    private static SoundPlayer? _cachedPlayer;
    private static string? _cachedPath;

    public static string[] GetAvailableChoices()
    {
        var choices = new List<string>
        {
            "system:Asterisk",
            "system:Beep",
            "system:Exclamation",
            "system:Hand",
            "system:Question",
        };

        try
        {
            if (Directory.Exists(MediaFolder))
            {
                var wavFiles = Directory.GetFiles(MediaFolder, "*.wav")
                    .Select(Path.GetFileName)
                    .Where(f => f != null)
                    .Cast<string>()
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
                choices.AddRange(wavFiles);
            }
        }
        catch { }

        return choices.ToArray();
    }

    public static void Play(string? soundName)
    {
        if (string.IsNullOrEmpty(soundName))
        {
            SystemSounds.Beep.Play();
            return;
        }

        if (soundName.StartsWith("system:"))
        {
            var sound = soundName[7..] switch
            {
                "Asterisk" => SystemSounds.Asterisk,
                "Beep" => SystemSounds.Beep,
                "Exclamation" => SystemSounds.Exclamation,
                "Hand" => SystemSounds.Hand,
                "Question" => SystemSounds.Question,
                _ => SystemSounds.Beep,
            };
            sound.Play();
            return;
        }

        try
        {
            var path = Path.Combine(MediaFolder, soundName);
            if (File.Exists(path))
            {
                if (_cachedPath != path)
                {
                    _cachedPlayer?.Dispose();
                    _cachedPlayer = new SoundPlayer(path);
                    _cachedPlayer.Load();
                    _cachedPath = path;
                }
                _cachedPlayer!.Play();
            }
            else
                SystemSounds.Beep.Play();
        }
        catch
        {
            _cachedPlayer?.Dispose();
            _cachedPlayer = null;
            _cachedPath = null;
            SystemSounds.Beep.Play();
        }
    }
}
