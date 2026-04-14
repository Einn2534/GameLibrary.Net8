namespace GameLibrary.Net8;

public class ArchiveImportResult
{
    public bool Success { get; set; }

    public bool Skipped { get; set; }

    public string Message { get; set; }

    public string InstallDirectory { get; set; }
}
