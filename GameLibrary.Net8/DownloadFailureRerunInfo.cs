namespace GameLibrary.Net8;

public class DownloadFailureRerunInfo
{
    public DownloadFailureRerunInfo(int stepIndex, string failureSetName, int unresolvedCount)
    {
        StepIndex = stepIndex;
        FailureSetName = failureSetName ?? string.Empty;
        UnresolvedCount = unresolvedCount;
    }

    public int StepIndex { get; }

    public string FailureSetName { get; }

    public int UnresolvedCount { get; }
}
