using Newtonsoft.Json;
using System.ComponentModel;

namespace GameLibrary.Net8;

public class GameInfo : INotifyPropertyChanged
{
    private string name;
    private string icon;
    private string executable;
    private string installDirectory;
    private string description;
    private string sourcePageUrl;
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
        set => SetField(ref executable, value, nameof(Executable));
    }

    public string InstallDirectory
    {
        get => installDirectory;
        set => SetField(ref installDirectory, value, nameof(InstallDirectory));
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

    private void SetField(ref string field, string value, string propertyName)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
