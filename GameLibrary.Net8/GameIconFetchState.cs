using System.Net.Http;

namespace GameLibrary.Net8;

internal sealed class IconFetchMetadata
{
    public DateTime LastAttemptUtc { get; set; }

    public bool Success { get; set; }

    public string Source { get; set; }

    public string SourcePageUrl { get; set; }

    public string ImageUrl { get; set; }

    public List<string> SourceTags { get; set; } = new();

    public string LastError { get; set; }

    public string LastAttemptedSource { get; set; }

    public string LastAttemptedUrl { get; set; }

    public string LastAttemptedSourcePageUrl { get; set; }

    public int? LastHttpStatusCode { get; set; }

    public string LastRequestHeaders { get; set; }

    public IconFetchMetadata WithDiagnostics(IconFetchContext fetchContext)
    {
        if (fetchContext == null)
        {
            return this;
        }

        Source = Source ?? fetchContext.LastSource;
        SourcePageUrl = SourcePageUrl ?? fetchContext.LastSourcePageUrl;
        ImageUrl = ImageUrl ?? fetchContext.LastUrl;
        LastAttemptedSource = fetchContext.LastSource;
        LastAttemptedUrl = fetchContext.LastUrl;
        LastAttemptedSourcePageUrl = fetchContext.LastSourcePageUrl;
        LastHttpStatusCode = fetchContext.LastStatusCode;
        LastRequestHeaders = fetchContext.LastRequestHeaders;
        LastError = string.IsNullOrWhiteSpace(LastError) ? fetchContext.LastError : LastError;
        return this;
    }
}

internal sealed class IconFetchContext
{
    public string LastSource { get; private set; }

    public string LastSourcePageUrl { get; private set; }

    public string LastUrl { get; private set; }

    public int? LastStatusCode { get; private set; }

    public string LastRequestHeaders { get; private set; }

    public string LastError { get; set; }

    public void RecordRequest(
        string source,
        string sourcePageUrl,
        string url,
        HttpClient httpClient,
        HttpRequestMessage request)
    {
        LastSource = source;
        LastSourcePageUrl = sourcePageUrl;
        LastUrl = url;
        LastStatusCode = null;
        LastError = string.Empty;
        LastRequestHeaders = FormatRequestHeaders(httpClient, request);
    }

    public void RecordResponse(HttpResponseMessage response)
    {
        if (response == null)
        {
            return;
        }

        LastStatusCode = (int)response.StatusCode;
    }

    private static string FormatRequestHeaders(HttpClient httpClient, HttpRequestMessage request)
    {
        var lines = new List<string>();

        if (httpClient != null)
        {
            lines.AddRange(httpClient.DefaultRequestHeaders
                .Select(header => header.Key + ": " + string.Join(", ", header.Value)));
        }

        if (request != null)
        {
            lines.AddRange(request.Headers
                .Select(header => header.Key + ": " + string.Join(", ", header.Value)));
        }

        return string.Join(Environment.NewLine, lines);
    }
}

internal sealed class ImageDownloadResult
{
    public static readonly ImageDownloadResult Empty = new(string.Empty, string.Empty);

    public ImageDownloadResult(string iconPath, string error)
    {
        IconPath = iconPath;
        Error = error;
    }

    public string IconPath { get; }

    public string Error { get; }
}

internal sealed class ArticleIconCandidate
{
    public string ArticleUrl { get; set; }

    public string ImageUrl { get; set; }

    public string DlsiteProductUrl { get; set; }

    public List<string> SourceTags { get; set; } = new();
}
