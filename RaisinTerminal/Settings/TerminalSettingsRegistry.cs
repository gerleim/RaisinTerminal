using Raisin.WPF.Base.Settings;

namespace RaisinTerminal.Settings;

public static class TerminalSettingsRegistry
{
    public static readonly string[] CategoryOrder = ["Display", "Attachments"];

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
    ];
}
