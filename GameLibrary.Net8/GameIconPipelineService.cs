using Newtonsoft.Json;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using static GameLibrary.Net8.DownloadFileInspector;

namespace GameLibrary.Net8;

public class GameIconPipelineService
{
    private const string DlsiteManiaxBaseUrl = "https://www.dlsite.com/maniax";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly string cacheDirectory;

    public GameIconPipelineService(string cacheDirectory, bool enableRemoteFetch, int remoteFetchLimitPerRun)
    {
        this.cacheDirectory = cacheDirectory ?? throw new ArgumentNullException(nameof(cacheDirectory));
        EnableRemoteFetch = enableRemoteFetch;
        RemoteFetchLimitPerRun = Math.Max(0, remoteFetchLimitPerRun);
    }

    public bool EnableRemoteFetch { get; }

    public int RemoteFetchLimitPerRun { get; }

    public GameIconResolveResult ResolveIcon(string gameName, string gameRootDirectory, bool allowRemoteFetch)
    {
        return ResolveIcon(gameName, gameRootDirectory, allowRemoteFetch, string.Empty);
    }

    public GameIconResolveResult ResolveIcon(
        string gameName,
        string gameRootDirectory,
        bool allowRemoteFetch,
        string sourcePageUrl)
    {
        Directory.CreateDirectory(cacheDirectory);

        string entryDirectory = GetEntryDirectory(gameName, gameRootDirectory);
        string cachedIconPath = TryGetCachedIconPath(entryDirectory);
        if (!string.IsNullOrWhiteSpace(cachedIconPath))
        {
            return new GameIconResolveResult(cachedIconPath, attemptedRemoteFetch: false);
        }

        IconFetchMetadata metadata = LoadMetadata(entryDirectory);
        string directSourcePageUrl = ResolveDirectSourcePageUrl(sourcePageUrl, metadata);
        if (!allowRemoteFetch ||
            !EnableRemoteFetch ||
            !ShouldAttemptFetch(metadata) ||
            string.IsNullOrWhiteSpace(directSourcePageUrl))
        {
            return new GameIconResolveResult(string.Empty, attemptedRemoteFetch: false);
        }

        IconFetchMetadata updatedMetadata = FetchAndCacheIconFromSourcePage(directSourcePageUrl, entryDirectory);
        SaveMetadata(entryDirectory, updatedMetadata);

        string iconPath = TryGetCachedIconPath(entryDirectory);
        return new GameIconResolveResult(iconPath, attemptedRemoteFetch: true);
    }

    public GameIconResolveResult ResolveIconFromKimochiArticle(
        string gameName,
        string gameRootDirectory,
        string articleUrl,
        string articleHtml)
    {
        Directory.CreateDirectory(cacheDirectory);

        string entryDirectory = GetEntryDirectory(gameName, gameRootDirectory);
        ArticleIconCandidate articleCandidate = BuildArticleIconCandidate(articleUrl, articleHtml);
        SaveArticleSourceMetadata(entryDirectory, articleUrl, articleCandidate.SourceTags);

        string cachedIconPath = TryGetCachedIconPath(entryDirectory);
        if (!string.IsNullOrWhiteSpace(cachedIconPath))
        {
            return new GameIconResolveResult(cachedIconPath, attemptedRemoteFetch: false);
        }

        if (!EnableRemoteFetch || string.IsNullOrWhiteSpace(articleHtml))
        {
            return new GameIconResolveResult(string.Empty, attemptedRemoteFetch: false);
        }

        var fetchContext = new IconFetchContext();
        IconFetchMetadata metadata = TryFetchFromKimochiArticleCandidate(entryDirectory, articleCandidate, fetchContext)
            ?? new IconFetchMetadata
            {
                LastAttemptUtc = DateTime.UtcNow,
                Success = false,
                Source = "kimochi",
                SourcePageUrl = articleUrl,
                SourceTags = articleCandidate.SourceTags,
                LastError = "No matching remote icon was found."
            }.WithDiagnostics(fetchContext);

        SaveMetadata(entryDirectory, metadata);
        string iconPath = TryGetCachedIconPath(entryDirectory);
        return new GameIconResolveResult(iconPath, attemptedRemoteFetch: true);
    }

