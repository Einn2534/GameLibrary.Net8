using static GameLibrary.Net8.DownloadEntryOperations;
using static GameLibrary.Net8.DownloadFileInspector;
using static GameLibrary.Net8.DownloadPipelineArtifacts;

namespace GameLibrary.Net8;

public class DownloadResumeService
{
    public DownloadResumeState Detect(string runtimeDirectory, string saveDirectory)
    {
        if (string.IsNullOrWhiteSpace(runtimeDirectory) || !Directory.Exists(runtimeDirectory))
        {
            return DownloadResumeState.None;
        }

        DownloadRunState runState = LoadJson<DownloadRunState>(Path.Combine(runtimeDirectory, RunStateFile));
        int startStepIndex = InferStartStepIndex(runtimeDirectory, saveDirectory);
        if (startStepIndex < 0)
        {
            return DownloadResumeState.None;
        }

        string stepLabel = DownloadPipelineService.StepLabels[Math.Clamp(startStepIndex, 0, DownloadPipelineService.StepLabels.Count - 1)];
        DateTime? dateTo = runState?.DateTo?.Date;
        DateTime? lastUpdatedAt = GetLastUpdatedAt(runtimeDirectory, runState);

        return new DownloadResumeState
        {
            CanResume = true,
            StartStepIndex = startStepIndex,
            DateFrom = runState?.DateFrom?.Date,
            DateTo = dateTo,
            LastUpdatedAt = lastUpdatedAt,
            Summary = BuildSummary(startStepIndex, stepLabel, runState, lastUpdatedAt)
        };
    }

