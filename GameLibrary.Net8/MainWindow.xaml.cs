using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace GameLibrary.Net8;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly GameTagStoreService tagStoreService;
    private readonly GameCatalogService catalogService;
    private readonly GameCatalogService remoteIconCatalogService;
    private readonly GameFilterState filterState = new();
    private readonly ArchiveImportService archiveImportService;
    private readonly DownloadPipelineService downloadPipelineService;
    private readonly ICollectionView gamesView;
    private CancellationTokenSource downloaderCancellation;
    private string statusMessage;
    private string downloadDateFromText;
    private string downloadDateToText;
    private string selectedDownloaderStartStep;
    private string downloaderLog;
    private bool isDownloaderRunning;
    private bool isRefreshingIcons;
    private bool refreshIconsAgain;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        tagStoreService = new GameTagStoreService(AppSettings.TagStorePath);
        catalogService = new GameCatalogService(
            AppSettings.GamesDirectory,
            AppSettings.GamesJsonPath,
            new GamesJsonGenerator(
                AppSettings.DefaultGameDescription,
                new GameIconPipelineService(
                    AppSettings.IconCacheDirectory,
                    enableRemoteFetch: false,
                    remoteFetchLimitPerRun: 0)),
            tagStoreService);

        remoteIconCatalogService = new GameCatalogService(
            AppSettings.GamesDirectory,
            AppSettings.GamesJsonPath,
            new GamesJsonGenerator(
                AppSettings.DefaultGameDescription,
                new GameIconPipelineService(
                    AppSettings.IconCacheDirectory,
                    AppSettings.EnableRemoteIconFetch,
                    AppSettings.RemoteIconFetchLimitPerRun)),
            tagStoreService);

        archiveImportService = new ArchiveImportService(
            AppSettings.SevenZipPath,
            AppSettings.ArchivePasswords,
            AppSettings.ArchiveExtensions);

        downloadPipelineService = new DownloadPipelineService();
        gamesView = CollectionViewSource.GetDefaultView(Games);
        gamesView.Filter = GameFilter;

        InitializeDownloaderState();
        LoadGames();
        LoadArchives();
        StatusMessage = UiText.Get("Status.Ready");
    }

    public ObservableCollection<GameInfo> Games { get; } = new();

    public ObservableCollection<string> AvailableTags { get; } = new();

    public ObservableCollection<ArchiveItem> Archives { get; } = new();

    public ObservableCollection<DownloadStepState> DownloadSteps { get; } = new();

    public ObservableCollection<string> DownloaderStartSteps { get; } = new();

    public string SearchText
    {
        get => filterState.SearchText;
        set
        {
            string nextValue = value ?? string.Empty;
            if (string.Equals(filterState.SearchText, nextValue, StringComparison.Ordinal))
            {
                return;
            }

            filterState.SearchText = nextValue;
            OnPropertyChanged(nameof(SearchText));
            RefreshGamesView();
        }
    }

    public string SelectedTag
    {
        get => filterState.SelectedTag;
        set
        {
            string nextValue = string.IsNullOrWhiteSpace(value) ? GameFilterState.AllTagsLabel : value;
            if (string.Equals(filterState.SelectedTag, nextValue, StringComparison.Ordinal))
            {
                return;
            }

            filterState.SelectedTag = nextValue;
            OnPropertyChanged(nameof(SelectedTag));
            RefreshGamesView();
        }
    }

    public string RuntimeDirectoryPath => AppSettings.RuntimeDirectory;

    public string GamesDirectoryPath => AppSettings.GamesDirectory;

    public string ArchiveInboxDirectory => AppSettings.ArchiveInboxDirectory;

    public string SevenZipPath => AppSettings.SevenZipPath;

    public string DownloadDateFromText
    {
        get => downloadDateFromText;
        set
        {
            if (string.Equals(downloadDateFromText, value, StringComparison.Ordinal))
            {
                return;
            }

            downloadDateFromText = value;
            OnPropertyChanged(nameof(DownloadDateFromText));
        }
    }

    public string DownloadDateToText
    {
        get => downloadDateToText;
        set
        {
            if (string.Equals(downloadDateToText, value, StringComparison.Ordinal))
            {
                return;
            }

            downloadDateToText = value;
            OnPropertyChanged(nameof(DownloadDateToText));
        }
    }

    public string SelectedDownloaderStartStep
    {
        get => selectedDownloaderStartStep;
        set
        {
            if (string.Equals(selectedDownloaderStartStep, value, StringComparison.Ordinal))
            {
                return;
            }

            selectedDownloaderStartStep = value;
            OnPropertyChanged(nameof(SelectedDownloaderStartStep));
        }
    }

    public string DownloaderLog
    {
        get => downloaderLog;
        set
        {
            if (string.Equals(downloaderLog, value, StringComparison.Ordinal))
            {
                return;
            }

            downloaderLog = value;
            OnPropertyChanged(nameof(DownloaderLog));
        }
    }

    public bool CanStartDownloader => !isDownloaderRunning;

    public bool CanStopDownloader => isDownloaderRunning;

    public string StatusMessage
    {
        get => statusMessage;
        set
        {
            if (string.Equals(statusMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            statusMessage = value;
            OnPropertyChanged(nameof(StatusMessage));
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void InitializeDownloaderState()
    {
        DownloadSteps.Clear();
        DownloaderStartSteps.Clear();

        for (int i = 0; i < DownloadPipelineService.StepLabels.Count; i++)
        {
            string label = DownloadPipelineService.StepLabels[i];
            DownloadSteps.Add(new DownloadStepState(i, label));
            DownloaderStartSteps.Add(UiText.Format("Downloader.StepDisplay", i, label));
        }

        SelectedDownloaderStartStep = DownloaderStartSteps.FirstOrDefault();
        DownloadDateFromText = DateTime.Today.AddDays(-7).ToString("yyyy-MM-dd");
        DownloadDateToText = DateTime.Today.ToString("yyyy-MM-dd");
        DownloaderLog = string.Empty;
        SetDownloaderRunning(false);
    }

    private void LoadGames()
    {
        try
        {
            Games.Clear();
            foreach (GameInfo game in catalogService.LoadGames())
            {
                Games.Add(game);
            }

            UpdateAvailableTags();
            RefreshGamesView();
            StatusMessage = tagStoreService.LastStatus.HasMessage
                ? FormatTagStoreStatus(tagStoreService.LastStatus)
                : UiText.Format("Status.LibraryLoaded", Games.Count);
            QueueRemoteIconRefresh();
        }
        catch (Exception ex)
        {
            ShowError("Message.LibraryLoadFailed", ex.Message);
            StatusMessage = UiText.Get("Status.LibraryLoadFailed");
        }
    }

    private void LoadArchives()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.ArchiveInboxDirectory);
            Archives.Clear();
            foreach (string path in archiveImportService.GetArchivePaths(AppSettings.ArchiveInboxDirectory))
            {
                Archives.Add(new ArchiveItem(path));
            }

            StatusMessage = UiText.Format("Status.ArchiveInboxReady", Archives.Count);
        }
        catch (Exception ex)
        {
            ShowError("Message.ArchiveScanFailed", ex.Message);
            StatusMessage = UiText.Get("Status.ArchiveScanFailed");
        }
    }

    private void UpdateAvailableTags()
    {
        AvailableTags.Clear();
        foreach (string tag in GameFilterState.BuildAvailableTags(Games))
        {
            AvailableTags.Add(tag);
        }

        SelectedTag = AvailableTags.Contains(SelectedTag)
            ? SelectedTag
            : GameFilterState.AllTagsLabel;
    }

    private bool GameFilter(object item)
    {
        return item is GameInfo game && filterState.Matches(game);
    }

    private void RefreshGamesView()
    {
        gamesView?.Refresh();
    }

    private void RefreshLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        LoadGames();
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        GameInfo game = ResolveGame(sender);
        if (game == null)
        {
            return;
        }

        if (!File.Exists(game.Executable))
        {
            ShowWarning("Message.ExecutableNotFound", game.Executable);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = game.Executable,
                UseShellExecute = true,
                WorkingDirectory = game.InstallDirectory
            });
            StatusMessage = UiText.Format("Status.Launched", game.Name);
        }
        catch (Exception ex)
        {
            ShowError("Message.LaunchFailed", ex.Message);
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        GameInfo game = ResolveGame(sender);
        if (game == null)
        {
            return;
        }

        string targetDirectory = !string.IsNullOrWhiteSpace(game.InstallDirectory)
            ? game.InstallDirectory
            : Path.GetDirectoryName(game.Executable);

        OpenFolder(targetDirectory);
    }

    private void SaveTagsButton_Click(object sender, RoutedEventArgs e)
    {
        GameInfo game = ResolveGame(sender);
        if (game == null)
        {
            return;
        }

        try
        {
            IReadOnlyList<string> tags = tagStoreService.SaveTags(game, Games);
            UpdateAvailableTags();
            RefreshGamesView();
            StatusMessage = tagStoreService.LastStatus.HasMessage
                ? FormatTagStoreStatus(tagStoreService.LastStatus)
                : UiText.Format("Status.TagsSaved", game.Name, tags.Count);
        }
        catch (Exception ex)
        {
            ShowError("Message.TagSaveFailed", ex.Message);
        }
    }

    private async void RunDownloaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (isDownloaderRunning)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(AppSettings.RuntimeDirectory);
            Directory.CreateDirectory(AppSettings.ArchiveInboxDirectory);

            ResetDownloadStepStatuses();
            DownloaderLog = string.Empty;
            SetDownloaderRunning(true);
            downloaderCancellation = new CancellationTokenSource();

            var options = new DownloadPipelineOptions
            {
                RuntimeDirectory = AppSettings.RuntimeDirectory,
                SaveDirectory = AppSettings.ArchiveInboxDirectory,
                DateFrom = ParseOptionalDate(DownloadDateFromText),
                DateTo = ParseOptionalDate(DownloadDateToText, endOfDay: true),
                StartStepIndex = GetSelectedStartStepIndex()
            };

            await downloadPipelineService.RunAsync(
                options,
                SetStepStatus,
                AppendDownloaderLog,
                downloaderCancellation.Token);

            LoadArchives();
            StatusMessage = UiText.Get("Status.DownloaderFinished");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = UiText.Get("Status.DownloaderStopped");
            AppendDownloaderLog(UiText.Get("Log.Stopped"));
        }
        catch (Exception ex)
        {
            StatusMessage = UiText.Get("Status.DownloaderFailed");
            AppendDownloaderLog(UiText.Format("Log.ErrorFormat", ex.Message));
            ShowError("Message.DownloaderFailed", ex.Message);
        }
        finally
        {
            downloaderCancellation?.Dispose();
            downloaderCancellation = null;
            SetDownloaderRunning(false);
        }
    }

    private void StopDownloaderButton_Click(object sender, RoutedEventArgs e)
    {
        downloaderCancellation?.Cancel();
    }

    private void RefreshArchivesButton_Click(object sender, RoutedEventArgs e)
    {
        LoadArchives();
    }

    private async void ExtractAllButton_Click(object sender, RoutedEventArgs e)
    {
        List<ArchiveItem> items = Archives.ToList();
        if (items.Count == 0)
        {
            StatusMessage = UiText.Get("Status.NoArchivesFound");
            return;
        }

        int importedCount = 0;
        int skippedCount = 0;
        int failedCount = 0;

        foreach (ArchiveItem archive in items)
        {
            archive.Status = UiText.Get("Archive.StatusExtracting");

            try
            {
                ArchiveImportResult result = await Task.Run(() =>
                    archiveImportService.ImportArchive(archive.FullPath, AppSettings.GamesDirectory));

                archive.Status = result.Message;
                if (result.Success && !result.Skipped)
                {
                    importedCount++;
                }
                else if (result.Skipped)
                {
                    skippedCount++;
                }
                else
                {
                    failedCount++;
                }
            }
            catch (Exception ex)
            {
                archive.Status = UiText.Get("Archive.StatusFailed");
                failedCount++;
                StatusMessage = UiText.Format("Status.ImportError", ex.Message);
            }
        }

        LoadGames();
        StatusMessage = UiText.Format(
            "Status.ImportComplete",
            importedCount,
            skippedCount,
            failedCount);
    }

    private void OpenInboxButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.ArchiveInboxDirectory);
        OpenFolder(AppSettings.ArchiveInboxDirectory);
    }

    private void OpenLibraryFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.GamesDirectory);
        OpenFolder(AppSettings.GamesDirectory);
    }

    private void ResetDownloadStepStatuses()
    {
        foreach (DownloadStepState step in DownloadSteps)
        {
            step.Status = UiText.Get("Status.Pending");
        }
    }

    private void SetStepStatus(int index, string status)
    {
        if (index < 0 || index >= DownloadSteps.Count)
        {
            return;
        }

        DownloadSteps[index].Status = status;
    }

    private void AppendDownloaderLog(string line)
    {
        DownloaderLog = string.IsNullOrEmpty(DownloaderLog)
            ? line
            : DownloaderLog + Environment.NewLine + line;
    }

    private int GetSelectedStartStepIndex()
    {
        if (string.IsNullOrWhiteSpace(SelectedDownloaderStartStep))
        {
            return 0;
        }

        int dashIndex = SelectedDownloaderStartStep.IndexOf('-');
        string stepPart = dashIndex >= 0
            ? SelectedDownloaderStartStep[..dashIndex]
            : SelectedDownloaderStartStep;
        string numericPart = new(stepPart.Where(char.IsDigit).ToArray());
        return int.TryParse(numericPart, out int index) ? index : 0;
    }

    private void SetDownloaderRunning(bool isRunning)
    {
        isDownloaderRunning = isRunning;
        OnPropertyChanged(nameof(CanStartDownloader));
        OnPropertyChanged(nameof(CanStopDownloader));
    }

    private static DateTime? ParseOptionalDate(string value, bool endOfDay = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        DateTime parsed = DateTime.Parse(value);
        return endOfDay
            ? parsed.Date.AddHours(23).AddMinutes(59).AddSeconds(59)
            : parsed.Date;
    }

    private static void OpenFolder(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            ShowWarning("Message.FolderNotFound", directoryPath);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "\"" + directoryPath + "\"",
            UseShellExecute = true
        });
    }

    private static GameInfo ResolveGame(object sender)
    {
        return (sender as FrameworkElement)?.Tag as GameInfo;
    }

    private static void ShowError(string messageKey, params object[] args)
    {
        MessageBox.Show(
            UiText.Format(messageKey, args),
            UiText.Get("Dialog.AppTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void ShowWarning(string messageKey, params object[] args)
    {
        MessageBox.Show(
            UiText.Format(messageKey, args),
            UiText.Get("Dialog.AppTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static string FormatTagStoreStatus(GameTagStoreStatus status)
    {
        return status != null && status.HasMessage
            ? UiText.Format(status.MessageKey, status.Args)
            : string.Empty;
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void QueueRemoteIconRefresh()
    {
        if (isRefreshingIcons || !AppSettings.EnableRemoteIconFetch || AppSettings.RemoteIconFetchLimitPerRun <= 0)
        {
            if (isRefreshingIcons)
            {
                refreshIconsAgain = true;
            }

            return;
        }

        _ = RefreshRemoteIconsInBackgroundAsync();
    }

    private async Task RefreshRemoteIconsInBackgroundAsync()
    {
        isRefreshingIcons = true;
        refreshIconsAgain = false;

        try
        {
            IReadOnlyList<GameInfo> refreshedGames = await Task.Run(() => remoteIconCatalogService.LoadGames());
            await Dispatcher.InvokeAsync(() =>
            {
                if (refreshIconsAgain)
                {
                    return;
                }

                Games.Clear();
                foreach (GameInfo game in refreshedGames)
                {
                    Games.Add(game);
                }

                UpdateAvailableTags();
                RefreshGamesView();
            });
        }
        catch
        {
        }
        finally
        {
            isRefreshingIcons = false;
            if (refreshIconsAgain)
            {
                QueueRemoteIconRefresh();
            }
        }
    }
}
