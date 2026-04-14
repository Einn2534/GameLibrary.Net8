using Newtonsoft.Json;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace GameLibrary.Net8;

public class DownloadPipelineService
{
    private const string BaseUrl = "https://kimochi.info";
    private const string ArticleUrlsFile = "article_urls.json";
    private const string DownloadRecordsFile = "download_records.json";
    private const string PrimaryResolvedFile = "primary_resolved.json";
    private const string PrimaryFailuresFile = "primary_failures.json";
    private const string MirrorCandidatesFile = "mirror_candidates.json";
    private const string MirrorResolvedFile = "mirror_resolved.json";

    public static IReadOnlyList<string> StepLabels =>
    [
        UiText.Get("Downloader.Step.0"),
        UiText.Get("Downloader.Step.1"),
        UiText.Get("Downloader.Step.2"),
        UiText.Get("Downloader.Step.3"),
        UiText.Get("Downloader.Step.4"),
        UiText.Get("Downloader.Step.5"),
        UiText.Get("Downloader.Step.6"),
        UiText.Get("Downloader.Step.7")
    ];

    public async Task RunAsync(
        DownloadPipelineOptions options,
        Action<int, string> setStepStatus,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        Directory.CreateDirectory(options.RuntimeDirectory);
        Directory.CreateDirectory(options.SaveDirectory);

        for (int i = 0; i < StepLabels.Count; i++)
        {
            if (i < options.StartStepIndex)
            {
                setStepStatus(i, UiText.Get("Status.Skipped"));
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            setStepStatus(i, UiText.Get("Status.Running"));
            log(string.Empty);
            log(new string('=', 52));
            log("Step " + i + ": " + StepLabels[i]);
            log(new string('=', 52));

            try
            {
                switch (i)
                {
                    case 0:
                        await Step0CollectArticleUrlsAsync(options.RuntimeDirectory, options.DateFrom, options.DateTo, log, cancellationToken);
                        break;
                    case 1:
                        await Step1NormalizeArticleUrlsAsync(options.RuntimeDirectory, log, cancellationToken);
                        break;
                    case 2:
                        await Step2ExtractHostLinksAsync(options.RuntimeDirectory, log, cancellationToken);
                        break;
                    case 3:
                        await Step3ResolvePrimaryLinksAsync(options.RuntimeDirectory, log, cancellationToken);
                        break;
                    case 4:
                        await Step4DownloadPrimaryArchivesAsync(options.RuntimeDirectory, options.SaveDirectory, log, cancellationToken);
                        break;
                    case 5:
                        await Step5PrepareMirrorFallbackAsync(options.RuntimeDirectory, log, cancellationToken);
                        break;
                    case 6:
                        await Step6ResolveMirrorLinksAsync(options.RuntimeDirectory, log, cancellationToken);
                        break;
                    case 7:
                        await Step7DownloadMirrorArchivesAsync(options.RuntimeDirectory, options.SaveDirectory, log, cancellationToken);
                        break;
                }

                    setStepStatus(i, UiText.Get("Status.Done"));
                }
                catch
                {
                    setStepStatus(i, UiText.Get("Status.Error"));
                    throw;
                }
            }
    }

    private async Task Step0CollectArticleUrlsAsync(
        string runtimeDirectory,
        DateTime? dateFrom,
        DateTime? dateTo,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var urls = new List<string>();
        int page = 1;

        using HttpClient client = CreateHttpClient();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string pageUrl = page == 1 ? BaseUrl + "/" : BaseUrl + "/page/" + page + "/";
            log("Fetching page " + page + ": " + pageUrl);
            string html = await GetStringSafeAsync(client, pageUrl, cancellationToken);
            if (string.IsNullOrWhiteSpace(html))
            {
                log("No more article pages.");
                break;
            }

            List<Tuple<string, DateTime?>> articles = ExtractArticlesFromPage(html);
            if (articles.Count == 0)
            {
                log("No articles found on page " + page + ".");
                break;
            }

            bool reachedOlderArticle = false;
            foreach (Tuple<string, DateTime?> article in articles)
            {
                DateTime? postDate = article.Item2;
                if (postDate.HasValue)
                {
                    if (dateTo.HasValue && postDate.Value > dateTo.Value)
                    {
                        continue;
                    }

                    if (dateFrom.HasValue && postDate.Value < dateFrom.Value)
                    {
                        reachedOlderArticle = true;
                        break;
                    }
                }

                urls.Add(article.Item1);
            }

            log("Collected " + urls.Count + " article URL(s) so far.");
            if (reachedOlderArticle)
            {
                break;
            }

            page++;
        }

        urls = urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SaveJson(Path.Combine(runtimeDirectory, ArticleUrlsFile), urls);
        log("Saved " + urls.Count + " article URL(s).");
    }

