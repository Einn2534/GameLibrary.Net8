using System.ComponentModel;
using static GameLibrary.Net8.FileExtensionMatcher;

namespace GameLibrary.Net8;

public class StoredArchiveItem : INotifyPropertyChanged
{
    private string status;

    public StoredArchiveItem(string fullPath)
    {
        FullPath = fullPath ?? throw new ArgumentNullException(nameof(fullPath));
        FileName = Path.GetFileName(fullPath);
        IsMedia = IsConfiguredExtension(fullPath, AppSettings.MediaExtensions);
        IsArchive = IsConfiguredExtension(fullPath, AppSettings.ArchiveExtensions);
        CanRestore = IsArchive || IsMedia;
        KindText = UiText.Get(IsMedia ? "DownloadItem.Kind.Video" : "DownloadItem.Kind.Archive");

        var info = new FileInfo(fullPath);
        SizeText = info.Exists ? StorageMaintenanceService.FormatSize(info.Length) : "-";
        ModifiedAt = info.Exists ? info.LastWriteTime.ToString("yyyy-MM-dd HH:mm") : "-";
        status = UiText.Get("Status.Pending");
    }

    public string FileName { get; }

    public string FullPath { get; }

    public bool IsArchive { get; }

    public bool IsMedia { get; }

    public bool CanRestore { get; }

    public string KindText { get; }

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

}
