namespace RaisinTerminal.Models;

public class Project
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string HomePath { get; set; } = "";
    public string? IconPath { get; set; }
    public bool AlertOnWaitingForInput { get; set; } = true;
}
