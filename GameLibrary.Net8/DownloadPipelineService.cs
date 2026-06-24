using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using static GameLibrary.Net8.DownloadEntryOperations;
using static GameLibrary.Net8.DownloadFileInspector;
using static GameLibrary.Net8.DownloadPipelineArtifacts;
using static GameLibrary.Net8.DownloadPipelineRecordSets;

namespace GameLibrary.Net8;

public class DownloadPipelineService
{
    private const string BaseUrl = "https://kimochi.info";
    private const int ProgressSaveInterval = 25;
    private const int MaxRetryAttempts = 3;
    private readonly GameIconPipelineService iconPipelineService;

    public DownloadPipelineService(GameIconPipelineService iconPipelineService = null)
    {
        this.iconPipelineService = iconPipelineService ?? new GameIconPipelineService(
            AppSettings.IconCacheDirectory,
            AppSettings.EnableRemoteIconFetch,
            AppSettings.RemoteIconFetchLimitPerRun);
    }

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

    public DownloadFailureRerunInfo GetFailureRerunInfo(string runtimeDirectory, int stepIndex)
    {
        runtimeDirectory = string.IsNullOrWhiteSpace(runtimeDirectory)
            ? AppSettings.RuntimeDirectory
            : runtimeDirectory;
        if (stepIndex < 2)
        {
            stepIndex = 2;
        }

        return stepIndex switch
        {
            2 => new DownloadFailureRerunInfo(
                stepIndex,
                HostLinkFailuresFile,
                GetUnresolvedHostLinkFailures(runtimeDirectory).Count),
            3 => new DownloadFailureRerunInfo(
                stepIndex,
                PrimaryResolvedFile,
                GetUnresolvedPrimaryLinkRecords(runtimeDirectory).Count),
            4 => new DownloadFailureRerunInfo(
                stepIndex,
                PrimaryFailuresFile,
                GetUnresolvedPrimaryDownloadFailures(runtimeDirectory).Count),
            5 => new DownloadFailureRerunInfo(
                stepIndex,
                PrimaryFailuresFile,
                GetMirrorFallbackCandidates(runtimeDirectory).Count),
            6 => new DownloadFailureRerunInfo(
                stepIndex,
                MirrorResolvedFile,
                GetUnresolvedMirrorLinkRecords(runtimeDirectory).Count),
            7 => new DownloadFailureRerunInfo(
                stepIndex,
                MirrorFailuresFile,
                GetUnresolvedMirrorDownloadFailures(runtimeDirectory).Count),
            _ => new DownloadFailureRerunInfo(stepIndex, UiText.Get("Downloader.NoFailureSet"), 0)
        };
    }

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

        DownloadRunState runState = CreateRunState(options);
        SaveRunState(options.RuntimeDirectory, runState);