    public GameSourceMetadata ResolveSourceMetadataFromKimochiArticle(
        string gameName,
        string gameRootDirectory,
        string articleUrl,
        string articleHtml)
    {
        Directory.CreateDirectory(cacheDirectory);

        string entryDirectory = GetEntryDirectory(gameName, gameRootDirectory);
        ArticleIconCandidate articleCandidate = BuildArticleIconCandidate(articleUrl, articleHtml);
        IconFetchMetadata metadata = SaveArticleSourceMetadata(
            entryDirectory,
            articleUrl,
            articleCandidate.SourceTags);

        return new GameSourceMetadata
        {
            SourcePageUrl = ResolveDirectSourcePageUrl(articleUrl, metadata),
            DefaultTags = NormalizeTagList(metadata?.SourceTags)
        };
    }

    public GameSourceMetadata ResolveSourceMetadata(string gameName, string gameRootDirectory)
    {
        return ResolveSourceMetadata(gameName, gameRootDirectory, string.Empty);
    }

    public GameSourceMetadata ResolveSourceMetadata(string gameName, string gameRootDirectory, string sourcePageUrl)
    {
        string entryDirectory = GetEntryDirectory(gameName, gameRootDirectory);
        IconFetchMetadata metadata = LoadMetadata(entryDirectory);
        string directSourcePageUrl = ResolveDirectSourcePageUrl(sourcePageUrl, metadata);
        if (string.IsNullOrWhiteSpace(directSourcePageUrl))
        {
            return new GameSourceMetadata();
        }

        List<string> sourceTags = NormalizeTagList(metadata?.SourceTags);
        if (sourceTags.Count == 0)
        {
            sourceTags = FetchSourceTags(directSourcePageUrl);
            if (metadata != null)
            {
                metadata.SourcePageUrl = directSourcePageUrl;
                metadata.SourceTags = sourceTags;
                SaveMetadata(entryDirectory, metadata);
            }
            else if (sourceTags.Count > 0)
            {
                SaveMetadata(entryDirectory, new IconFetchMetadata
                {
                    SourcePageUrl = directSourcePageUrl,
                    SourceTags = sourceTags
                });
            }
        }

        return new GameSourceMetadata
        {
            SourcePageUrl = directSourcePageUrl,
            DefaultTags = sourceTags
        };
    }

    public GameIconFetchDiagnostics ResolveFetchDiagnostics(string gameName, string gameRootDirectory)
    {
        string entryDirectory = GetEntryDirectory(gameName, gameRootDirectory);
        IconFetchMetadata metadata = LoadMetadata(entryDirectory);
        if (!HasFetchFailure(metadata))
        {
            return new GameIconFetchDiagnostics();
        }

        string diagnosticsSourcePageUrl = FirstNonEmpty(metadata.LastAttemptedSourcePageUrl, metadata.SourcePageUrl);
        if (!IsDirectSourcePageUrl(diagnosticsSourcePageUrl))
        {
            return new GameIconFetchDiagnostics();
        }

        return new GameIconFetchDiagnostics
        {
            LastAttemptedSource = FirstNonEmpty(metadata.LastAttemptedSource, metadata.Source),
            SourcePageUrl = diagnosticsSourcePageUrl,
            RequestUrl = FirstNonEmpty(metadata.LastAttemptedUrl, metadata.ImageUrl),
            HttpStatus = metadata.LastHttpStatusCode,
            SavedError = metadata.LastError ?? string.Empty
        };
    }

    private IconFetchMetadata FetchAndCacheIconFromSourcePage(string sourcePageUrl, string entryDirectory)
    {
        var fetchContext = new IconFetchContext();

        try
        {
            string html = GetStringSafe(sourcePageUrl, GetSourceName(sourcePageUrl), fetchContext);
            IconFetchMetadata metadata = IsDlsiteProductUrl(sourcePageUrl)
                ? TryFetchFromDlsiteProductPage(entryDirectory, sourcePageUrl, html, fetchContext)
                : TryFetchFromKimochiArticleCandidate(
                    entryDirectory,
                    BuildArticleIconCandidate(sourcePageUrl, html),
                    fetchContext,
                    GetSourceName(sourcePageUrl));

            if (metadata != null)
            {
                return metadata;
            }

            return new IconFetchMetadata
            {
                LastAttemptUtc = DateTime.UtcNow,
                Success = false,
                SourcePageUrl = sourcePageUrl,
                LastError = "No matching remote icon was found on the source page."
            }.WithDiagnostics(fetchContext);
        }
        catch (Exception ex)
        {
            return new IconFetchMetadata
            {
                LastAttemptUtc = DateTime.UtcNow,
                Success = false,
                SourcePageUrl = sourcePageUrl,
                LastError = ex.Message
            }.WithDiagnostics(fetchContext);
        }
    }

