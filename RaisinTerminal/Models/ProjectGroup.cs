namespace RaisinTerminal.Models;

public class ProjectGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
}
