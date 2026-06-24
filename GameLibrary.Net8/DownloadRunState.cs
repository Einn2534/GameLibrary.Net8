namespace GameLibrary.Net8;

public class DownloadRunState
{
    public DateTime? DateFrom { get; set; }

    public DateTime? DateTo { get; set; }

    public int RequestedStartStepIndex { get; set; }

    public bool FailedOnly { get; set; }

    public int LastStartedStepIndex { get; set; } = -1;

    public int LastCompletedStepIndex { get; set; } = -1;

    public DateTime StartedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
