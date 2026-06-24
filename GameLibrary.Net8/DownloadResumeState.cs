namespace GameLibrary.Net8;

public class DownloadResumeState
{
    public static DownloadResumeState None { get; } = new()
    {
        CanResume = false,
        StartStepIndex = 0,
        Summary = UiText.Get("Downloader.ResumeUnavailable")
    };

    public bool CanResume { get; set; }

    public int StartStepIndex { get; set; }

    public DateTime? DateFrom { get; set; }

    public DateTime? DateTo { get; set; }

    public DateTime? LastUpdatedAt { get; set; }

    public string Summary { get; set; }
}
