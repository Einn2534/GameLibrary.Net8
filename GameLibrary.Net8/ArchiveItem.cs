using System.ComponentModel;

namespace GameLibrary.Net8;

public class ArchiveItem : INotifyPropertyChanged
{
    private string status;

    public ArchiveItem(string fullPath)
    {
        FullPath = fullPath ?? throw new ArgumentNullException(nameof(fullPath));
        FileName = Path.GetFileName(fullPath);

        var info = new FileInfo(fullPath);
        SizeText = info.Exists ? FormatSize(info.Length) : "-";
        ModifiedAt = info.Exists ? info.LastWriteTime.ToString("yyyy-MM-dd HH:mm") : "-";
        status = UiText.Get("Status.Pending");
    }

    public string FileName { get; }

    public string FullPath { get; }

    public string SizeText { get; }

    public string ModifiedAt { get; }

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

    private static string FormatSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int index = 0;

        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return value.ToString(index == 0 ? "0" : "0.0") + " " + suffixes[index];
    }
}
