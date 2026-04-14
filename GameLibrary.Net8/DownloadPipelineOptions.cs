namespace GameLibrary.Net8;

public class DownloadPipelineOptions
{
    public string RuntimeDirectory { get; set; }

    public string SaveDirectory { get; set; }

    public DateTime? DateFrom { get; set; }

    public DateTime? DateTo { get; set; }

    public int StartStepIndex { get; set; }
}