    private Task Step1NormalizeArticleUrlsAsync(string runtimeDirectory, Action<string> log, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<string> urls = LoadJson<List<string>>(Path.Combine(runtimeDirectory, ArticleUrlsFile)) ?? [];
        urls = urls
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        SaveJson(Path.Combine(runtimeDirectory, ArticleUrlsFile), urls);
        log("Normalized " + urls.Count + " article URL(s).");
        return Task.CompletedTask;
    }

    private async Task Step2ExtractHostLinksAsync(string runtimeDirectory, Action<string> log, CancellationToken cancellationToken)
    {
        List<string> articleUrls = LoadJson<List<string>>(Path.Combine(runtimeDirectory, ArticleUrlsFile)) ?? [];
        var records = new List<DownloadEntry>();

        using HttpClient client = CreateHttpClient();
        for (int i = 0; i < articleUrls.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string articleUrl = articleUrls[i];
            try
            {
                string html = await GetStringSafeAsync(client, articleUrl, cancellationToken);
                if (string.IsNullOrWhiteSpace(html))
                {
                    log("[" + (i + 1) + "/" + articleUrls.Count + "] Empty article response: " + articleUrl);
                    continue;
                }

                string title = ExtractArticleTitle(html);
                string fileName = SanitizeFileName(string.IsNullOrWhiteSpace(title) ? "unknown_title" : title);
                string driveLink = ExtractHostLink(html, "Drive", articleUrl);
                string mirrorLink = ExtractHostLink(html, "Mirror", articleUrl);

                records.Add(new DownloadEntry
                {
                    Name = fileName,
                    ArticleUrl = articleUrl,
                    DriveIntermediateUrl = driveLink,
                    MirrorIntermediateUrl = mirrorLink
                });

                log("[" + (i + 1) + "/" + articleUrls.Count + "] " + fileName);
            }
            catch (Exception ex)
            {
                log("Failed to extract host links: " + ex.Message);
            }
        }

        SaveJson(Path.Combine(runtimeDirectory, DownloadRecordsFile), records);
        log("Saved " + records.Count + " download record(s).");
    }

