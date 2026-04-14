using Newtonsoft.Json;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GameLibrary.Net8;

public class GameIconPipelineService
{
    private const string KimochiBaseUrl = "https://kimochi.info";
    private const string DlsiteManiaxBaseUrl = "https://www.dlsite.com/maniax";
    private const string DlsiteProBaseUrl = "https://www.dlsite.com/pro";
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
        Directory.CreateDirectory(cacheDirectory);

        string entryDirectory = GetEntryDirectory(gameName, gameRootDirectory);
        string cachedIconPath = TryGetCachedIconPath(entryDirectory);
        if (!string.IsNullOrWhiteSpace(cachedIconPath))
        {
            return new GameIconResolveResult(cachedIconPath, attemptedRemoteFetch: false);
        }

        IconFetchMetadata metadata = LoadMetadata(entryDirectory);
        if (!allowRemoteFetch || !EnableRemoteFetch || !ShouldAttemptFetch(metadata))
        {
            return new GameIconResolveResult(string.Empty, attemptedRemoteFetch: false);
        }

        IconFetchMetadata updatedMetadata = FetchAndCacheIcon(gameName, entryDirectory);
        SaveMetadata(entryDirectory, updatedMetadata);

        string iconPath = TryGetCachedIconPath(entryDirectory);
        return new GameIconResolveResult(iconPath, attemptedRemoteFetch: true);
    }

    private IconFetchMetadata FetchAndCacheIcon(string gameName, string entryDirectory)
    {
        try
        {
            foreach (string query in BuildSearchQueries(gameName))
            {
                List<string> articleUrls = SearchKimochiArticleUrls(query);
                foreach (string articleUrl in articleUrls)
                {
                    ArticleIconCandidate articleCandidate = GetArticleIconCandidate(articleUrl);
                    if (!articleCandidate.IsMatchFor(gameName))
                    {
                        continue;
                    }

                    string downloadedFromKimochi = DownloadImageToCache(articleCandidate.ImageUrl, entryDirectory);
                    if (!string.IsNullOrWhiteSpace(downloadedFromKimochi))
                    {
                        return new IconFetchMetadata
                        {
                            LastAttemptUtc = DateTime.UtcNow,
                            Success = true,
                            Source = "kimochi",
                            SourcePageUrl = articleUrl,
                            ImageUrl = articleCandidate.ImageUrl
                        };
                    }

                    string dlsiteImageUrl = ResolveDlsiteImageUrl(articleCandidate);
                    string downloadedFromDlsite = DownloadImageToCache(dlsiteImageUrl, entryDirectory);
                    if (!string.IsNullOrWhiteSpace(downloadedFromDlsite))
                    {
                        return new IconFetchMetadata
                        {
                            LastAttemptUtc = DateTime.UtcNow,
                            Success = true,
                            Source = "dlsite",
                            SourcePageUrl = articleCandidate.DlsiteProductUrl,
                            ImageUrl = dlsiteImageUrl
                        };
                    }
                }

                string directDlsiteImageUrl = SearchDlsiteImageUrl(query, gameName);
                string downloadedDirectDlsite = DownloadImageToCache(directDlsiteImageUrl, entryDirectory);
                if (!string.IsNullOrWhiteSpace(downloadedDirectDlsite))
                {
                    return new IconFetchMetadata
                    {
                        LastAttemptUtc = DateTime.UtcNow,
                        Success = true,
                        Source = "dlsite",
                        SourcePageUrl = directDlsiteImageUrl,
                        ImageUrl = directDlsiteImageUrl
                    };
                }
            }

            return new IconFetchMetadata
            {
                LastAttemptUtc = DateTime.UtcNow,
                Success = false,
                LastError = "No matching remote icon was found."
            };
        }
        catch (Exception ex)
        {
            return new IconFetchMetadata
            {
                LastAttemptUtc = DateTime.UtcNow,
                Success = false,
                LastError = ex.Message
            };
        }
    }

    private static IEnumerable<string> BuildSearchQueries(string gameName)
    {
        string normalized = NormalizeForSearch(gameName);
        return new[] { gameName?.Trim(), normalized }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeForSearch(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Normalize(NormalizationForm.FormKC);
        normalized = Regex.Replace(normalized, @"[\[\(【（].*?[\]\)】）]", " ");
        normalized = Regex.Replace(normalized, @"(?:ver(?:sion)?|v)\s*\d+(?:\.\d+)*", " ", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\b(?:jp|eng|japanese|english|win|windows|x64|x86|dlc)\b", " ", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"[_\-~]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized;
    }

    private static List<string> SearchKimochiArticleUrls(string query)
    {
        string url = KimochiBaseUrl + "/search/" + Uri.EscapeDataString(query) + "/";
        string html = GetStringSafe(url);
        return ExtractKimochiArticleUrls(html);
    }

    private static List<string> ExtractKimochiArticleUrls(string html)
    {
        List<string> results = [];

        foreach (Match articleMatch in Regex.Matches(
            html ?? string.Empty,
            "<article\\b[\\s\\S]*?</article>",
            RegexOptions.IgnoreCase))
        {
            Match hrefMatch = Regex.Match(
                articleMatch.Value,
                "entry-title[\\s\\S]*?<a[^>]*href=[\"'](?<href>https://kimochi\\.info/[^\"']+)[\"']",
                RegexOptions.IgnoreCase);
            if (!hrefMatch.Success)
            {
                continue;
            }

            string href = WebUtility.HtmlDecode(hrefMatch.Groups["href"].Value);
            if (IsLikelyKimochiArticleUrl(href))
            {
                results.Add(href);
            }
        }

        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private static bool IsLikelyKimochiArticleUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        string relative = url.Replace(KimochiBaseUrl + "/", string.Empty, StringComparison.OrdinalIgnoreCase);
        string[] blockedPrefixes =
        {
            "category/",
            "author/",
            "tag/",
            "search/",
            "feed",
            "wp-json",
            "xmlrpc.php",
            "faqs",
            "dmca"
        };

        return !blockedPrefixes.Any(prefix => relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static ArticleIconCandidate GetArticleIconCandidate(string articleUrl)
    {
        string html = GetStringSafe(articleUrl);
        return new ArticleIconCandidate
        {
            ArticleUrl = articleUrl,
            Title = ExtractMetaContent(html, "og:title"),
            ImageUrl = ExtractMetaContent(html, "og:image"),
            DlsiteProductUrl = ExtractDlsiteProductUrl(html)
        };
    }

    private static string ResolveDlsiteImageUrl(ArticleIconCandidate candidate)
    {
        if (candidate == null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(candidate.DlsiteProductUrl))
        {
            return ExtractDlsiteImageUrlFromProductPage(candidate.DlsiteProductUrl);
        }

        return string.Empty;
    }

    private static string SearchDlsiteImageUrl(string query, string gameName)
    {
        foreach (string baseUrl in new[] { DlsiteManiaxBaseUrl, DlsiteProBaseUrl })
        {
            string searchUrl = baseUrl + "/fsr/=/keyword/" + Uri.EscapeDataString(query);
            string html = GetStringSafe(searchUrl);
            List<string> productUrls = ExtractDlsiteProductUrls(html, baseUrl);

            string bestImageUrl = string.Empty;
            int bestScore = 0;
            foreach (string productUrl in productUrls)
            {
                string productHtml = GetStringSafe(productUrl);
                string title = ExtractMetaContent(productHtml, "og:title");
                int score = ComputeTitleMatchScore(title, gameName);
                if (score < bestScore)
                {
                    continue;
                }

                string imageUrl = ExtractMetaContent(productHtml, "og:image");
                if (string.IsNullOrWhiteSpace(imageUrl))
                {
                    continue;
                }

                bestScore = score;
                bestImageUrl = imageUrl;
            }

            if (!string.IsNullOrWhiteSpace(bestImageUrl))
            {
                return bestImageUrl;
            }
        }

        return string.Empty;
    }

    private static List<string> ExtractDlsiteProductUrls(string html, string baseUrl)
    {
        return Regex.Matches(html ?? string.Empty, "product_id/(?<id>[A-Z]{2}[0-9]{6,})", RegexOptions.IgnoreCase)
            .OfType<Match>()
            .Select(match => match.Groups["id"].Value.ToUpperInvariant())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(id => baseUrl + "/work/=/product_id/" + id + ".html")
            .ToList();
    }

    private static string ExtractDlsiteProductUrl(string html)
    {
        Match linkMatch = Regex.Match(
            html ?? string.Empty,
            "(?<url>https?://www\\.dlsite\\.com/(?:maniax|pro)/work/=/product_id/[A-Z]{2}[0-9]{6,}\\.html)",
            RegexOptions.IgnoreCase);
        if (linkMatch.Success)
        {
            return linkMatch.Groups["url"].Value;
        }

        Match idMatch = Regex.Match(html ?? string.Empty, "(?<id>[A-Z]{2}[0-9]{6,})", RegexOptions.IgnoreCase);
        if (!idMatch.Success)
        {
            return string.Empty;
        }

        return DlsiteManiaxBaseUrl + "/work/=/product_id/" + idMatch.Groups["id"].Value.ToUpperInvariant() + ".html";
    }

    private static string ExtractDlsiteImageUrlFromProductPage(string productUrl)
    {
        string html = GetStringSafe(productUrl);
        return ExtractMetaContent(html, "og:image");
    }

    private static string ExtractMetaContent(string html, string propertyName)
    {
        Match match = Regex.Match(
            html ?? string.Empty,
            "<meta[^>]+(?:property|name)=[\"']" + Regex.Escape(propertyName) + "[\"'][^>]+content=[\"'](?<content>[^\"']+)[\"']",
            RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["content"].Value) : string.Empty;
    }

    private static int ComputeTitleMatchScore(string candidateTitle, string gameName)
    {
        string left = NormalizeComparisonText(candidateTitle);
        string right = NormalizeComparisonText(gameName);
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 100;
        }

        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal))
        {
            return 80;
        }

        string[] leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int overlap = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        return overlap * 10;
    }

    private static string NormalizeComparisonText(string value)
    {
        string normalized = NormalizeForSearch(value);
        normalized = Regex.Replace(normalized, @"download free hentai game porn games", " ", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"kimochi gaming.*$", " ", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim().ToLowerInvariant();
        return normalized;
    }

    private static string DownloadImageToCache(string imageUrl, string entryDirectory)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return string.Empty;
        }

        try
        {
            Directory.CreateDirectory(entryDirectory);
            DeleteCachedIconFiles(entryDirectory);

            using HttpResponseMessage response = HttpClient.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            string extension = GetImageExtension(response, imageUrl);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".jpg";
            }

            string iconPath = Path.Combine(entryDirectory, "icon" + extension);
            using Stream source = response.Content.ReadAsStream();
            using FileStream destination = new(iconPath, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(destination);
            return iconPath;
        }
        catch
        {
            return string.Empty;
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

    private static string GetStringSafe(string url)
    {
        using HttpResponseMessage response = HttpClient.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
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
        string safeName = SanitizeFileName(gameName);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(gameRootDirectory))).Substring(0, 12);
        return Path.Combine(cacheDirectory, safeName + "_" + hash);
    }

    private static string SanitizeFileName(string value)
    {
        string name = string.IsNullOrWhiteSpace(value) ? "game" : value;
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name.Trim();
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

    private sealed class IconFetchMetadata
    {
        public DateTime LastAttemptUtc { get; set; }

        public bool Success { get; set; }

        public string Source { get; set; }

        public string SourcePageUrl { get; set; }

        public string ImageUrl { get; set; }

        public string LastError { get; set; }
    }

    private sealed class ArticleIconCandidate
    {
        public string ArticleUrl { get; set; }

        public string Title { get; set; }

        public string ImageUrl { get; set; }

        public string DlsiteProductUrl { get; set; }

        public bool IsMatchFor(string gameName)
        {
            return ComputeTitleMatchScore(Title, gameName) >= 30;
        }
    }
}