        for (int i = 0; i < StepLabels.Count; i++)
        {
            if (i < options.StartStepIndex)
            {
                setStepStatus(i, UiText.Get("Status.Skipped"));
                continue;
            }

            if (options.FailedOnly && i < 2)
            {
                setStepStatus(i, UiText.Get("Status.Skipped"));
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            runState.LastStartedStepIndex = i;
            runState.UpdatedAt = DateTime.Now;
            SaveRunState(options.RuntimeDirectory, runState);

            setStepStatus(i, UiText.Get("Status.Running"));
            log(string.Empty);
            log(new string('=', 52));
            log("Step " + i + ": " + StepLabels[i]);
            if (options.FailedOnly)
            {
                DownloadFailureRerunInfo rerunInfo = GetFailureRerunInfo(options.RuntimeDirectory, i);
                log("Failed-only source: " + rerunInfo.FailureSetName + " (" + rerunInfo.UnresolvedCount + " unresolved)");
            }

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
                        await Step2ExtractHostLinksAsync(options.RuntimeDirectory, options.SaveDirectory, options.FailedOnly, log, cancellationToken);
                        break;
                    case 3:
                        await Step3ResolvePrimaryLinksAsync(options.RuntimeDirectory, options.FailedOnly, log, cancellationToken);
                        break;
                    case 4:
                        await Step4DownloadPrimaryArchivesAsync(options.RuntimeDirectory, options.SaveDirectory, options.FailedOnly, log, cancellationToken);
                        break;
                    case 5:
                        await Step5PrepareMirrorFallbackAsync(options.RuntimeDirectory, options.FailedOnly, log, cancellationToken);
                        break;
                    case 6:
                        await Step6ResolveMirrorLinksAsync(options.RuntimeDirectory, options.FailedOnly, log, cancellationToken);
                        break;
                    case 7:
                        await Step7DownloadMirrorArchivesAsync(options.RuntimeDirectory, options.SaveDirectory, options.FailedOnly, log, cancellationToken);
                        break;
                }

                runState.LastCompletedStepIndex = i;
                runState.UpdatedAt = DateTime.Now;
                SaveRunState(options.RuntimeDirectory, runState);
                setStepStatus(i, UiText.Get("Status.Done"));
            }
            catch
            {
                runState.UpdatedAt = DateTime.Now;
                SaveRunState(options.RuntimeDirectory, runState);
                setStepStatus(i, UiText.Get("Status.Error"));
                throw;
            }
        }
    }

    private static DownloadRunState CreateRunState(DownloadPipelineOptions options)
    {
        DateTime now = DateTime.Now;
        return new DownloadRunState
        {
            DateFrom = options.DateFrom?.Date,
            DateTo = options.DateTo?.Date,
            RequestedStartStepIndex = options.StartStepIndex,
            FailedOnly = options.FailedOnly,
            StartedAt = now,
            UpdatedAt = now
        };
    }

    private static void SaveRunState(string runtimeDirectory, DownloadRunState runState)
    {
        SaveJson(Path.Combine(runtimeDirectory, RunStateFile), runState);
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
            string pageUrl = BaseUrl + "/browse/page/" + page + "/";
            log("Fetching page " + page + ": " + pageUrl);
            string html = await RetryAsync(
                () => GetStringSafeAsync(client, pageUrl, cancellationToken),
                log,
                "fetch page " + page,
                cancellationToken);
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

                if (!urls.Contains(article.Item1, StringComparer.OrdinalIgnoreCase))
                {
                    urls.Add(article.Item1);
                }
            }

            log("Collected " + urls.Count + " article URL(s) so far.");
            SaveJson(Path.Combine(runtimeDirectory, ArticleUrlsFile), urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
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

    private async Task Step2ExtractHostLinksAsync(string runtimeDirectory, string saveDirectory, bool failedOnly, Action<string> log, CancellationToken cancellationToken)
    {
        List<string> articleUrls = failedOnly
            ? GetUnresolvedHostLinkFailures(runtimeDirectory)
                .Select(record => record.ArticleUrl)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : LoadJson<List<string>>(Path.Combine(runtimeDirectory, ArticleUrlsFile)) ?? [];
        List<DownloadEntry> records = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, DownloadRecordsFile)) ?? [];
        List<DownloadEntry> failures = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, HostLinkFailuresFile)) ?? [];

        if (!failedOnly)
        {
            records = records
                .Where(record => articleUrls.Any(url => IsSameDownloadEntry(record, new DownloadEntry { ArticleUrl = url })))
                .ToList();
            failures = failures
                .Where(record => articleUrls.Any(url => IsSameDownloadEntry(record, new DownloadEntry { ArticleUrl = url })))
                .ToList();
        }

        List<DownloadEntry> previouslyDownloadedRecords = GetPreviouslyDownloadedRecords(runtimeDirectory, saveDirectory);
        var skippedDownloadedRecords = new List<DownloadEntry>();
        log((failedOnly ? "Retrying " : "Processing ") + articleUrls.Count + " article URL(s).");
        if (previouslyDownloadedRecords.Count > 0)
        {
            log("Step 2 downloaded-history filter: " + previouslyDownloadedRecords.Count + " record(s).");
        }

        try
        {
            using HttpClient client = CreateHttpClient();
            for (int i = 0; i < articleUrls.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string articleUrl = articleUrls[i];
                DownloadEntry existingRecord = records.FirstOrDefault(record => IsSameDownloadEntry(record, new DownloadEntry { ArticleUrl = articleUrl }));
                DownloadEntry articleRecord = existingRecord ?? new DownloadEntry { ArticleUrl = articleUrl };
                if (TryMarkPreviouslyDownloaded(
                    articleRecord,
                    previouslyDownloadedRecords,
                    saveDirectory,
                    _ => { },
                    out DownloadEntry previouslyDownloadedRecord))
                {
                    DownloadEntry skippedRecord = MergeDownloadEntry(articleRecord, previouslyDownloadedRecord);
                    UpsertDownloadEntry(records, skippedRecord);
                    RemoveDownloadEntry(failures, skippedRecord);
                    UpsertDownloadEntry(skippedDownloadedRecords, skippedRecord);
                    log("[" + (i + 1) + "/" + articleUrls.Count + "] Skipped previously downloaded: " + DescribeDownloadEntry(skippedRecord));
                    continue;
                }

                if (existingRecord != null &&
                    HasExtractedHostLinks(existingRecord) &&
                    HasExtractedArticleMetadata(existingRecord))
                {
                    log("[" + (i + 1) + "/" + articleUrls.Count + "] Already extracted: " + articleUrl);
                    continue;
                }

                try
                {
                    string html = await RetryAsync(
                        () => GetStringSafeAsync(client, articleUrl, cancellationToken),
                        log,
                        "extract host links",
                        cancellationToken);
                    if (string.IsNullOrWhiteSpace(html))
                    {
                        log("[" + (i + 1) + "/" + articleUrls.Count + "] Empty article response: " + articleUrl);
                        continue;
                    }

                    string title = ExtractArticleTitle(html);
                    string fileName = SanitizeFileName(string.IsNullOrWhiteSpace(title) ? "unknown_title" : title);
                    string driveLink = ExtractHostLink(html, "Drive", articleUrl);
                    string mirrorLink = ExtractHostLink(html, "Mirror", articleUrl);
                    ArticleMetadataResult metadataResult = TryCacheArticleMetadata(fileName, articleUrl, html, log);

                    DownloadEntry nextRecord = MergeDownloadEntry(new DownloadEntry
                    {
                        Name = fileName,
                        ArticleUrl = articleUrl,
                        DriveIntermediateUrl = driveLink,
                        MirrorIntermediateUrl = mirrorLink,
                        IconPath = metadataResult.IconPath,
                        DefaultTags = metadataResult.DefaultTags
                    }, existingRecord);

                    if (TryMarkPreviouslyDownloaded(
                        nextRecord,
                        previouslyDownloadedRecords,
                        saveDirectory,
                        log,
                        out DownloadEntry downloadedRecord))
                    {
                        DownloadEntry skippedRecord = MergeDownloadEntry(nextRecord, downloadedRecord);
                        UpsertDownloadEntry(records, skippedRecord);
                        RemoveDownloadEntry(failures, skippedRecord);
                        UpsertDownloadEntry(skippedDownloadedRecords, skippedRecord);
                        log("[" + (i + 1) + "/" + articleUrls.Count + "] Skipped previously downloaded: " + DescribeDownloadEntry(skippedRecord));
                        continue;
                    }

                    UpsertDownloadEntry(records, nextRecord);
                    RemoveDownloadEntry(failures, new DownloadEntry { ArticleUrl = articleUrl });

                    log("[" + (i + 1) + "/" + articleUrls.Count + "] " + fileName);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    UpsertDownloadEntry(failures, new DownloadEntry
                    {
                        Name = articleUrl,
                        ArticleUrl = articleUrl,
                        FailureReason = ex.Message
                    });
                    log("Failed to extract host links: " + ex.Message);
                }

                if ((i + 1) % ProgressSaveInterval == 0)
                {
                    SaveJson(Path.Combine(runtimeDirectory, DownloadRecordsFile), records);
                    SaveJson(Path.Combine(runtimeDirectory, HostLinkFailuresFile), failures);
                }
            }
        }
        finally
        {
            SaveJson(Path.Combine(runtimeDirectory, DownloadRecordsFile), records);
            SaveJson(Path.Combine(runtimeDirectory, HostLinkFailuresFile), failures);
            RemoveDownloadEntriesFromFailureFile(runtimeDirectory, PrimaryFailuresFile, skippedDownloadedRecords);
            RemoveDownloadEntriesFromFailureFile(runtimeDirectory, MirrorFailuresFile, skippedDownloadedRecords);
        }

        if (skippedDownloadedRecords.Count > 0)
        {
            log("Skipped previously downloaded article(s) at step 2: " + skippedDownloadedRecords.Count);
        }

        log("Saved " + records.Count + " download record(s).");
        log("Host link extraction failures: " + failures.Count);
    }

    private async Task Step3ResolvePrimaryLinksAsync(string runtimeDirectory, bool failedOnly, Action<string> log, CancellationToken cancellationToken)
    {
        List<DownloadEntry> sourceRecords = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, DownloadRecordsFile)) ?? [];
        List<DownloadEntry> allRecords = MergeDownloadEntries(
            sourceRecords,
            LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, PrimaryResolvedFile)) ?? []);
        List<DownloadEntry> records = failedOnly
            ? allRecords.Where(IsUnresolvedPrimaryLinkRecord).ToList()
            : allRecords.Where(record => !IsDownloaded(record)).ToList();

        log((failedOnly ? "Retrying " : "Processing ") + records.Count + " primary link record(s).");

        try
        {
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

                if (!string.IsNullOrWhiteSpace(record.PrimaryUrl))
                {
                    log("[" + (i + 1) + "/" + records.Count + "] Already resolved primary URL for " + record.Name);
                    continue;
                }

                try
                {
                    record.PrimaryUrl = await RetryAsync(
                        () => ResolveIntermediateLinkAsync(client, record.DriveIntermediateUrl, cancellationToken),
                        log,
                        "resolve primary link",
                        cancellationToken);
                    record.FailureReason = null;
                    log("[" + (i + 1) + "/" + records.Count + "] Resolved primary URL for " + record.Name);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    record.FailureReason = ex.Message;
                    log("Primary link resolution failed for " + record.Name + ": " + ex.Message);
                }

                if ((i + 1) % ProgressSaveInterval == 0)
                {
                    SaveJson(Path.Combine(runtimeDirectory, PrimaryResolvedFile), allRecords);
                }
            }
        }
        finally
        {
            SaveJson(Path.Combine(runtimeDirectory, PrimaryResolvedFile), allRecords);
        }
    }

    private async Task Step4DownloadPrimaryArchivesAsync(string runtimeDirectory, string saveDirectory, bool failedOnly, Action<string> log, CancellationToken cancellationToken)
    {
        List<DownloadEntry> allRecords = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, PrimaryResolvedFile)) ?? [];
        List<DownloadEntry> records = failedOnly
            ? MergeDownloadEntries(GetUnresolvedPrimaryDownloadFailures(runtimeDirectory), allRecords)
            : allRecords.Where(record => !IsDownloaded(record)).ToList();
        List<DownloadEntry> failures = failedOnly
            ? LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, PrimaryFailuresFile)) ?? []
            : [];

        log((failedOnly ? "Retrying " : "Processing ") + records.Count + " primary download record(s).");

        try
        {
            for (int i = 0; i < records.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DownloadEntry record = records[i];

                if (string.IsNullOrWhiteSpace(record.PrimaryUrl))
                {
                    record.FailureReason = "Primary URL missing";
                    UpsertDownloadEntry(failures, record);
                    log("[" + (i + 1) + "/" + records.Count + "] No primary URL: " + record.Name);
                    continue;
                }

                if (TryUseExistingDownloadedFile(record, saveDirectory, log, "primary"))
                {
                    RemoveDownloadEntry(failures, record);
                    log("[" + (i + 1) + "/" + records.Count + "] Already downloaded primary: " + Path.GetFileName(record.DownloadedFilePath));
                    continue;
                }

                try
                {
                    record.DownloadedFilePath = await RetryAsync(
                        () => DownloadResolvedUrlAsync(record.Name, record.PrimaryUrl, saveDirectory, cancellationToken),
                        log,
                        "download primary",
                        cancellationToken);
                    record.FailureReason = null;
                    RemoveDownloadEntry(failures, record);
                    log("[" + (i + 1) + "/" + records.Count + "] Downloaded primary: " + Path.GetFileName(record.DownloadedFilePath));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    record.FailureReason = ex.Message;
                    UpsertDownloadEntry(failures, record);
                    log("Primary download failed for " + record.Name + ": " + ex.Message);
                }

                if ((i + 1) % ProgressSaveInterval == 0)
                {
                    SaveJson(Path.Combine(runtimeDirectory, PrimaryResolvedFile), allRecords);
                    SaveJson(Path.Combine(runtimeDirectory, PrimaryFailuresFile), failures);
                }
            }
        }
        finally
        {
            SaveJson(Path.Combine(runtimeDirectory, PrimaryResolvedFile), allRecords);
            SaveJson(Path.Combine(runtimeDirectory, PrimaryFailuresFile), failures);
        }

        log("Primary failures: " + failures.Count);
    }

    private Task Step5PrepareMirrorFallbackAsync(string runtimeDirectory, bool failedOnly, Action<string> log, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<DownloadEntry> mirrorCandidates = GetMirrorFallbackCandidates(runtimeDirectory);
        SaveJson(Path.Combine(runtimeDirectory, MirrorCandidatesFile), mirrorCandidates);
        log((failedOnly ? "Failed-only mirror fallback candidates: " : "Mirror fallback candidates: ") + mirrorCandidates.Count);
        return Task.CompletedTask;
    }

    private async Task Step6ResolveMirrorLinksAsync(string runtimeDirectory, bool failedOnly, Action<string> log, CancellationToken cancellationToken)
    {
        List<DownloadEntry> sourceRecords = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, MirrorCandidatesFile)) ?? [];
        List<DownloadEntry> allRecords = MergeDownloadEntries(
            sourceRecords,
            LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, MirrorResolvedFile)) ?? []);
        List<DownloadEntry> records = failedOnly
            ? allRecords.Where(IsUnresolvedMirrorLinkRecord).ToList()
            : allRecords;

        log((failedOnly ? "Retrying " : "Processing ") + records.Count + " mirror link record(s).");

        try
        {
            using HttpClient client = CreateHttpClient();
            for (int i = 0; i < records.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DownloadEntry record = records[i];
                if (string.IsNullOrWhiteSpace(record.MirrorIntermediateUrl))
                {
                    log("[" + (i + 1) + "/" + records.Count + "] No mirror link for " + record.Name);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(record.MirrorUrl))
                {
                    log("[" + (i + 1) + "/" + records.Count + "] Already resolved mirror URL for " + record.Name);
                    continue;
                }

                try
                {
                    record.MirrorUrl = await RetryAsync(
                        () => ResolveIntermediateLinkAsync(client, record.MirrorIntermediateUrl, cancellationToken),
                        log,
                        "resolve mirror link",
                        cancellationToken);
                    record.FailureReason = null;
                    log("[" + (i + 1) + "/" + records.Count + "] Resolved mirror URL for " + record.Name);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    record.FailureReason = ex.Message;
                    log("Mirror link resolution failed for " + record.Name + ": " + ex.Message);
                }

                if ((i + 1) % ProgressSaveInterval == 0)
                {
                    SaveJson(Path.Combine(runtimeDirectory, MirrorResolvedFile), allRecords);
                }
            }
        }
        finally
        {
            SaveJson(Path.Combine(runtimeDirectory, MirrorResolvedFile), allRecords);
        }
    }

    private async Task Step7DownloadMirrorArchivesAsync(string runtimeDirectory, string saveDirectory, bool failedOnly, Action<string> log, CancellationToken cancellationToken)
    {
        List<DownloadEntry> allRecords = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, MirrorResolvedFile)) ?? [];
        List<DownloadEntry> records = failedOnly
            ? MergeDownloadEntries(GetUnresolvedMirrorDownloadFailures(runtimeDirectory), allRecords)
            : allRecords;
        List<DownloadEntry> failures = failedOnly
            ? LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, MirrorFailuresFile)) ?? []
            : [];
        List<DownloadEntry> primaryFailures = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, PrimaryFailuresFile)) ?? [];

        log((failedOnly ? "Retrying " : "Processing ") + records.Count + " mirror download record(s).");

        try
        {
            for (int i = 0; i < records.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DownloadEntry record = records[i];
                if (string.IsNullOrWhiteSpace(record.MirrorUrl))
                {
                    record.FailureReason = "Mirror URL missing";
                    UpsertDownloadEntry(failures, record);
                    log("[" + (i + 1) + "/" + records.Count + "] No mirror URL: " + record.Name);
                    continue;
                }

                if (TryUseExistingDownloadedFile(record, saveDirectory, log, "mirror"))
                {
                    RemoveDownloadEntry(failures, record);
                    RemoveDownloadEntry(primaryFailures, record);
                    log("[" + (i + 1) + "/" + records.Count + "] Already downloaded mirror: " + Path.GetFileName(record.DownloadedFilePath));
                    continue;
                }

                try
                {
                    record.DownloadedFilePath = await RetryAsync(
                        () => DownloadResolvedUrlAsync(record.Name, record.MirrorUrl, saveDirectory, cancellationToken),
                        log,
                        "download mirror",
                        cancellationToken);
                    record.FailureReason = null;
                    RemoveDownloadEntry(failures, record);
                    RemoveDownloadEntry(primaryFailures, record);
                    log("[" + (i + 1) + "/" + records.Count + "] Downloaded mirror: " + Path.GetFileName(record.DownloadedFilePath));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    record.FailureReason = ex.Message;
                    UpsertDownloadEntry(failures, record);
                    log("Mirror download failed for " + record.Name + ": " + ex.Message);
                }

                if ((i + 1) % ProgressSaveInterval == 0)
                {
                    SaveJson(Path.Combine(runtimeDirectory, MirrorResolvedFile), allRecords);
                    SaveJson(Path.Combine(runtimeDirectory, MirrorFailuresFile), failures);
                    SaveJson(Path.Combine(runtimeDirectory, PrimaryFailuresFile), primaryFailures);
                }
            }
        }
        finally
        {
            SaveJson(Path.Combine(runtimeDirectory, MirrorResolvedFile), allRecords);
            SaveJson(Path.Combine(runtimeDirectory, MirrorFailuresFile), failures);
            SaveJson(Path.Combine(runtimeDirectory, PrimaryFailuresFile), primaryFailures);
        }

        log("Mirror failures kept for manual resolution: " + failures.Count);
    }

    private static void SavePermanentMirrorFailures(string runtimeDirectory, List<DownloadEntry> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        string path = Path.Combine(runtimeDirectory, MirrorFailuresFile);
        List<DownloadEntry> permanentFailures = LoadJson<List<DownloadEntry>>(path) ?? [];
        foreach (DownloadEntry failure in failures)
        {
            int existingIndex = permanentFailures.FindIndex(entry => IsSameDownloadEntry(entry, failure));
            if (existingIndex >= 0)
            {
                permanentFailures[existingIndex] = failure;
                continue;
            }

            permanentFailures.Add(failure);
        }

        SaveJson(path, permanentFailures);
    }

    private static string DescribeDownloadEntry(DownloadEntry record)
    {
        if (record == null)
        {
            return string.Empty;
        }

        string name = !string.IsNullOrWhiteSpace(record.Name) ? record.Name : record.ArticleUrl;
        string fileName = Path.GetFileName(record.DownloadedFilePath);
        return string.IsNullOrWhiteSpace(fileName) ? name : name + " (" + fileName + ")";
    }

    private static void RemoveDownloadEntriesFromFailureFile(
        string runtimeDirectory,
        string fileName,
        IReadOnlyList<DownloadEntry> recordsToRemove)
    {
        if (recordsToRemove == null || recordsToRemove.Count == 0)
        {
            return;
        }

        string path = Path.Combine(runtimeDirectory, fileName);
        List<DownloadEntry> failures = LoadJson<List<DownloadEntry>>(path) ?? [];
        int originalCount = failures.Count;
        foreach (DownloadEntry recordToRemove in recordsToRemove)
        {
            RemoveDownloadEntry(failures, recordToRemove);
        }

        if (failures.Count != originalCount)
        {
            SaveJson(path, failures);
        }
    }

    private ArticleMetadataResult TryCacheArticleMetadata(string gameName, string articleUrl, string html, Action<string> log)
    {
        string iconPath = string.Empty;
        List<string> defaultTags = new();
        try
        {
            string expectedInstallDirectory = Path.Combine(AppSettings.GamesDirectory, gameName);
            GameSourceMetadata sourceMetadata = iconPipelineService.ResolveSourceMetadataFromKimochiArticle(
                gameName,
                expectedInstallDirectory,
                articleUrl,
                html);
            defaultTags = sourceMetadata.DefaultTags ?? new List<string>();

            GameIconResolveResult iconResult = iconPipelineService.ResolveIconFromKimochiArticle(
                gameName,
                expectedInstallDirectory,
                articleUrl,
                html);
            if (!string.IsNullOrWhiteSpace(iconResult.IconPath))
            {
                iconPath = iconResult.IconPath;
            }
        }
        catch (Exception ex)
        {
            log("Article metadata fetch skipped for " + gameName + ": " + ex.Message);
        }

        return new ArticleMetadataResult(iconPath, defaultTags);
    }

    private static async Task<T> RetryAsync<T>(
        Func<Task<T>> action,
        Action<string> log,
        string operationName,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            try
            {
                return await action();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxRetryAttempts)
            {
                TimeSpan delay = TimeSpan.FromSeconds(attempt == 1 ? 1 : 3);
                log(operationName + " failed, retrying " + (attempt + 1) + "/" + MaxRetryAttempts + ": " + ex.Message);
                await Task.Delay(delay, cancellationToken);
            }
        }

        return await action();
    }

    private async Task<string> ResolveIntermediateLinkAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        Uri currentUri = new(url, UriKind.Absolute);
        string firstHtml = await GetStringSafeAsync(client, currentUri.AbsoluteUri, cancellationToken);
        string getLinkUrl = ExtractActionLink(firstHtml, currentUri, "Download File");
        if (string.IsNullOrWhiteSpace(getLinkUrl))
        {
            getLinkUrl = ExtractActionLink(firstHtml, currentUri, "Getlink");
        }

        if (string.IsNullOrWhiteSpace(getLinkUrl))
        {
            throw new InvalidOperationException("Download action was not found.");
        }

        string redirectedUrl = await ResolveDownloadFileRedirectAsync(getLinkUrl, cancellationToken);
        if (!string.IsNullOrWhiteSpace(redirectedUrl))
        {
            return redirectedUrl;
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

    private static async Task<string> ResolveDownloadFileRedirectAsync(string url, CancellationToken cancellationToken)
    {
        using HttpClient client = CreateHttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false
        });

        using HttpResponseMessage response = await client.GetAsync(url, cancellationToken);
        if (response.Headers.TryGetValues("x-download-url", out IEnumerable<string> downloadUrls))
        {
            string downloadUrl = downloadUrls.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(downloadUrl))
            {
                return WebUtility.HtmlDecode(downloadUrl);
            }
        }

        if (response.Headers.Location != null)
        {
            return new Uri(new Uri(url), response.Headers.Location).AbsoluteUri;
        }

        string html = await response.Content.ReadAsStringAsync(cancellationToken);
        Match match = Regex.Match(
            html,
            "https?://(?:drive\\.google\\.com|workupload\\.com|www\\.workupload\\.com|mediafire\\.com)[^\"'<>\\s]+",
            RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(match.Value) : null;
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
        await EnsureDownloadableResponseAsync(response, cancellationToken);
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
        Match downloadButton = Regex.Match(
            html,
            "<a[^>]*id=[\"']downloadButton[\"'][^>]*href=[\"'](?<href>https?://[^\"']+)[\"']",
            RegexOptions.IgnoreCase);
        if (downloadButton.Success)
        {
            string href = WebUtility.HtmlDecode(downloadButton.Groups["href"].Value);
            if (IsMediaFireDirectDownloadUrl(href))
            {
                return href;
            }
        }

        foreach (Match anchor in Regex.Matches(
            html,
            "<a[^>]*href=[\"'](?<href>https?://[^\"']+)[\"'][^>]*>(?<text>[\\s\\S]*?)</a>",
            RegexOptions.IgnoreCase))
        {
            string text = StripTags(anchor.Groups["text"].Value);
            string href = WebUtility.HtmlDecode(anchor.Groups["href"].Value);
            if (text.IndexOf("download", StringComparison.OrdinalIgnoreCase) >= 0 && IsMediaFireDirectDownloadUrl(href))
            {
                return href;
            }
        }

        throw new InvalidOperationException("MediaFire direct download link was not found.");
    }

    private static bool IsDownloadResponse(HttpResponseMessage response)
    {
        string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return true;
        }

        return !contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
            !contentType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task EnsureDownloadableResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (IsDownloadResponse(response))
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(BuildNonFileResponseMessage(response, body));
    }

    private static string BuildNonFileResponseMessage(HttpResponseMessage response, string body)
    {
        Uri responseUri = response.RequestMessage?.RequestUri;
        string host = responseUri?.Host ?? "download host";
        if (body?.IndexOf("quota exceeded", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Google Drive quota exceeded.";
        }

        if (body?.IndexOf("Are you a human", StringComparison.OrdinalIgnoreCase) >= 0 ||
            body?.IndexOf("Security Check", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return host + " returned a security check page instead of a file.";
        }

        return host + " returned a web page instead of a file.";
    }

    private static bool IsMediaFireDirectDownloadUrl(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out Uri uri))
        {
            return false;
        }

        string host = uri.Host.ToLowerInvariant();
        return host.Contains("mediafire.com") &&
            (host.StartsWith("download", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.IndexOf("/download", StringComparison.OrdinalIgnoreCase) >= 0);
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
        await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (FileStream destination = new(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        if (!IsUsableDownloadedFile(targetPath))
        {
            string reason = GetInvalidDownloadedFileReason(targetPath);
            TryDeleteFile(targetPath);
            throw new InvalidOperationException("Downloaded response was not a usable file: " + reason);
        }

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
            case "video/webm":
                return ".webm";
            case "video/quicktime":
                return ".mov";
            case "video/x-matroska":
                return ".mkv";
            case "video/x-msvideo":
                return ".avi";
            case "video/x-ms-wmv":
                return ".wmv";
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
                hrefMatch = Regex.Match(
                    articleHtml,
                    "<a[^>]*href=[\"'](?<href>https?://kimochi\\.info/(?!browse/|page/|search/|feedback/|faqs/|privacy-policy|terms-of-service|explore/|trending/|wp-)[^\"']+)[\"']",
                    RegexOptions.IgnoreCase);
            }

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
            if (!dateMatch.Success)
            {
                dateMatch = Regex.Match(
                    articleHtml,
                    "(?<text>(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\\s+\\d{1,2},\\s+\\d{4})",
                    RegexOptions.IgnoreCase);
            }

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
        if (!match.Success)
        {
            match = Regex.Match(
                html,
                "<h1\\b[^>]*>(?<text>[\\s\\S]*?)</h1>",
                RegexOptions.IgnoreCase);
        }

        if (!match.Success)
        {
            match = Regex.Match(
                html,
                "<meta[^>]*(?:property|name)=[\"']og:title[\"'][^>]*content=[\"'](?<text>[^\"']+)[\"'][^>]*>",
                RegexOptions.IgnoreCase);
        }

        if (!match.Success)
        {
            match = Regex.Match(
                html,
                "<title[^>]*>(?<text>[\\s\\S]*?)</title>",
                RegexOptions.IgnoreCase);
        }

        if (!match.Success)
        {
            return string.Empty;
        }

        string title = StripTags(match.Groups["text"].Value);
        const string suffix = " - Kimochi Gaming";
        if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            title = title[..^suffix.Length].Trim();
        }

        return title;
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
            return ExtractDownloadCardLink(html, pageUrl, hostName);
        }

        string href = WebUtility.HtmlDecode(match.Groups["href"].Value);
        return new Uri(new Uri(pageUrl), href).AbsoluteUri;
    }

    private static bool HasExtractedHostLinks(DownloadEntry record)
    {
        return record != null &&
            !string.IsNullOrWhiteSpace(record.Name) &&
            !string.Equals(record.Name, "unknown_title", StringComparison.OrdinalIgnoreCase) &&
            (!string.IsNullOrWhiteSpace(record.DriveIntermediateUrl) ||
                !string.IsNullOrWhiteSpace(record.MirrorIntermediateUrl));
    }

    private static bool HasCachedIcon(DownloadEntry record)
    {
        return record != null &&
            !string.IsNullOrWhiteSpace(record.IconPath) &&
            File.Exists(record.IconPath);
    }

    private static bool HasExtractedArticleMetadata(DownloadEntry record)
    {
        return HasExtractedTags(record) &&
            (!AppSettings.EnableRemoteIconFetch || HasCachedIcon(record));
    }

    private static bool HasExtractedTags(DownloadEntry record)
    {
        return record?.DefaultTags?.Count > 0;
    }

    private static string ExtractDownloadCardLink(string html, string pageUrl, string hostName)
    {
        Match downloadsMatch = Regex.Match(
            html,
            "<section\\b[^>]*id=[\"']downloads[\"'][^>]*>(?<html>[\\s\\S]*?)(?:</section>|<section\\b)",
            RegexOptions.IgnoreCase);
        string searchHtml = downloadsMatch.Success ? downloadsMatch.Groups["html"].Value : html;
        var candidates = new List<Tuple<string, string>>();

        foreach (Match linkMatch in Regex.Matches(
            searchHtml,
            "<a\\b[^>]*href=[\"'](?<href>[^\"']*/download/[^\"']+)[\"'][^>]*>(?<text>[\\s\\S]*?)</a>",
            RegexOptions.IgnoreCase))
        {
            string href = WebUtility.HtmlDecode(linkMatch.Groups["href"].Value);
            string text = StripTags(linkMatch.Groups["text"].Value);
            candidates.Add(Tuple.Create(href, text));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        Tuple<string, string> selected = null;
        if (hostName.Equals("Drive", StringComparison.OrdinalIgnoreCase))
        {
            selected = candidates.FirstOrDefault(candidate =>
                candidate.Item2.IndexOf("drive", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        else if (hostName.Equals("Mirror", StringComparison.OrdinalIgnoreCase))
        {
            selected = candidates.FirstOrDefault(candidate =>
                candidate.Item2.IndexOf("drive", StringComparison.OrdinalIgnoreCase) < 0);
        }

        selected ??= candidates.FirstOrDefault();
        return selected == null ? null : new Uri(new Uri(pageUrl), selected.Item1).AbsoluteUri;
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
        if (DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime absoluteDate))
        {
            return absoluteDate.Date;
        }

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

    private static string StripTags(string html)
    {
        string value = Regex.Replace(html ?? string.Empty, "<.*?>", string.Empty);
        return WebUtility.HtmlDecode(value).Trim();
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

    private sealed class ArticleMetadataResult
    {
        public ArticleMetadataResult(string iconPath, List<string> defaultTags)
        {
            IconPath = iconPath ?? string.Empty;
            DefaultTags = defaultTags ?? new List<string>();
        }

        public string IconPath { get; }

        public List<string> DefaultTags { get; }
    }
}
