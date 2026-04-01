using Raisin.WPF.Base.Settings;
using RaisinTerminal.Services;

namespace RaisinTerminal.Settings;

public static class TerminalSettingsRegistry
{
    public static readonly string[] CategoryOrder = ["Display", "Alerts", "Attachments", "Debug"];

    public static readonly List<SettingDefinition> All =
    [
        new()
        {
            Key = "display.compress-empty-lines",
            DisplayName = "Compress empty lines",
            Description = "Reduce the height of empty lines in terminal output for a more compact display.",
            Category = "Display",
            PropertyName = "CompressEmptyLines",
            EditorType = SettingEditorType.Bool,
            Order = 0,
        },
        new()
        {
            Key = "alerts.waiting-for-input",
            DisplayName = "Alert on waiting for input",
            Description = "Play a sound when a terminal session starts waiting for user input. This is the default for terminals not assigned to a project and for newly created projects.",
            Category = "Alerts",
            PropertyName = "AlertOnWaitingForInput",
            EditorType = SettingEditorType.Bool,
            Order = 0,
        },
        new()
        {
            Key = "alerts.sound",
            DisplayName = "Alert sound",
            Description = "Sound to play when a terminal starts waiting for input.",
            Category = "Alerts",
            PropertyName = "AlertSound",
            EditorType = SettingEditorType.Choice,
            Choices = AlertSoundPlayer.GetAvailableChoices(),
            Order = 1,
        },
        new()
        {
            Key = "attachments.auto-delete",
            DisplayName = "Auto-delete old attachments",
            Description = "Automatically delete attachment images older than the configured number of days on startup.",
            Category = "Attachments",
            PropertyName = "AutoDeleteAttachments",
            EditorType = SettingEditorType.Bool,
            Order = 0,
        },
        new()
        {
            Key = "attachments.auto-delete-days",
            DisplayName = "Auto-delete after (days)",
            Description = "Number of days after which attachment images are automatically deleted.",
            Category = "Attachments",
            PropertyName = "AutoDeleteAttachmentsDays",
            EditorType = SettingEditorType.UnsignedInt,
            Order = 1,
            MinInt = 1,
            MaxInt = 365,
        },
        new()
        {
            Key = "debug.ansi-logging",
            DisplayName = "ANSI sequence logging",
            Description = "Log terminal ANSI operations (cursor moves, erases, scrolls, line ops) to the log file for debugging rendering issues.",
            Category = "Debug",
            PropertyName = "AnsiLogging",
            EditorType = SettingEditorType.Bool,
            Order = 0,
        },
    ];
}
