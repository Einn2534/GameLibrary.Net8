namespace GameLibrary.Net8;

public class ArchiveImportResult
{
    public bool Success { get; set; }

    public bool Skipped { get; set; }

    public string Message { get; set; }

    public string InstallDirectory { get; set; }

    public string MediaPath { get; set; }

    public List<string> MediaPaths { get; set; } = new();

    public int MediaCount => MediaPaths?.Count > 0
        ? MediaPaths.Count
        : string.IsNullOrWhiteSpace(MediaPath) ? 0 : 1;
}