    private static string ResolveDirectSourcePageUrl(string sourcePageUrl, IconFetchMetadata metadata)
    {
        foreach (string candidate in new[] { sourcePageUrl, metadata?.SourcePageUrl })
        {
            string trimmed = candidate?.Trim();
            if (IsDirectSourcePageUrl(trimmed))
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values?
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static bool IsDirectSourcePageUrl(string sourcePageUrl)
    {
        if (!IsLikelyHtmlSourceUrl(sourcePageUrl) ||
            !Uri.TryCreate(sourcePageUrl, UriKind.Absolute, out Uri uri))
        {
            return false;
        }

        string host = uri.Host ?? string.Empty;
        if (host.Equals("kimochi.info", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".kimochi.info", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(uri.Query) &&
                Regex.IsMatch(uri.Query, @"(?:^|[?&])s=", RegexOptions.IgnoreCase))
            {
                return false;
            }

            string relative = uri.AbsolutePath.TrimStart('/');
            string[] blockedPrefixes =
            {
                "browse/",
                "category/",
                "author/",
                "tag/",
                "search/",
                "page/",
                "feed",
                "wp-json",
                "xmlrpc.php",
                "faqs",
                "dmca"
            };

            return !string.IsNullOrWhiteSpace(relative) &&
                !blockedPrefixes.Any(prefix => relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        if (host.Equals("www.dlsite.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".dlsite.com", StringComparison.OrdinalIgnoreCase))
        {
            return IsDlsiteProductUrl(sourcePageUrl);
        }

        return true;
    }

    private static bool IsDlsiteProductUrl(string sourcePageUrl)
    {
        return Regex.IsMatch(
            sourcePageUrl ?? string.Empty,
            @"^https?://www\.dlsite\.com/(?:maniax|pro)/work/=/product_id/[A-Z]{2}[0-9]{6,}\.html",
            RegexOptions.IgnoreCase);
    }

    private static string GetSourceName(string sourcePageUrl)
    {
        if (!Uri.TryCreate(sourcePageUrl, UriKind.Absolute, out Uri uri))
        {
            return "metadata";
        }

        string host = uri.Host ?? string.Empty;
        if (host.EndsWith("dlsite.com", StringComparison.OrdinalIgnoreCase))
        {
            return "dlsite";
        }

        if (host.EndsWith("kimochi.info", StringComparison.OrdinalIgnoreCase))
        {
            return "kimochi";
        }

        return "metadata";
    }

    private static IconFetchMetadata TryFetchFromKimochiArticleCandidate(
        string entryDirectory,
        ArticleIconCandidate articleCandidate,
        IconFetchContext fetchContext,
        string source = "kimochi")
    {
        if (articleCandidate == null)
        {
            return null;
        }

        ImageDownloadResult downloadedFromKimochi = DownloadImageToCache(
            articleCandidate.ImageUrl,
            entryDirectory,
            source,
            articleCandidate.ArticleUrl,
            fetchContext);
        if (!string.IsNullOrWhiteSpace(downloadedFromKimochi.IconPath))
        {
            return new IconFetchMetadata
            {
                LastAttemptUtc = DateTime.UtcNow,
                Success = true,
                Source = source,
                SourcePageUrl = articleCandidate.ArticleUrl,
                ImageUrl = articleCandidate.ImageUrl,
                SourceTags = articleCandidate.SourceTags
            };
        }

        string dlsiteImageUrl = ResolveDlsiteImageUrl(articleCandidate, fetchContext);
        ImageDownloadResult downloadedFromDlsite = DownloadImageToCache(
            dlsiteImageUrl,
            entryDirectory,
            "dlsite",
            articleCandidate.ArticleUrl,
            fetchContext);
        if (!string.IsNullOrWhiteSpace(downloadedFromDlsite.IconPath))
        {
            return new IconFetchMetadata
            {
                LastAttemptUtc = DateTime.UtcNow,
                Success = true,
                Source = "dlsite",
                SourcePageUrl = articleCandidate.ArticleUrl,
                ImageUrl = dlsiteImageUrl,
                SourceTags = articleCandidate.SourceTags
            };
        }

        return null;
    }

    private static IconFetchMetadata TryFetchFromDlsiteProductPage(
        string entryDirectory,
        string productUrl,
        string productHtml,
        IconFetchContext fetchContext)
    {
        string imageUrl = FirstNonEmpty(
            ExtractMetaContent(productHtml, "og:image"),
            ExtractMetaContent(productHtml, "twitter:image"));
        ImageDownloadResult downloadedFromDlsite = DownloadImageToCache(
            imageUrl,
            entryDirectory,
            "dlsite",
            productUrl,
            fetchContext);
        if (string.IsNullOrWhiteSpace(downloadedFromDlsite.IconPath))
        {
            return null;
        }

        return new IconFetchMetadata
        {
            LastAttemptUtc = DateTime.UtcNow,
            Success = true,
            Source = "dlsite",
            SourcePageUrl = productUrl,
            ImageUrl = imageUrl,
            SourceTags = ExtractSourceTags(productHtml)
        };
    }

    private static ArticleIconCandidate BuildArticleIconCandidate(string articleUrl, string html)
    {
        return new ArticleIconCandidate
        {
            ArticleUrl = articleUrl,
            ImageUrl = FirstNonEmpty(
                ExtractMetaContent(html, "og:image"),
                ExtractMetaContent(html, "twitter:image"),
                ExtractArticleImageUrl(articleUrl, html)),
            DlsiteProductUrl = ExtractDlsiteProductUrl(html),
            SourceTags = ExtractSourceTags(html)
        };
    }

    private static bool HasFetchFailure(IconFetchMetadata metadata)
    {
        return metadata?.Success == false &&
            (metadata.LastAttemptUtc != default ||
                !string.IsNullOrWhiteSpace(metadata.LastError) ||
                !string.IsNullOrWhiteSpace(metadata.LastAttemptedUrl));
    }

    private static IconFetchMetadata SaveArticleSourceMetadata(
        string entryDirectory,
        string articleUrl,
        IEnumerable<string> sourceTags)
    {
        IconFetchMetadata metadata = LoadMetadata(entryDirectory) ?? new IconFetchMetadata();
        List<string> normalizedTags = NormalizeTagList(sourceTags);
        bool shouldSave = false;

        if (IsDirectSourcePageUrl(articleUrl) &&
            !string.Equals(metadata.SourcePageUrl, articleUrl, StringComparison.OrdinalIgnoreCase))
        {
            metadata.SourcePageUrl = articleUrl;
            shouldSave = true;
        }

        if (normalizedTags.Count > 0 &&
            !AreEquivalentTags(metadata.SourceTags, normalizedTags))
        {
            metadata.SourceTags = normalizedTags;
            shouldSave = true;
        }

        if (shouldSave)
        {
            SaveMetadata(entryDirectory, metadata);
        }

        return metadata;
    }

    private static bool AreEquivalentTags(IEnumerable<string> left, IEnumerable<string> right)
    {
        List<string> normalizedLeft = NormalizeTagList(left);
        List<string> normalizedRight = NormalizeTagList(right);
        return normalizedLeft.Count == normalizedRight.Count &&
            normalizedLeft.SequenceEqual(normalizedRight, StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveDlsiteImageUrl(ArticleIconCandidate candidate, IconFetchContext fetchContext)
    {
        if (candidate == null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(candidate.DlsiteProductUrl))
        {
            return ExtractDlsiteImageUrlFromProductPage(candidate.DlsiteProductUrl, fetchContext);
        }

        return string.Empty;
    }

    private static string ExtractDlsiteProductUrl(string html)
    {
        Match linkMatch = Regex.Match(
            html ?? string.Empty,
            "(?<url>https?://www\\.dlsite\\.com/(?:maniax|pro)/work/=/product_id/[A-Z]{2}[0-9]{6,}\\.html(?:\\?[^\"'\\s<>]*)?)",
            RegexOptions.IgnoreCase);
        if (linkMatch.Success)
        {
            return linkMatch.Groups["url"].Value;
        }

        string titleText = string.Join(
            " ",
            ExtractMetaContent(html, "og:title"),
            ExtractMetaContent(html, "twitter:title"),
            ExtractElementText(html, "title"),
            ExtractElementText(html, "h1"));
        Match idMatch = Regex.Match(
            titleText,
            "(?<![A-Z0-9])(?<id>(?:RJ|VJ|BJ|RE)[0-9]{6,})(?![A-Z0-9])",
            RegexOptions.IgnoreCase);
        if (!idMatch.Success)
        {
            return string.Empty;
        }

        return DlsiteManiaxBaseUrl + "/work/=/product_id/" + idMatch.Groups["id"].Value.ToUpperInvariant() + ".html";
    }

    private static string ExtractElementText(string html, string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return string.Empty;
        }

        Match match = Regex.Match(
            html ?? string.Empty,
            "<" + Regex.Escape(tagName) + "\\b[^>]*>(?<text>[\\s\\S]*?)</" + Regex.Escape(tagName) + ">",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return string.Empty;
        }

        string text = Regex.Replace(match.Groups["text"].Value, "<.*?>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(text), "\\s+", " ").Trim();
    }

    private static string ExtractDlsiteImageUrlFromProductPage(string productUrl, IconFetchContext fetchContext)
    {
        string html = GetStringSafe(productUrl, "dlsite", fetchContext);
        return ExtractMetaContent(html, "og:image");
    }

    private static string ExtractMetaContent(string html, string propertyName)
    {
        foreach (Match match in Regex.Matches(html ?? string.Empty, "<meta\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            Dictionary<string, string> attributes = ExtractHtmlAttributes(match.Value);
            string name = FirstNonEmpty(GetAttribute(attributes, "property"), GetAttribute(attributes, "name"));
            if (!string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string content = GetAttribute(attributes, "content");
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }
        }

        return string.Empty;
    }

    private static string ExtractArticleImageUrl(string articleUrl, string html)
    {
        foreach (Match match in Regex.Matches(html ?? string.Empty, "<img\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            Dictionary<string, string> attributes = ExtractHtmlAttributes(match.Value);
            if (IsIgnoredArticleImage(attributes))
            {
                continue;
            }

            string candidate = FirstNonEmpty(
                GetAttribute(attributes, "data-src"),
                GetAttribute(attributes, "data-lazy-src"),
                GetAttribute(attributes, "src"),
                ExtractFirstSrcSetUrl(GetAttribute(attributes, "data-srcset")),
                ExtractFirstSrcSetUrl(GetAttribute(attributes, "srcset")));
            string resolved = ResolveImageUrl(articleUrl, candidate);
            if (IsLikelyImageUrl(resolved))
            {
                return resolved;
            }
        }

        return string.Empty;
    }

    private static bool IsIgnoredArticleImage(IReadOnlyDictionary<string, string> attributes)
    {
        string id = GetAttribute(attributes, "id");
        if (string.Equals(id, "gallery-lightbox-img", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string className = GetAttribute(attributes, "class");
        return className.IndexOf("avatar", StringComparison.OrdinalIgnoreCase) >= 0 ||
            className.IndexOf("logo", StringComparison.OrdinalIgnoreCase) >= 0 ||
            className.IndexOf("emoji", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ExtractFirstSrcSetUrl(string srcset)
    {
        if (string.IsNullOrWhiteSpace(srcset))
        {
            return string.Empty;
        }

        foreach (string candidate in srcset.Split(','))
        {
            string url = candidate.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return string.Empty;
    }

    private static Dictionary<string, string> ExtractHtmlAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(
            tag ?? string.Empty,
            "(?<name>[A-Za-z_:][A-Za-z0-9_:\\.-]*)\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s\"'=<>]+))",
            RegexOptions.IgnoreCase))
        {
            attributes[match.Groups["name"].Value] = WebUtility.HtmlDecode(match.Groups["value"].Value).Trim();
        }

        return attributes;
    }

    private static string GetAttribute(IReadOnlyDictionary<string, string> attributes, string name)
    {
        return attributes != null && attributes.TryGetValue(name, out string value)
            ? value
            : string.Empty;
    }

    private static string ResolveImageUrl(string baseUrl, string imageUrl)
    {
        string value = imageUrl?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            string scheme = Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri baseUri)
                ? baseUri.Scheme
                : Uri.UriSchemeHttps;
            value = scheme + ":" + value;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out Uri absoluteUri))
        {
            return IsHttpUrl(absoluteUri) ? absoluteUri.ToString() : string.Empty;
        }

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri sourceUri) &&
            Uri.TryCreate(sourceUri, value, out Uri resolvedUri) &&
            IsHttpUrl(resolvedUri))
        {
            return resolvedUri.ToString();
        }

        return string.Empty;
    }

    private static bool IsLikelyImageUrl(string imageUrl)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri uri) || !IsHttpUrl(uri))
        {
            return false;
        }

        string extension = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return true;
        }

        string[] allowedExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".ico", ".bmp" };
        return allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsHttpUrl(Uri uri)
    {
        return uri != null &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static List<string> FetchSourceTags(string sourcePageUrl)
    {
        if (!IsDirectSourcePageUrl(sourcePageUrl))
        {
            return new List<string>();
        }

        try
        {
            string html = GetStringSafe(sourcePageUrl, "metadata", new IconFetchContext());
            return ExtractSourceTags(html);
        }
        catch
        {
            return new List<string>();
        }
    }

    private static List<string> ExtractSourceTags(string html)
    {
        IEnumerable<string> metaTags = Regex.Matches(
                html ?? string.Empty,
                "<meta[^>]+property=[\"']article:tag[\"'][^>]+content=[\"'](?<tag>[^\"']+)[\"']",
                RegexOptions.IgnoreCase)
            .OfType<Match>()
            .Select(match => WebUtility.HtmlDecode(match.Groups["tag"].Value));

        IEnumerable<string> linkTags = Regex.Matches(
                html ?? string.Empty,
                "<a[^>]+href=[\"'][^\"']*/tag/[^\"']*[\"'][^>]*>(?<tag>.*?)</a>",
                RegexOptions.IgnoreCase)
            .OfType<Match>()
            .Select(match => Regex.Replace(WebUtility.HtmlDecode(match.Groups["tag"].Value), "<.*?>", string.Empty));

        return NormalizeTagList(metaTags.Concat(linkTags));
    }

    private static bool IsLikelyHtmlSourceUrl(string sourcePageUrl)
    {
        if (string.IsNullOrWhiteSpace(sourcePageUrl) || !Uri.TryCreate(sourcePageUrl, UriKind.Absolute, out Uri uri))
        {
            return false;
        }

        string extension = Path.GetExtension(uri.AbsolutePath);
        return string.IsNullOrWhiteSpace(extension) ||
               string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> NormalizeTagList(IEnumerable<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();

        foreach (string tag in tags ?? Enumerable.Empty<string>())
        {
            string cleaned = Regex.Replace(tag ?? string.Empty, "\\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(cleaned) || !seen.Add(cleaned))
            {
                continue;
            }

            results.Add(cleaned);
        }

        return results;
    }

    private static ImageDownloadResult DownloadImageToCache(
        string imageUrl,
        string entryDirectory,
        string source,
        string sourcePageUrl,
        IconFetchContext fetchContext)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return ImageDownloadResult.Empty;
        }

        try
        {
            Directory.CreateDirectory(entryDirectory);
            DeleteCachedIconFiles(entryDirectory);

            foreach (string refererUrl in BuildImageRefererAttempts(imageUrl, sourcePageUrl))
            {
                try
                {
                    using HttpResponseMessage response = SendGetWithDiagnostics(
                        imageUrl,
                        source,
                        sourcePageUrl,
                        fetchContext,
                        HttpCompletionOption.ResponseHeadersRead,
                        refererUrl);

                    if (!response.IsSuccessStatusCode)
                    {
                        fetchContext.LastError = BuildHttpStatusError(response);
                        continue;
                    }

                    string extension = GetImageExtension(response, imageUrl);
                    if (string.IsNullOrWhiteSpace(extension))
                    {
                        extension = ".jpg";
                    }

                    string iconPath = Path.Combine(entryDirectory, "icon" + extension);
                    using Stream responseStream = response.Content.ReadAsStream();
                    using FileStream destination = new(iconPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    responseStream.CopyTo(destination);
                    return new ImageDownloadResult(iconPath, string.Empty);
                }
                catch (Exception ex)
                {
                    fetchContext.LastError = ex.Message;
                }
            }

            return new ImageDownloadResult(string.Empty, fetchContext.LastError);
        }
        catch (Exception ex)
        {
            fetchContext.LastError = ex.Message;
            return new ImageDownloadResult(string.Empty, ex.Message);
        }
    }

    private static string GetImageExtension(HttpResponseMessage response, string imageUrl)
    {
        string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        switch (mediaType.ToLowerInvariant())
        {
            case "image/png":
                return ".png";
            case "image/webp":
                return ".webp";
            case "image/jpeg":
                return ".jpg";
            case "image/x-icon":
            case "image/vnd.microsoft.icon":
                return ".ico";
        }

        string extension = Path.GetExtension(new Uri(imageUrl).AbsolutePath);
        return string.IsNullOrWhiteSpace(extension) ? string.Empty : extension;
    }

    private static string GetStringSafe(string url, string source, IconFetchContext fetchContext)
    {
        using HttpResponseMessage response = SendGetWithDiagnostics(
            url,
            source,
            url,
            fetchContext,
            HttpCompletionOption.ResponseContentRead,
            refererUrl: null);
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static HttpResponseMessage SendGetWithDiagnostics(
        string url,
        string source,
        string sourcePageUrl,
        IconFetchContext fetchContext,
        HttpCompletionOption completionOption,
        string refererUrl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        Uri referer = CreateRefererUri(refererUrl);
        if (referer != null)
        {
            request.Headers.Referrer = referer;
        }

        fetchContext.RecordRequest(source, sourcePageUrl, url, HttpClient, request);
        HttpResponseMessage response = HttpClient.Send(request, completionOption);
        fetchContext.RecordResponse(response);
        return response;
    }

    private static IEnumerable<string> BuildImageRefererAttempts(string imageUrl, string sourcePageUrl)
    {
        string sourceReferer = CreateRefererUri(sourcePageUrl)?.ToString() ?? string.Empty;
        string originReferer = GetOriginRefererUrl(imageUrl);

        return new[] { sourceReferer, originReferer, string.Empty }
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static Uri CreateRefererUri(string refererUrl)
    {
        if (string.IsNullOrWhiteSpace(refererUrl) ||
            !Uri.TryCreate(refererUrl, UriKind.Absolute, out Uri referer) ||
            (referer.Scheme != Uri.UriSchemeHttp && referer.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return referer;
    }

    private static string GetOriginRefererUrl(string imageUrl)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri imageUri) ||
            (imageUri.Scheme != Uri.UriSchemeHttp && imageUri.Scheme != Uri.UriSchemeHttps))
        {
            return string.Empty;
        }

        return imageUri.GetLeftPart(UriPartial.Authority) + "/";
    }

    private static string BuildHttpStatusError(HttpResponseMessage response)
    {
        if (response == null)
        {
            return string.Empty;
        }

        return "Response status code does not indicate success: " +
            (int)response.StatusCode +
            " (" +
            response.ReasonPhrase +
            ").";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        });
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        return client;
    }

    private string GetEntryDirectory(string gameName, string gameRootDirectory)
    {
        string safeName = SanitizeFileName(gameName, "game");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gameRootDirectory))).Substring(0, 12);
        return Path.Combine(cacheDirectory, safeName + "_" + hash);
    }

    private static bool ShouldAttemptFetch(IconFetchMetadata metadata)
    {
        if (metadata == null)
        {
            return true;
        }

        if (metadata.Success)
        {
            return false;
        }

        return metadata.LastAttemptUtc < DateTime.UtcNow.AddDays(-7);
    }

    private static string TryGetCachedIconPath(string entryDirectory)
    {
        if (!Directory.Exists(entryDirectory))
        {
            return string.Empty;
        }

        return Directory
            .EnumerateFiles(entryDirectory, "icon.*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? string.Empty;
    }

    private static void DeleteCachedIconFiles(string entryDirectory)
    {
        if (!Directory.Exists(entryDirectory))
        {
            return;
        }

        foreach (string filePath in Directory.EnumerateFiles(entryDirectory, "icon.*", SearchOption.TopDirectoryOnly))
        {
            File.Delete(filePath);
        }
    }

    private static IconFetchMetadata LoadMetadata(string entryDirectory)
    {
        string path = Path.Combine(entryDirectory, "metadata.json");
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonConvert.DeserializeObject<IconFetchMetadata>(File.ReadAllText(path));
    }

    private static void SaveMetadata(string entryDirectory, IconFetchMetadata metadata)
    {
        Directory.CreateDirectory(entryDirectory);
        File.WriteAllText(
            Path.Combine(entryDirectory, "metadata.json"),
            JsonConvert.SerializeObject(metadata, Formatting.Indented));
    }

}
