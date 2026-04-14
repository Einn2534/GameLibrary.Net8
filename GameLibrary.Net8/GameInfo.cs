namespace GameLibrary.Net8;

public class GameInfo
{
    public string Name { get; set; }

    public string Icon { get; set; }

    public string Executable { get; set; }

    public string InstallDirectory { get; set; }

    public string Description { get; set; }

    public List<string> Tags { get; set; } = new();
}
