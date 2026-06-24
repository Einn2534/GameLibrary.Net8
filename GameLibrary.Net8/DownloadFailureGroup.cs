using System.Collections.ObjectModel;

namespace GameLibrary.Net8;

public class DownloadFailureGroup
{
    public DownloadFailureGroup(string title, string fileName, int rerunStepIndex, IEnumerable<DownloadFailureItem> entries)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        RerunStepIndex = rerunStepIndex;
        Entries = new ObservableCollection<DownloadFailureItem>(entries ?? []);
    }

    public string Title { get; }

    public string FileName { get; }

    public int RerunStepIndex { get; }

    public ObservableCollection<DownloadFailureItem> Entries { get; }

    public int Count => Entries.Count;

    public string HeaderText => Title + ": " + Count;

    public string FileText => FileName;
}
