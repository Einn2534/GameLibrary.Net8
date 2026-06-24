using Newtonsoft.Json;
using System.ComponentModel;

namespace GameLibrary.Net8;

public class GameInfo : INotifyPropertyChanged
{
    public const string GameEntryType = "Game";
    public const string VideoEntryType = "Video";

    private string name;
    private string icon;
    private string executable;
    private string installDirectory;
    private string entryType = GameEntryType;
    private string mediaPath;
    private string description;
    private string sourcePageUrl;
    private GameIconFetchDiagnostics remoteIconFetchDiagnostics = new();
    private List<string> tags = new();
    private List<string> defaultTags = new();
    private string editableTagsText = string.Empty;

    public string Name
    {
        get => name;
        set => SetField(ref name, value, nameof(Name));
    }

    public string Icon
    {
        get => icon;
        set => SetField(ref icon, value, nameof(Icon));
    }

    public string Executable
    {
        get => executable;
        set
        {
            if (SetField(ref executable, value, nameof(Executable)))
            {
                OnPropertyChanged(nameof(DisplayPath));
            }
        }
    }

    public string InstallDirectory
    {
        get => installDirectory;
        set
        {
            if (SetField(ref installDirectory, value, nameof(InstallDirectory)))
            {
                OnPropertyChanged(nameof(InstallDirectoryCreatedUtc));
                OnPropertyChanged(nameof(InstallDirectoryModifiedUtc));
                OnPropertyChanged(nameof(LibraryCardInfo));
            }
        }
    }

    public string EntryType
    {
        get => string.IsNullOrWhiteSpace(entryType) ? GameEntryType : entryType;
        set
        {
            string nextValue = string.IsNullOrWhiteSpace(value) ? GameEntryType : value;
            if (SetField(ref entryType, nextValue, nameof(EntryType)))
            {
                OnPropertyChanged(nameof(IsVideo));
                OnPropertyChanged(nameof(PrimaryActionLabel));
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(InstallDirectoryCreatedUtc));
                OnPropertyChanged(nameof(InstallDirectoryModifiedUtc));
                OnPropertyChanged(nameof(LibraryCardInfo));
            }
        }
    }

    public string MediaPath
    {
        get => mediaPath;
        set
        {
            if (SetField(ref mediaPath, value, nameof(MediaPath)))
            {
                OnPropertyChanged(nameof(DisplayPath));
                OnPropertyChanged(nameof(InstallDirectoryCreatedUtc));
                OnPropertyChanged(nameof(InstallDirectoryModifiedUtc));
                OnPropertyChanged(nameof(LibraryCardInfo));
            }
        }
    }

    public string Description
    {
        get => description;
        set => SetField(ref description, value, nameof(Description));
    }

    public string SourcePageUrl
    {
        get => sourcePageUrl;
        set => SetField(ref sourcePageUrl, value, nameof(SourcePageUrl));
    }

    [JsonIgnore]
    public bool IsVideo => string.Equals(EntryType, VideoEntryType, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string PrimaryActionLabel => UiText.Get(IsVideo ? "Button.Play" : "Button.Launch");

    [JsonIgnore]
    public string DisplayPath => IsVideo ? MediaPath : Executable;

    [JsonIgnore]
    public DateTime InstallDirectoryCreatedUtc => IsVideo
        ? GetFileTimeUtc(MediaPath, File.GetCreationTimeUtc)
        : GetDirectoryTimeUtc(InstallDirectory, Directory.GetCreationTimeUtc);

    [JsonIgnore]
    public DateTime InstallDirectoryModifiedUtc => IsVideo
        ? GetFileTimeUtc(MediaPath, File.GetLastWriteTimeUtc)
        : GetDirectoryTimeUtc(InstallDirectory, Directory.GetLastWriteTimeUtc);

    [JsonIgnore]
    public string LibraryCardInfo => "更新 " + FormatLibraryDate(InstallDirectoryModifiedUtc) +
        " / 追加 " + FormatLibraryDate(InstallDirectoryCreatedUtc);

    [JsonIgnore]
    public GameIconFetchDiagnostics RemoteIconFetchDiagnostics
    {
        get => remoteIconFetchDiagnostics;
        set
        {
            remoteIconFetchDiagnostics = value ?? new GameIconFetchDiagnostics();
            OnPropertyChanged(nameof(RemoteIconFetchDiagnostics));
            OnPropertyChanged(nameof(HasRemoteIconFetchFailure));
        }
    }

    [JsonIgnore]
    public bool HasRemoteIconFetchFailure => RemoteIconFetchDiagnostics?.HasFailure == true;

    public List<string> DefaultTags
    {
        get => defaultTags;
        set
        {
            defaultTags = value ?? new List<string>();
            OnPropertyChanged(nameof(DefaultTags));
        }
    }

    public List<string> Tags
    {
        get => tags;
        set
        {
            tags = value ?? new List<string>();
            OnPropertyChanged(nameof(Tags));
        }
    }

    [JsonIgnore]
    public string EditableTagsText
    {
        get => editableTagsText;
        set => SetField(ref editableTagsText, value ?? string.Empty, nameof(EditableTagsText));
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private bool SetField(ref string field, string value, string propertyName)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatLibraryDate(DateTime value)
    {
        if (value <= DateTime.MinValue)
        {
            return "-";
        }

        DateTime displayValue = value.Kind == DateTimeKind.Utc
            ? value.ToLocalTime()
            : value;
        return displayValue.ToString("yyyy/MM/dd");
    }

    private static DateTime GetDirectoryTimeUtc(string directoryPath, Func<string, DateTime> getTimeUtc)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return DateTime.MinValue;
        }

        try
        {
            return getTimeUtc(directoryPath);
        }
        catch (Exception ex) when (
            ex is IOException ||
            ex is UnauthorizedAccessException ||
            ex is ArgumentException ||
            ex is NotSupportedException)
        {
            return DateTime.MinValue;
        }
    }

    private static DateTime GetFileTimeUtc(string filePath, Func<string, DateTime> getTimeUtc)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return DateTime.MinValue;
        }

        try
        {
            return getTimeUtc(filePath);
        }
        catch (Exception ex) when (
            ex is IOException ||
            ex is UnauthorizedAccessException ||
            ex is ArgumentException ||
            ex is NotSupportedException)
        {
            return DateTime.MinValue;
        }
    }
}