    private static int InferStartStepIndex(string runtimeDirectory, string saveDirectory)
    {
        List<string> articleUrls = LoadJson<List<string>>(Path.Combine(runtimeDirectory, ArticleUrlsFile)) ?? [];
        if (articleUrls.Count == 0)
        {
            return -1;
        }

        List<DownloadEntry> downloadRecords = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, DownloadRecordsFile)) ?? [];
        if (downloadRecords.Count == 0 || HasMissingArticleRecords(articleUrls, downloadRecords))
        {
            return 2;
        }

        List<DownloadEntry> primaryResolved = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, PrimaryResolvedFile)) ?? [];
        if (primaryResolved.Count == 0 || HasPendingPrimaryResolution(primaryResolved))
        {
            return 3;
        }

        if (HasPendingPrimaryDownloads(primaryResolved, saveDirectory))
        {
            return 4;
        }

        List<DownloadEntry> primaryFailures = GetUnresolvedPrimaryDownloadFailures(runtimeDirectory);
        if (primaryFailures.Count == 0)
        {
            return -1;
        }

        List<DownloadEntry> mirrorCandidates = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, MirrorCandidatesFile)) ?? [];
        if (mirrorCandidates.Count == 0 && primaryFailures.Any(record => !string.IsNullOrWhiteSpace(record.MirrorIntermediateUrl)))
        {
            return 5;
        }

        if (mirrorCandidates.Count == 0)
        {
            return -1;
        }

        List<DownloadEntry> mirrorResolved = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, MirrorResolvedFile)) ?? [];
        if (mirrorResolved.Count == 0 || HasPendingMirrorResolution(mirrorResolved))
        {
            return 6;
        }

        if (HasPendingMirrorDownloads(mirrorResolved, saveDirectory))
        {
            return 7;
        }

        List<DownloadEntry> mirrorFailures = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, MirrorFailuresFile)) ?? [];
        return mirrorFailures.Count > 0 ? 7 : -1;
    }

    private static bool HasMissingArticleRecords(IReadOnlyList<string> articleUrls, IReadOnlyList<DownloadEntry> records)
    {
        return articleUrls.Any(url => !records.Any(record => IsSameDownloadEntry(record, new DownloadEntry { ArticleUrl = url })));
    }

    private static bool HasPendingPrimaryResolution(IEnumerable<DownloadEntry> records)
    {
        return records.Any(record =>
            !string.IsNullOrWhiteSpace(record?.DriveIntermediateUrl) &&
            string.IsNullOrWhiteSpace(record.PrimaryUrl) &&
            string.IsNullOrWhiteSpace(record.FailureReason));
    }

    private static bool HasPendingPrimaryDownloads(IEnumerable<DownloadEntry> records, string saveDirectory)
    {
        return records.Any(record =>
            !string.IsNullOrWhiteSpace(record?.PrimaryUrl) &&
            string.IsNullOrWhiteSpace(record.FailureReason) &&
            !DownloadedFileExists(record.DownloadedFilePath, saveDirectory));
    }

    private static List<DownloadEntry> GetUnresolvedPrimaryDownloadFailures(string runtimeDirectory)
    {
        List<DownloadEntry> failures = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, PrimaryFailuresFile)) ?? [];
        List<DownloadEntry> mirrorResolved = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, MirrorResolvedFile)) ?? [];
        return failures
            .Where(record => record != null &&
                !DownloadedFileExists(record.DownloadedFilePath, null) &&
                !mirrorResolved.Any(savedRecord =>
                    IsSameDownloadEntry(savedRecord, record) &&
                    DownloadedFileExists(savedRecord.DownloadedFilePath, null)))
            .ToList();
    }

    private static bool HasPendingMirrorResolution(IEnumerable<DownloadEntry> records)
    {
        return records.Any(record =>
            !string.IsNullOrWhiteSpace(record?.MirrorIntermediateUrl) &&
            string.IsNullOrWhiteSpace(record.MirrorUrl) &&
            string.IsNullOrWhiteSpace(record.FailureReason));
    }

    private static bool HasPendingMirrorDownloads(IEnumerable<DownloadEntry> records, string saveDirectory)
    {
        return records.Any(record =>
            !string.IsNullOrWhiteSpace(record?.MirrorUrl) &&
            !DownloadedFileExists(record.DownloadedFilePath, saveDirectory));
    }

    private static bool DownloadedFileExists(string downloadedFilePath, string saveDirectory)
    {
        if (!string.IsNullOrWhiteSpace(downloadedFilePath) && IsUsableDownloadedFile(downloadedFilePath))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(downloadedFilePath))
        {
            return false;
        }

        string fileName = Path.GetFileName(downloadedFilePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return GetDownloadSearchDirectories(saveDirectory, Path.GetExtension(fileName))
            .Any(directory => IsUsableDownloadedFile(Path.Combine(directory, fileName)));
    }

    private static DateTime? GetLastUpdatedAt(string runtimeDirectory, DownloadRunState runState)
    {
        if (runState?.UpdatedAt != default)
        {
            return runState.UpdatedAt;
        }

        return Directory
            .EnumerateFiles(runtimeDirectory, "*.json")
            .Select(File.GetLastWriteTime)
            .DefaultIfEmpty()
            .Max();
    }

    private static string BuildSummary(int startStepIndex, string stepLabel, DownloadRunState runState, DateTime? lastUpdatedAt)
    {
        string range = runState?.DateFrom.HasValue == true || runState?.DateTo.HasValue == true
            ? FormatDateRange(runState.DateFrom, runState.DateTo)
            : UiText.Get("Downloader.ResumeDateRangeUnknown");

        string updated = lastUpdatedAt.HasValue && lastUpdatedAt.Value != default
            ? lastUpdatedAt.Value.ToString("yyyy-MM-dd HH:mm")
            : UiText.Get("Downloader.ResumeUpdatedUnknown");

        return UiText.Format("Downloader.ResumeSummary", startStepIndex, stepLabel, range, updated);
    }

    private static string FormatDateRange(DateTime? dateFrom, DateTime? dateTo)
    {
        string from = dateFrom.HasValue
            ? dateFrom.Value.ToString("yyyy-MM-dd")
            : UiText.Get("Downloader.ResumeOpenStart");
        string to = dateTo.HasValue
            ? dateTo.Value.ToString("yyyy-MM-dd")
            : UiText.Get("Downloader.ResumeOpenEnd");

        return from + " -> " + to;
    }

}
