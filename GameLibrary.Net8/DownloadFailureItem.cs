namespace GameLibrary.Net8;

public class DownloadFailureItem
{
    public DownloadFailureItem(DownloadEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Name = string.IsNullOrWhiteSpace(entry.Name) ? UiText.Get("Downloader.FailureUnknownName") : entry.Name;
        Reason = string.IsNullOrWhiteSpace(entry.FailureReason) ? UiText.Get("Downloader.FailureNoReason") : entry.FailureReason;
        Urls = BuildUrls(entry);
        UrlsText = Urls.Count == 0 ? UiText.Get("Downloader.FailureNoUrls") : string.Join(Environment.NewLine, Urls);
    }

    public DownloadEntry Entry { get; }

    public string Name { get; }

    public string Reason { get; }

    public IReadOnlyList<string> Urls { get; }

    public string UrlsText { get; }

    private static IReadOnlyList<string> BuildUrls(DownloadEntry entry)
    {
        return new[]
            {
                entry.ArticleUrl,
                entry.DriveIntermediateUrl,
                entry.PrimaryUrl,
                entry.MirrorIntermediateUrl,
                entry.MirrorUrl
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
