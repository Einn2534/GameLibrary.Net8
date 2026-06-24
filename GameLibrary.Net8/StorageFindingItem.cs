namespace GameLibrary.Net8;

public class StorageFindingItem
{
    public StorageFindingItem(string type, string name, string details, string path)
    {
        Type = type ?? string.Empty;
        Name = name ?? string.Empty;
        Details = details ?? string.Empty;
        Path = path ?? string.Empty;
    }

    public string Type { get; }

    public string Name { get; }

    public string Details { get; }

    public string Path { get; }
}
