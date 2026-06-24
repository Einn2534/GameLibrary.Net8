using static GameLibrary.Net8.DownloadEntryOperations;
using static GameLibrary.Net8.DownloadFileInspector;
using static GameLibrary.Net8.DownloadPipelineArtifacts;

namespace GameLibrary.Net8;

internal static class DownloadPipelineRecordSets
{
    public static List<DownloadEntry> GetUnresolvedHostLinkFailures(string runtimeDirectory)
    {
        List<DownloadEntry> records = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, DownloadRecordsFile)) ?? [];
        List<DownloadEntry> failures = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, HostLinkFailuresFile)) ?? [];
        return failures
            .Where(record => record != null &&
                !records.Any(savedRecord => IsSameDownloadEntry(savedRecord, record)))
            .ToList();
    }

    public static List<DownloadEntry> GetUnresolvedPrimaryLinkRecords(string runtimeDirectory)
    {
        List<DownloadEntry> sourceRecords = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, DownloadRecordsFile)) ?? [];
        List<DownloadEntry> resolvedRecords = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, PrimaryResolvedFile)) ?? [];
        return MergeDownloadEntries(sourceRecords, resolvedRecords)
            .Where(IsUnresolvedPrimaryLinkRecord)
            .ToList();
    }

    public static List<DownloadEntry> GetUnresolvedPrimaryDownloadFailures(string runtimeDirectory)
    {
        List<DownloadEntry> failures = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, PrimaryFailuresFile)) ?? [];
        List<DownloadEntry> mirrorResolved = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, MirrorResolvedFile)) ?? [];
        return failures
            .Where(record => record != null &&
                !IsDownloaded(record) &&
                !mirrorResolved.Any(savedRecord => IsSameDownloadEntry(savedRecord, record) && IsDownloaded(savedRecord)))
            .ToList();
    }

    public static List<DownloadEntry> GetMirrorFallbackCandidates(string runtimeDirectory)
    {
        return GetUnresolvedPrimaryDownloadFailures(runtimeDirectory)
            .Where(record => !string.IsNullOrWhiteSpace(record.MirrorIntermediateUrl))
            .ToList();
    }

    public static List<DownloadEntry> GetUnresolvedMirrorLinkRecords(string runtimeDirectory)
    {
        List<DownloadEntry> sourceRecords = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, MirrorCandidatesFile)) ?? [];
        List<DownloadEntry> resolvedRecords = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, MirrorResolvedFile)) ?? [];
        return MergeDownloadEntries(sourceRecords, resolvedRecords)
            .Where(IsUnresolvedMirrorLinkRecord)
            .ToList();
    }

    public static List<DownloadEntry> GetUnresolvedMirrorDownloadFailures(string runtimeDirectory)
    {
        List<DownloadEntry> failures = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, MirrorFailuresFile)) ?? [];
        return failures
            .Where(record => record != null && !IsDownloaded(record))
            .ToList();
    }

    public static bool IsUnresolvedPrimaryLinkRecord(DownloadEntry record)
    {
        return record != null &&
            !IsDownloaded(record) &&
            !string.IsNullOrWhiteSpace(record.DriveIntermediateUrl) &&
            string.IsNullOrWhiteSpace(record.PrimaryUrl);
    }

    public static bool IsUnresolvedMirrorLinkRecord(DownloadEntry record)
    {
        return record != null &&
            !IsDownloaded(record) &&
            !string.IsNullOrWhiteSpace(record.MirrorIntermediateUrl) &&
            string.IsNullOrWhiteSpace(record.MirrorUrl);
    }

    public static bool IsDownloaded(DownloadEntry record)
    {
        return record != null &&
            !string.IsNullOrWhiteSpace(record.DownloadedFilePath) &&
            IsUsableDownloadedFile(record.DownloadedFilePath);
    }

    public static List<DownloadEntry> GetPreviouslyDownloadedRecords(string runtimeDirectory, string saveDirectory)
    {
        var results = new List<DownloadEntry>();
        foreach (string fileName in new[] { DownloadRecordsFile, PrimaryResolvedFile, MirrorResolvedFile })
        {
            List<DownloadEntry> records = LoadJson<List<DownloadEntry>>(Path.Combine(runtimeDirectory, fileName)) ?? [];
            foreach (DownloadEntry record in records.Where(record => record != null))
            {
                DownloadEntry candidate = CloneDownloadEntry(record);
                if (TryUseExistingDownloadedFile(candidate, saveDirectory, _ => { }, "downloaded"))
                {
                    UpsertDownloadEntry(results, candidate);
                }
            }
        }

        return results;
    }

    public static bool TryMarkPreviouslyDownloaded(
        DownloadEntry record,
        List<DownloadEntry> previouslyDownloadedRecords,
        string saveDirectory,
        Action<string> log,
        out DownloadEntry previouslyDownloadedRecord)
    {
        previouslyDownloadedRecord = null;
        if (record == null)
        {
            return false;
        }

        if (TryFindPreviouslyDownloadedRecord(previouslyDownloadedRecords, record, out previouslyDownloadedRecord))
        {
            return true;
        }

        DownloadEntry candidate = CloneDownloadEntry(record);
        if (!TryUseExistingDownloadedFile(candidate, saveDirectory, log, "downloaded"))
        {
            return false;
        }

        UpsertDownloadEntry(previouslyDownloadedRecords, candidate);
        previouslyDownloadedRecord = candidate;
        return true;
    }

    private static bool TryFindPreviouslyDownloadedRecord(
        IEnumerable<DownloadEntry> previouslyDownloadedRecords,
        DownloadEntry record,
        out DownloadEntry previouslyDownloadedRecord)
    {
        previouslyDownloadedRecord = previouslyDownloadedRecords.FirstOrDefault(candidate => IsSameDownloadedGame(candidate, record));
        return previouslyDownloadedRecord != null;
    }

    private static bool IsSameDownloadedGame(DownloadEntry left, DownloadEntry right)
    {
        if (IsSameDownloadEntry(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(left.Name) &&
            !string.IsNullOrWhiteSpace(right.Name) &&
            string.Equals(SanitizeFileName(left.Name), SanitizeFileName(right.Name), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string leftProductCode = ExtractProductCode(left.Name);
        string rightProductCode = ExtractProductCode(right.Name);
        return !string.IsNullOrWhiteSpace(leftProductCode) &&
            string.Equals(leftProductCode, rightProductCode, StringComparison.OrdinalIgnoreCase);
    }
}
