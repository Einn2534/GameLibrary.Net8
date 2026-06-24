namespace GameLibrary.Net8;

internal static class DownloadEntryOperations
{
    public static bool IsSameDownloadEntry(DownloadEntry left, DownloadEntry right)
    {
        if (left == null || right == null)
        {
            return false;
        }

        string leftKey = !string.IsNullOrWhiteSpace(left.ArticleUrl) ? left.ArticleUrl : left.Name;
        string rightKey = !string.IsNullOrWhiteSpace(right.ArticleUrl) ? right.ArticleUrl : right.Name;
        if (string.IsNullOrWhiteSpace(leftKey) || string.IsNullOrWhiteSpace(rightKey))
        {
            return false;
        }

        return string.Equals(leftKey, rightKey, StringComparison.OrdinalIgnoreCase);
    }

    public static List<DownloadEntry> MergeDownloadEntries(
        IEnumerable<DownloadEntry> sourceRecords,
        IEnumerable<DownloadEntry> savedRecords)
    {
        var results = new List<DownloadEntry>();
        List<DownloadEntry> saved = (savedRecords ?? Enumerable.Empty<DownloadEntry>())
            .Where(record => record != null)
            .ToList();

        foreach (DownloadEntry sourceRecord in (sourceRecords ?? Enumerable.Empty<DownloadEntry>()).Where(record => record != null))
        {
            DownloadEntry savedRecord = saved.FirstOrDefault(record => IsSameDownloadEntry(record, sourceRecord));
            results.Add(MergeDownloadEntry(sourceRecord, savedRecord));
        }

        return results;
    }

    public static DownloadEntry MergeDownloadEntry(DownloadEntry sourceRecord, DownloadEntry savedRecord)
    {
        if (savedRecord == null)
        {
            return sourceRecord;
        }

        return new DownloadEntry
        {
            Name = !string.IsNullOrWhiteSpace(sourceRecord.Name) &&
                !string.Equals(sourceRecord.Name, "unknown_title", StringComparison.OrdinalIgnoreCase)
                    ? sourceRecord.Name
                    : savedRecord.Name,
            ArticleUrl = !string.IsNullOrWhiteSpace(sourceRecord.ArticleUrl)
                ? sourceRecord.ArticleUrl
                : savedRecord.ArticleUrl,
            DriveIntermediateUrl = !string.IsNullOrWhiteSpace(sourceRecord.DriveIntermediateUrl)
                ? sourceRecord.DriveIntermediateUrl
                : savedRecord.DriveIntermediateUrl,
            MirrorIntermediateUrl = !string.IsNullOrWhiteSpace(sourceRecord.MirrorIntermediateUrl)
                ? sourceRecord.MirrorIntermediateUrl
                : savedRecord.MirrorIntermediateUrl,
            PrimaryUrl = savedRecord.PrimaryUrl,
            MirrorUrl = savedRecord.MirrorUrl,
            IconPath = !string.IsNullOrWhiteSpace(sourceRecord.IconPath)
                ? sourceRecord.IconPath
                : savedRecord.IconPath,
            DefaultTags = sourceRecord.DefaultTags?.Count > 0
                ? sourceRecord.DefaultTags
                : savedRecord.DefaultTags,
            DownloadedFilePath = savedRecord.DownloadedFilePath,
            FailureReason = savedRecord.FailureReason
        };
    }

    public static DownloadEntry CloneDownloadEntry(DownloadEntry record)
    {
        if (record == null)
        {
            return null;
        }

        return new DownloadEntry
        {
            Name = record.Name,
            ArticleUrl = record.ArticleUrl,
            DriveIntermediateUrl = record.DriveIntermediateUrl,
            MirrorIntermediateUrl = record.MirrorIntermediateUrl,
            PrimaryUrl = record.PrimaryUrl,
            MirrorUrl = record.MirrorUrl,
            IconPath = record.IconPath,
            DefaultTags = record.DefaultTags?.ToList() ?? new List<string>(),
            DownloadedFilePath = record.DownloadedFilePath,
            FailureReason = record.FailureReason
        };
    }

    public static void UpsertDownloadEntry(List<DownloadEntry> records, DownloadEntry nextRecord)
    {
        int existingIndex = records.FindIndex(record => IsSameDownloadEntry(record, nextRecord));
        if (existingIndex >= 0)
        {
            records[existingIndex] = nextRecord;
            return;
        }

        records.Add(nextRecord);
    }

    public static void RemoveDownloadEntry(List<DownloadEntry> records, DownloadEntry recordToRemove)
    {
        int existingIndex = records.FindIndex(record => IsSameDownloadEntry(record, recordToRemove));
        if (existingIndex >= 0)
        {
            records.RemoveAt(existingIndex);
        }
    }
}
