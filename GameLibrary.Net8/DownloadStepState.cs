using System.ComponentModel;

namespace GameLibrary.Net8;

public class DownloadStepState : INotifyPropertyChanged
{
    private string status;

    public DownloadStepState(int index, string label)
    {
        Index = index;
        Label = label ?? throw new ArgumentNullException(nameof(label));
        status = UiText.Get("Status.Pending");
    }

    public int Index { get; }

    public string Label { get; }

    public string DisplayLabel => UiText.Format("Downloader.StepDisplay", Index, Label);

    public string Status
    {
        get => status;
        set
        {
            if (string.Equals(status, value, StringComparison.Ordinal))
            {
                return;
            }

            status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
}
