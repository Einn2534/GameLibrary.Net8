namespace GameLibrary.Net8;

public class DownloadEntry
{
    public string Name { get; set; }

    public string ArticleUrl { get; set; }

    public string DriveIntermediateUrl { get; set; }

    public string MirrorIntermediateUrl { get; set; }

    public string PrimaryUrl { get; set; }

    public string MirrorUrl { get; set; }

    public string DownloadedFilePath { get; set; }

    public string FailureReason { get; set; }
}