    private async Task Step3ResolvePrimaryLinksAsync(string runtimeDirectory, Action<string> log, CancellationToken cancellationToken)
    {
        List<DownloadEntry> records = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, DownloadRecordsFile)) ?? [];

        using HttpClient client = CreateHttpClient();
        for (int i = 0; i < records.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadEntry record = records[i];
            if (string.IsNullOrWhiteSpace(record.DriveIntermediateUrl))
            {
                log("[" + (i + 1) + "/" + records.Count + "] No primary link for " + record.Name);
                continue;
            }

            try
            {
                record.PrimaryUrl = await ResolveIntermediateLinkAsync(client, record.DriveIntermediateUrl, cancellationToken);
                log("[" + (i + 1) + "/" + records.Count + "] Resolved primary URL for " + record.Name);
            }
            catch (Exception ex)
            {
                record.FailureReason = ex.Message;
                log("Primary link resolution failed for " + record.Name + ": " + ex.Message);
            }
        }

        SaveJson(Path.Combine(runtimeDirectory, PrimaryResolvedFile), records);
    }

    private async Task Step4DownloadPrimaryArchivesAsync(string runtimeDirectory, string saveDirectory, Action<string> log, CancellationToken cancellationToken)
    {
        List<DownloadEntry> records = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, PrimaryResolvedFile)) ?? [];
        var failures = new List<DownloadEntry>();

        for (int i = 0; i < records.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadEntry record = records[i];

            if (string.IsNullOrWhiteSpace(record.PrimaryUrl))
            {
                record.FailureReason = "Primary URL missing";
                failures.Add(record);
                log("[" + (i + 1) + "/" + records.Count + "] No primary URL: " + record.Name);
                continue;
            }

            try
            {
                record.DownloadedFilePath = await DownloadResolvedUrlAsync(record.Name, record.PrimaryUrl, saveDirectory, cancellationToken);
                record.FailureReason = null;
                log("[" + (i + 1) + "/" + records.Count + "] Downloaded primary: " + Path.GetFileName(record.DownloadedFilePath));
            }
            catch (Exception ex)
            {
                record.FailureReason = ex.Message;
                failures.Add(record);
                log("Primary download failed for " + record.Name + ": " + ex.Message);
            }
        }

        SaveJson(Path.Combine(runtimeDirectory, PrimaryResolvedFile), records);
        SaveJson(Path.Combine(runtimeDirectory, PrimaryFailuresFile), failures);
        log("Primary failures: " + failures.Count);
    }

    private Task Step5PrepareMirrorFallbackAsync(string runtimeDirectory, Action<string> log, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<DownloadEntry> failures = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, PrimaryFailuresFile)) ?? [];
        List<DownloadEntry> mirrorCandidates = failures
            .Where(record => !string.IsNullOrWhiteSpace(record.MirrorIntermediateUrl))
            .ToList();
        SaveJson(Path.Combine(runtimeDirectory, MirrorCandidatesFile), mirrorCandidates);
        log("Mirror fallback candidates: " + mirrorCandidates.Count);
        return Task.CompletedTask;
    }

    private async Task Step6ResolveMirrorLinksAsync(string runtimeDirectory, Action<string> log, CancellationToken cancellationToken)
    {
        List<DownloadEntry> records = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, MirrorCandidatesFile)) ?? [];

        using HttpClient client = CreateHttpClient();
        for (int i = 0; i < records.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadEntry record = records[i];
            try
            {
                record.MirrorUrl = await ResolveIntermediateLinkAsync(client, record.MirrorIntermediateUrl, cancellationToken);
                log("[" + (i + 1) + "/" + records.Count + "] Resolved mirror URL for " + record.Name);
            }
            catch (Exception ex)
            {
                record.FailureReason = ex.Message;
                log("Mirror link resolution failed for " + record.Name + ": " + ex.Message);
            }
        }

        SaveJson(Path.Combine(runtimeDirectory, MirrorResolvedFile), records);
    }

    private async Task Step7DownloadMirrorArchivesAsync(string runtimeDirectory, string saveDirectory, Action<string> log, CancellationToken cancellationToken)
    {
        List<DownloadEntry> records = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, MirrorResolvedFile)) ?? [];

        for (int i = 0; i < records.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadEntry record = records[i];
            if (string.IsNullOrWhiteSpace(record.MirrorUrl))
            {
                log("[" + (i + 1) + "/" + records.Count + "] No mirror URL: " + record.Name);
                continue;
            }

            try
            {
                record.DownloadedFilePath = await DownloadResolvedUrlAsync(record.Name, record.MirrorUrl, saveDirectory, cancellationToken);
                log("[" + (i + 1) + "/" + records.Count + "] Downloaded mirror: " + Path.GetFileName(record.DownloadedFilePath));
            }
            catch (Exception ex)
            {
                record.FailureReason = ex.Message;
                log("Mirror download failed for " + record.Name + ": " + ex.Message);
            }
        }

        SaveJson(Path.Combine(runtimeDirectory, MirrorResolvedFile), records);
    }

    private async Task<string> ResolveIntermediateLinkAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        Uri currentUri = new(url, UriKind.Absolute);
        string firstHtml = await GetStringSafeAsync(client, currentUri.AbsoluteUri, cancellationToken);
        string getLinkUrl = ExtractActionLink(firstHtml, currentUri, "Getlink");
        if (string.IsNullOrWhiteSpace(getLinkUrl))
        {
            throw new InvalidOperationException("Getlink action was not found.");
        }

        Uri secondUri = new(getLinkUrl, UriKind.Absolute);
        string secondHtml = await GetStringSafeAsync(client, secondUri.AbsoluteUri, cancellationToken);
        string directUrl = ExtractDirectDownloadLink(secondHtml, secondUri);
        if (string.IsNullOrWhiteSpace(directUrl))
        {
            throw new InvalidOperationException("Direct download link was not found.");
        }

        return directUrl;
    }

    private async Task<string> DownloadResolvedUrlAsync(string baseName, string url, string saveDirectory, CancellationToken cancellationToken)
    {
        Uri uri = new(url, UriKind.Absolute);
        string host = uri.Host.ToLowerInvariant();

        if (host.Contains("drive.google.com"))
        {
            return await DownloadGoogleDriveAsync(baseName, uri, saveDirectory, cancellationToken);
        }

        if (host.Contains("mediafire.com"))
        {
            string directUrl = await ResolveMediaFireDirectUrlAsync(uri.AbsoluteUri, cancellationToken);
            return await DownloadGenericFileAsync(baseName, directUrl, saveDirectory, cancellationToken);
        }

        return await DownloadGenericFileAsync(baseName, uri.AbsoluteUri, saveDirectory, cancellationToken);
    }

    private async Task<string> DownloadGenericFileAsync(string baseName, string url, string saveDirectory, CancellationToken cancellationToken)
    {
        using HttpClient client = CreateHttpClient();
        using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await SaveResponseToFileAsync(baseName, response, saveDirectory, cancellationToken);
    }

    private async Task<string> DownloadGoogleDriveAsync(string baseName, Uri uri, string saveDirectory, CancellationToken cancellationToken)
    {
        string fileId = ExtractDriveFileId(uri.AbsoluteUri);
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new InvalidOperationException("Google Drive file id could not be determined.");
        }

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true
        };

        using HttpClient client = CreateHttpClient(handler);
        string initialUrl = "https://drive.google.com/uc?export=download&id=" + Uri.EscapeDataString(fileId);
        using HttpResponseMessage initialResponse = await client.GetAsync(initialUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (IsDownloadResponse(initialResponse))
        {
            return await SaveResponseToFileAsync(baseName, initialResponse, saveDirectory, cancellationToken);
        }

        string html = await initialResponse.Content.ReadAsStringAsync();
        string confirmUrl = ExtractGoogleDriveConfirmUrl(html, initialResponse.RequestMessage.RequestUri);
        if (string.IsNullOrWhiteSpace(confirmUrl))
        {
            throw new InvalidOperationException("Google Drive confirmation link was not found.");
        }

        using HttpResponseMessage confirmedResponse = await client.GetAsync(confirmUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!IsDownloadResponse(confirmedResponse))
        {
            string body = await confirmedResponse.Content.ReadAsStringAsync();
            if (body.IndexOf("quota exceeded", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new InvalidOperationException("Google Drive quota exceeded.");
            }
        }

        confirmedResponse.EnsureSuccessStatusCode();
        return await SaveResponseToFileAsync(baseName, confirmedResponse, saveDirectory, cancellationToken);
    }

    private async Task<string> ResolveMediaFireDirectUrlAsync(string url, CancellationToken cancellationToken)
    {
        using HttpClient client = CreateHttpClient();
        string html = await GetStringSafeAsync(client, url, cancellationToken);
        foreach (Match anchor in Regex.Matches(
            html,
            "<a[^>]*href=[\"'](?<href>https?://[^\"']+)[\"'][^>]*>(?<text>[\\s\\S]*?)</a>",
            RegexOptions.IgnoreCase))
        {
            string text = StripTags(anchor.Groups["text"].Value);
            string href = WebUtility.HtmlDecode(anchor.Groups["href"].Value);
            if (text.IndexOf("download", StringComparison.OrdinalIgnoreCase) >= 0 &&
                href.IndexOf("mediafire.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return href;
            }
        }

        throw new InvalidOperationException("MediaFire direct download link was not found.");
    }

    private static bool IsDownloadResponse(HttpResponseMessage response)
    {
        string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        return !contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> SaveResponseToFileAsync(
        string baseName,
        HttpResponseMessage response,
        string saveDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(saveDirectory);

        string extension = GetPreferredExtension(response);
        string finalName = baseName;
        if (!string.IsNullOrWhiteSpace(extension) &&
            !finalName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            finalName += extension;
        }

        string targetPath = Path.Combine(saveDirectory, finalName);
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream destination = new(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken);

        return targetPath;
    }

    private static string GetPreferredExtension(HttpResponseMessage response)
    {
        string fileName = response.Content.Headers.ContentDisposition?.FileNameStar ??
                          response.Content.Headers.ContentDisposition?.FileName;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            fileName = fileName.Trim('"');
            string extension = Path.GetExtension(fileName);
            if (!string.IsNullOrWhiteSpace(extension))
            {
                return extension;
            }
        }

        string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        switch (mediaType.ToLowerInvariant())
        {
            case "application/pdf":
                return ".pdf";
            case "application/zip":
            case "application/x-zip-compressed":
                return ".zip";
            case "application/x-rar-compressed":
                return ".rar";
            case "image/jpeg":
                return ".jpg";
            case "image/png":
                return ".png";
            case "video/mp4":
                return ".mp4";
        }

        Uri finalUri = response.RequestMessage?.RequestUri;
        if (finalUri != null)
        {
            string uriExtension = Path.GetExtension(finalUri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(uriExtension) && uriExtension.Length <= 5)
            {
                return uriExtension;
            }
        }

        return string.Empty;
    }

    private static List<Tuple<string, DateTime?>> ExtractArticlesFromPage(string html)
    {
        List<Tuple<string, DateTime?>> results = [];

        foreach (Match articleMatch in Regex.Matches(
            html,
            "<article\\b[\\s\\S]*?</article>",
            RegexOptions.IgnoreCase))
        {
            string articleHtml = articleMatch.Value;
            Match hrefMatch = Regex.Match(
                articleHtml,
                "entry-title[\\s\\S]*?<a[^>]*href=[\"'](?<href>[^\"']+)[\"']",
                RegexOptions.IgnoreCase);
            if (!hrefMatch.Success)
            {
                continue;
            }

            string href = WebUtility.HtmlDecode(hrefMatch.Groups["href"].Value);
            if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Match dateMatch = Regex.Match(
                articleHtml,
                "updated[\"'][^>]*>(?<text>[\\s\\S]*?)</",
                RegexOptions.IgnoreCase);
            DateTime? date = dateMatch.Success
                ? ParseRelativeTime(StripTags(dateMatch.Groups["text"].Value))
                : null;

            results.Add(Tuple.Create(href, date));
        }

        return results;
    }

    private static string ExtractArticleTitle(string html)
    {
        Match match = Regex.Match(
            html,
            "<h1[^>]*class=[\"'][^\"']*entry-title[^\"']*[\"'][^>]*>(?<text>[\\s\\S]*?)</h1>",
            RegexOptions.IgnoreCase);
        return match.Success ? StripTags(match.Groups["text"].Value) : string.Empty;
    }

    private static string ExtractHostLink(string html, string hostName, string pageUrl)
    {
        string pattern =
            "<p[^>]*class=[\"'][^\"']*download-host-name[^\"']*[\"'][^>]*>\\s*" +
            Regex.Escape(hostName) +
            "\\s*</p>[\\s\\S]*?<a[^>]*href=[\"'](?<href>[^\"']+)[\"']";
        Match match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        string href = WebUtility.HtmlDecode(match.Groups["href"].Value);
        return new Uri(new Uri(pageUrl), href).AbsoluteUri;
    }

    private static string ExtractActionLink(string html, Uri baseUri, string actionText)
    {
        foreach (Match match in Regex.Matches(
            html,
            "<a[^>]*href=[\"'](?<href>[^\"']+)[\"'][^>]*>(?<text>[\\s\\S]*?)</a>",
            RegexOptions.IgnoreCase))
        {
            string text = StripTags(match.Groups["text"].Value);
            if (text.IndexOf(actionText, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            return new Uri(baseUri, WebUtility.HtmlDecode(match.Groups["href"].Value)).AbsoluteUri;
        }

        return null;
    }

    private static string ExtractDirectDownloadLink(string html, Uri baseUri)
    {
        foreach (Match match in Regex.Matches(
            html,
            "<a[^>]*href=[\"'](?<href>[^\"']+)[\"'][^>]*>(?<text>[\\s\\S]*?)</a>",
            RegexOptions.IgnoreCase))
        {
            string text = StripTags(match.Groups["text"].Value);
            if (text.IndexOf("direct download link", StringComparison.OrdinalIgnoreCase) < 0 &&
                text.IndexOf("download", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            string href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            return new Uri(baseUri, href).AbsoluteUri;
        }

        return null;
    }

    private static string ExtractGoogleDriveConfirmUrl(string html, Uri baseUri)
    {
        Match hrefMatch = Regex.Match(
            html,
            "href=[\"'](?<href>/uc\\?export=download[^\"']+)[\"']",
            RegexOptions.IgnoreCase);
        if (hrefMatch.Success)
        {
            return new Uri(baseUri, WebUtility.HtmlDecode(hrefMatch.Groups["href"].Value.Replace("&amp;", "&"))).AbsoluteUri;
        }

        Match formMatch = Regex.Match(
            html,
            "<form[^>]*id=[\"']download-form[\"'][^>]*action=[\"'](?<action>[^\"']+)[\"'][^>]*>(?<body>[\\s\\S]*?)</form>",
            RegexOptions.IgnoreCase);
        if (!formMatch.Success)
        {
            return null;
        }

        string action = WebUtility.HtmlDecode(formMatch.Groups["action"].Value.Replace("&amp;", "&"));
        string body = formMatch.Groups["body"].Value;
        var pairs = new List<string>();
        foreach (Match input in Regex.Matches(
            body,
            "<input[^>]*name=[\"'](?<name>[^\"']+)[\"'][^>]*value=[\"'](?<value>[^\"']*)[\"'][^>]*>",
            RegexOptions.IgnoreCase))
        {
            string name = WebUtility.HtmlDecode(input.Groups["name"].Value);
            string value = WebUtility.HtmlDecode(input.Groups["value"].Value);
            pairs.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value));
        }

        string separator = action.Contains('?') ? "&" : "?";
        return new Uri(baseUri, action + separator + string.Join("&", pairs)).AbsoluteUri;
    }

    private static DateTime? ParseRelativeTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        DateTime now = DateTime.Now;
        string normalized = text.Trim().ToLowerInvariant();
        var patterns = new[]
        {
            Tuple.Create("second", 1),
            Tuple.Create("minute", 60),
            Tuple.Create("hour", 3600),
            Tuple.Create("day", 86400),
            Tuple.Create("week", 604800),
            Tuple.Create("month", 2592000),
            Tuple.Create("year", 31536000)
        };

        foreach (Tuple<string, int> pattern in patterns)
        {
            Match match = Regex.Match(normalized, "(\\d+)\\s+" + pattern.Item1, RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            int amount = int.Parse(match.Groups[1].Value);
            return now.AddSeconds(-(amount * pattern.Item2));
        }

        return null;
    }

    private static string ExtractDriveFileId(string text)
    {
        Match match = Regex.Match(text, "(?:/file/d/|[?&]id=)([A-Za-z0-9_-]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unknown_title";
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name.Trim();
    }

    private static string StripTags(string html)
    {
        string value = Regex.Replace(html ?? string.Empty, "<.*?>", string.Empty);
        return WebUtility.HtmlDecode(value).Trim();
    }

    private static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string json = JsonConvert.SerializeObject(value, Formatting.Indented);
        File.WriteAllText(path, json);
    }

    private static T LoadJson<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        string json = File.ReadAllText(path);
        return JsonConvert.DeserializeObject<T>(json);
    }

    private static HttpClient CreateHttpClient()
    {
        return CreateHttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true
        });
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(90);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        return client;
    }

    private static async Task<string> GetStringSafeAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
