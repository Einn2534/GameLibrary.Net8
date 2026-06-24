namespace GameLibrary.Net8;

public class StorageActionResult
{
    public bool Success { get; set; }

    public string Message { get; set; }

    public long BytesChanged { get; set; }

    public int ItemCount { get; set; }

    public List<string> Paths { get; set; } = new();
}
