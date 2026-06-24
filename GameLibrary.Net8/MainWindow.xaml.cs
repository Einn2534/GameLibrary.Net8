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
using static GameLibrary.Net8.DownloadPipelineArtifacts;

namespace GameLibrary.Net8;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int GamesPageSize = 25;

    private readonly GameTagStoreService tagStoreService;
    private readonly GameCatalogService catalogService;
    private readonly GameCatalogService remoteIconCatalogService;
    private readonly GameFilterState filterState = new();
    private readonly ArchiveImportService archiveImportService;
    private readonly StorageMaintenanceService storageMaintenanceService;
    private readonly DownloadPipelineService downloadPipelineService;
    private readonly DownloadResumeService downloadResumeService;
    private readonly AudioSessionVolumeService audioSessionVolumeService = new();
    private readonly ICollectionView gamesView;
    private int currentGamePage = 1;
    private int totalGamePages = 1;
    private int filteredGameCount;
    private GameSortOption selectedGameSortOption;
    private CancellationTokenSource downloaderCancellation;
    private string statusMessage;
    private string downloadDateFromText;
    private string downloadDateToText;
    private string selectedDownloaderStartStep;
    private string downloaderResumeSummary;
    private string downloaderFailureRerunSummary;
    private string downloaderLog;
    private DownloadResumeState downloaderResumeState = DownloadResumeState.None;
    private bool isFailedOnlyDownloaderRun;
    private bool isDownloaderRunning;
    private bool isRefreshingIcons;
    private bool refreshIconsAgain;
    private bool isStorageMaintenanceRunning;
    private int downloaderFailureCount;
    private string expandedLibrarySummary;
    private GameInfo selectedGameDetail;
    private bool isGameDetailPopupOpen;

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
            AppSettings.ArchiveExtensions,
            AppSettings.MediaExtensions);

        storageMaintenanceService = new StorageMaintenanceService(archiveImportService);
        downloadPipelineService = new DownloadPipelineService();
        downloadResumeService = new DownloadResumeService();
        gamesView = CollectionViewSource.GetDefaultView(Games);
        gamesView.Filter = GameFilter;

        InitializeGameSortOptions();
        InitializeDownloaderState();
        LoadGames();
        LoadArchives();
        RefreshStorageMaintenance();
        LoadDownloaderFailures();
        RefreshDownloaderResumeState();
        StatusMessage = UiText.Get("Status.Ready");
    }

    public ObservableCollection<GameInfo> Games { get; } = new();

    public ObservableCollection<GameInfo> PagedGames { get; } = new();

    public GameInfo SelectedGameDetail
    {
        get => selectedGameDetail;
        private set
        {
            if (ReferenceEquals(selectedGameDetail, value))
            {
                return;
            }

            selectedGameDetail = value;
            OnPropertyChanged(nameof(SelectedGameDetail));
        }
    }

    public bool IsGameDetailPopupOpen
    {
        get => isGameDetailPopupOpen;
        private set
        {
            if (isGameDetailPopupOpen == value)
            {
                return;
            }

            isGameDetailPopupOpen = value;
            OnPropertyChanged(nameof(IsGameDetailPopupOpen));
        }
    }

    public ObservableCollection<GameSortOption> GameSortOptions { get; } = new();

    public ObservableCollection<string> AvailableTags { get; } = new();

    public ObservableCollection<ArchiveItem> Archives { get; } = new();

    public ObservableCollection<DownloadStepState> DownloadSteps { get; } = new();

    public ObservableCollection<string> DownloaderStartSteps { get; } = new();

    public ObservableCollection<DownloadFailureGroup> DownloaderFailureGroups { get; } = new();

    public ObservableCollection<StoredArchiveItem> StoredArchives { get; } = new();

    public ObservableCollection<StorageFindingItem> StorageFindings { get; } = new();

    public int CurrentGamePage
    {
        get => currentGamePage;
        private set
        {
            if (currentGamePage == value)
            {
                return;
            }

            currentGamePage = value;
            OnPropertyChanged(nameof(CurrentGamePage));
            NotifyGamePagePropertiesChanged();
        }
    }

    public int TotalGamePages
    {
        get => totalGamePages;
        private set
        {
            if (totalGamePages == value)
            {
                return;
            }

            totalGamePages = value;
            OnPropertyChanged(nameof(TotalGamePages));
            NotifyGamePagePropertiesChanged();
        }
    }

    public int FilteredGameCount
    {
        get => filteredGameCount;
        private set
        {
            if (filteredGameCount == value)
            {
                return;
            }

            filteredGameCount = value;
            OnPropertyChanged(nameof(FilteredGameCount));
            OnPropertyChanged(nameof(GamePageSummary));
        }
    }

    public string GamePageSummary => UiText.Format(
        "Library.PageSummary",
        CurrentGamePage,
        TotalGamePages,
        FilteredGameCount,
        GamesPageSize);

    public bool CanGoToPreviousGamePage => CurrentGamePage > 1;

    public bool CanGoToNextGamePage => CurrentGamePage < TotalGamePages;

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

    public GameSortOption SelectedGameSortOption
    {
        get => selectedGameSortOption;
        set
        {
            if (ReferenceEquals(selectedGameSortOption, value))
            {
                return;
            }

            selectedGameSortOption = value;
            OnPropertyChanged(nameof(SelectedGameSortOption));
            ApplyGameSort();
        }
    }

    public string RuntimeDirectoryPath => AppSettings.RuntimeDirectory;

    public string GamesDirectoryPath => AppSettings.GamesDirectory;

    public string ArchiveInboxDirectory => AppSettings.ArchiveInboxDirectory;

    public string ArchiveStorageDirectory => AppSettings.ArchiveStorageDirectory;

    public string VideoLibraryDirectory => AppSettings.VideoLibraryDirectory;

    public string SevenZipPath => AppSettings.SevenZipPath;

    public string ExpandedLibrarySummary
    {
        get => expandedLibrarySummary;
        set
        {
            if (string.Equals(expandedLibrarySummary, value, StringComparison.Ordinal))
            {
                return;
            }

            expandedLibrarySummary = value;
            OnPropertyChanged(nameof(ExpandedLibrarySummary));
        }
    }

    public int DownloaderFailureCount
    {
        get => downloaderFailureCount;
        private set
        {
            if (downloaderFailureCount == value)
            {
                return;
            }

            downloaderFailureCount = value;
            OnPropertyChanged(nameof(DownloaderFailureCount));
            OnPropertyChanged(nameof(DownloaderFailureSummary));
        }
    }

    public string DownloaderFailureSummary => UiText.Format("Downloader.FailureSummary", DownloaderFailureCount);

    public bool CanResumeLastDownloaderRun => !isDownloaderRunning && downloaderResumeState?.CanResume == true;

    public string DownloaderResumeSummary
    {
        get => downloaderResumeSummary;
        set
        {
            if (string.Equals(downloaderResumeSummary, value, StringComparison.Ordinal))
            {
                return;
            }

            downloaderResumeSummary = value;
            OnPropertyChanged(nameof(DownloaderResumeSummary));
        }
    }

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
            UpdateDownloaderFailureRerunSummary();
        }
    }

    public bool IsFailedOnlyDownloaderRun
    {
        get => isFailedOnlyDownloaderRun;
        set
        {
            if (isFailedOnlyDownloaderRun == value)
            {
                return;
            }

            isFailedOnlyDownloaderRun = value;
            OnPropertyChanged(nameof(IsFailedOnlyDownloaderRun));
            UpdateDownloaderFailureRerunSummary();
        }
    }

    public string DownloaderFailureRerunSummary
    {
        get => downloaderFailureRerunSummary;
        set
        {
            if (string.Equals(downloaderFailureRerunSummary, value, StringComparison.Ordinal))
            {
                return;
            }

            downloaderFailureRerunSummary = value;
            OnPropertyChanged(nameof(DownloaderFailureRerunSummary));
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

    public bool CanRunStorageMaintenance => !isStorageMaintenanceRunning;

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

    private void InitializeGameSortOptions()
    {
        GameSortOptions.Clear();
        GameSortOptions.Add(new GameSortOption(
            UiText.Get("Sort.NameAscending"),
            nameof(GameInfo.Name),
            ListSortDirection.Ascending));
        GameSortOptions.Add(new GameSortOption(
            UiText.Get("Sort.NameDescending"),
            nameof(GameInfo.Name),
            ListSortDirection.Descending));
        GameSortOptions.Add(new GameSortOption(
            UiText.Get("Sort.ModifiedDescending"),
            nameof(GameInfo.InstallDirectoryModifiedUtc),
            ListSortDirection.Descending));
        GameSortOptions.Add(new GameSortOption(
            UiText.Get("Sort.ModifiedAscending"),
            nameof(GameInfo.InstallDirectoryModifiedUtc),
            ListSortDirection.Ascending));
        GameSortOptions.Add(new GameSortOption(
            UiText.Get("Sort.CreatedDescending"),
            nameof(GameInfo.InstallDirectoryCreatedUtc),
            ListSortDirection.Descending));
        GameSortOptions.Add(new GameSortOption(
            UiText.Get("Sort.CreatedAscending"),
            nameof(GameInfo.InstallDirectoryCreatedUtc),
            ListSortDirection.Ascending));

        SelectedGameSortOption = GameSortOptions.FirstOrDefault();
    }

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
        IsFailedOnlyDownloaderRun = false;
        DownloaderResumeSummary = UiText.Get("Downloader.ResumeUnavailable");
        UpdateDownloaderFailureRerunSummary();
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
            RefreshStorageMaintenance(includeSizeSummary: false);
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

    private void RefreshStorageMaintenance(bool includeSizeSummary = false)
    {
        try
        {
            StoredArchives.Clear();
            foreach (StoredArchiveItem archive in storageMaintenanceService.LoadStoredArchives())
            {
                StoredArchives.Add(archive);
            }

            StorageFindings.Clear();
            foreach (StorageFindingItem finding in storageMaintenanceService.DetectFindings(Games.ToList()))
            {
                StorageFindings.Add(finding);
            }

            ExpandedLibrarySummary = includeSizeSummary
                ? storageMaintenanceService.BuildExpandedLibrarySummary(Games.ToList())
                : UiText.Get("Storage.SummaryNeedsRefresh");
        }
        catch (Exception ex)
        {
            StatusMessage = UiText.Format("Status.StorageScanFailed", ex.Message);
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
        RefreshGamesView(resetToFirstPage: true);
    }

    private void RefreshGamesView(bool resetToFirstPage)
    {
        if (gamesView == null)
        {
            return;
        }

        gamesView.Refresh();
        UpdatePagedGames(resetToFirstPage);
    }

    private void ApplyGameSort()
    {
        if (gamesView == null)
        {
            return;
        }

        using (gamesView.DeferRefresh())
        {
            gamesView.SortDescriptions.Clear();
            if (SelectedGameSortOption != null && !string.IsNullOrWhiteSpace(SelectedGameSortOption.PropertyName))
            {
                gamesView.SortDescriptions.Add(new SortDescription(
                    SelectedGameSortOption.PropertyName,
                    SelectedGameSortOption.Direction));

                if (!string.Equals(SelectedGameSortOption.PropertyName, nameof(GameInfo.Name), StringComparison.Ordinal))
                {
                    gamesView.SortDescriptions.Add(new SortDescription(nameof(GameInfo.Name), ListSortDirection.Ascending));
                }
            }
        }

        UpdatePagedGames(resetToFirstPage: true);
    }

    private void UpdatePagedGames(bool resetToFirstPage)
    {
        if (gamesView == null)
        {
            return;
        }

        List<GameInfo> filteredGames = gamesView.Cast<GameInfo>().ToList();
        FilteredGameCount = filteredGames.Count;
        TotalGamePages = Math.Max(1, (int)Math.Ceiling(filteredGames.Count / (double)GamesPageSize));
        CurrentGamePage = resetToFirstPage
            ? 1
            : Math.Clamp(CurrentGamePage, 1, TotalGamePages);

        PagedGames.Clear();
        foreach (GameInfo game in filteredGames
            .Skip((CurrentGamePage - 1) * GamesPageSize)
            .Take(GamesPageSize))
        {
            PagedGames.Add(game);
        }
    }

    private void NotifyGamePagePropertiesChanged()
    {
        OnPropertyChanged(nameof(GamePageSummary));
        OnPropertyChanged(nameof(CanGoToPreviousGamePage));
        OnPropertyChanged(nameof(CanGoToNextGamePage));
    }

    private void GoToGamePage(int page)
    {
        int targetPage = Math.Clamp(page, 1, TotalGamePages);
        if (targetPage == CurrentGamePage)
        {
            return;
        }

        CurrentGamePage = targetPage;
        UpdatePagedGames(resetToFirstPage: false);
    }

    private void PreviousGamePageButton_Click(object sender, RoutedEventArgs e)
    {
        GoToGamePage(CurrentGamePage - 1);
    }

    private void NextGamePageButton_Click(object sender, RoutedEventArgs e)
    {
        GoToGamePage(CurrentGamePage + 1);
    }

    private void RefreshLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        LoadGames();
    }

    private void OpenGameDetailButton_Click(object sender, RoutedEventArgs e)
    {
        GameInfo game = ResolveGame(sender);
        if (game == null)
        {
            return;
        }

        SelectedGameDetail = game;
        IsGameDetailPopupOpen = true;
    }

    private void CloseGameDetailButton_Click(object sender, RoutedEventArgs e)
    {
        CloseGameDetailPopup();
    }

    private void CloseGameDetailPopup()
    {
        IsGameDetailPopupOpen = false;
        SelectedGameDetail = null;
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        GameInfo game = ResolveGame(sender);
        if (game == null)
        {
            return;
        }

        if (game.IsVideo)
        {
            if (!File.Exists(game.MediaPath))
            {
                ShowWarning("Message.MediaNotFound", game.MediaPath);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = game.MediaPath,
                    UseShellExecute = true
                });
                StatusMessage = UiText.Format("Status.Played", game.Name);
            }
            catch (Exception ex)
            {
                ShowError("Message.MediaLaunchFailed", ex.Message);
            }

            return;
        }

        if (!File.Exists(game.Executable))
        {
            ShowWarning("Message.ExecutableNotFound", game.Executable);
            return;
        }

        try
        {
            Process process = Process.Start(new ProcessStartInfo
            {
                FileName = game.Executable,
                UseShellExecute = true,
                WorkingDirectory = game.InstallDirectory
            });
            QueueLaunchVolumeAdjustment(process, game);
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

        string targetDirectory = game.IsVideo
            ? Path.GetDirectoryName(game.MediaPath)
            : !string.IsNullOrWhiteSpace(game.InstallDirectory)
            ? game.InstallDirectory
            : Path.GetDirectoryName(game.Executable);

        OpenFolder(targetDirectory);
    }

    private async void DeleteInstallButton_Click(object sender, RoutedEventArgs e)
    {
        GameInfo game = ResolveGame(sender);
        if (game == null || isStorageMaintenanceRunning)
        {
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            UiText.Format("Message.DeleteInstallPrompt", game.Name),
            UiText.Get("Dialog.AppTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        SetStorageMaintenanceRunning(true);
        try
        {
            StorageActionResult actionResult = await Task.Run(() => storageMaintenanceService.DeleteExpandedInstall(game));
            LoadGames();
            RefreshStorageMaintenance(includeSizeSummary: true);
            StatusMessage = actionResult.Message + " Freed " + StorageMaintenanceService.FormatSize(actionResult.BytesChanged) + ".";
        }
        catch (Exception ex)
        {
            ShowError("Message.StorageActionFailed", ex.Message);
        }
        finally
        {
            SetStorageMaintenanceRunning(false);
        }
    }

    private async void CompressInstallButton_Click(object sender, RoutedEventArgs e)
    {
        GameInfo game = ResolveGame(sender);
        if (game == null || isStorageMaintenanceRunning)
        {
            return;
        }

        SetStorageMaintenanceRunning(true);
        try
        {
            StorageActionResult actionResult = await storageMaintenanceService.CompressExpandedInstallAsync(
                game,
                CancellationToken.None);
            RefreshStorageMaintenance(includeSizeSummary: true);
            StatusMessage = actionResult.Message;
        }
        catch (Exception ex)
        {
            ShowError("Message.StorageActionFailed", ex.Message);
        }
        finally
        {
            SetStorageMaintenanceRunning(false);
        }
    }

    private void RefreshStorageButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshStorageMaintenance(includeSizeSummary: true);
        StatusMessage = UiText.Format("Status.StorageScanComplete", StoredArchives.Count, StorageFindings.Count);
    }

    private async void RestoreStoredArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        StoredArchiveItem archive = ResolveStoredArchive(sender);
        if (archive == null || isStorageMaintenanceRunning)
        {
            return;
        }

        SetStorageMaintenanceRunning(true);
        archive.Status = UiText.Get("Archive.StatusExtracting");
        try
        {
            ArchiveImportResult result = await Task.Run(() => storageMaintenanceService.RestoreArchive(archive.FullPath));
            archive.Status = result.Message;
            LoadGames();
            LoadArchives();
            RefreshStorageMaintenance(includeSizeSummary: true);

            StorageActionResult pruneResult = await PruneExpandedLibraryIfNeededAsync(auto: true);
            StatusMessage = pruneResult?.ItemCount > 0
                ? result.Message + " " + pruneResult.Message
                : result.Message;
        }
        catch (Exception ex)
        {
            archive.Status = UiText.Get("Archive.StatusFailed");
            ShowError("Message.StorageActionFailed", ex.Message);
        }
        finally
        {
            SetStorageMaintenanceRunning(false);
        }
    }

    private async void PruneExpandedLibraryButton_Click(object sender, RoutedEventArgs e)
    {
        if (isStorageMaintenanceRunning)
        {
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            UiText.Get("Message.PruneExpandedLibraryPrompt"),
            UiText.Get("Dialog.AppTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        SetStorageMaintenanceRunning(true);
        try
        {
            StorageActionResult pruneResult = await PruneExpandedLibraryIfNeededAsync(auto: false);
            StatusMessage = pruneResult?.Message ?? UiText.Get("Status.Ready");
        }
        catch (Exception ex)
        {
            ShowError("Message.StorageActionFailed", ex.Message);
        }
        finally
        {
            SetStorageMaintenanceRunning(false);
        }
    }

    private void OpenIconSourcePageButton_Click(object sender, RoutedEventArgs e)
    {
        GameInfo game = ResolveGame(sender);
        string sourcePageUrl = game?.RemoteIconFetchDiagnostics?.SourcePageUrl;
        if (string.IsNullOrWhiteSpace(sourcePageUrl))
        {
            StatusMessage = UiText.Get("Status.RemoteIconSourceUnavailable");
            return;
        }

        OpenUrl(sourcePageUrl);
    }

    private void CopyIconDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        GameInfo game = ResolveGame(sender);
        string diagnosticText = game?.RemoteIconFetchDiagnostics?.ToDiagnosticText(game.Name) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(diagnosticText))
        {
            StatusMessage = UiText.Get("Status.RemoteIconDiagnosticsUnavailable");
            return;
        }

        Clipboard.SetText(diagnosticText);
        StatusMessage = UiText.Format("Status.RemoteIconDiagnosticsCopied", game.Name);
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
            RefreshGamesView(resetToFirstPage: false);
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
        await RunDownloaderAsync();
    }

    private async Task RunDownloaderAsync()
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
                StartStepIndex = GetSelectedStartStepIndex(),
                FailedOnly = IsFailedOnlyDownloaderRun
            };

            await downloadPipelineService.RunAsync(
                options,
                SetStepStatus,
                AppendDownloaderLog,
                downloaderCancellation.Token);

            LoadArchives();
            LoadDownloaderFailures();
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
            LoadDownloaderFailures();
            RefreshDownloaderResumeState();
        }
    }

    private void StopDownloaderButton_Click(object sender, RoutedEventArgs e)
    {
        downloaderCancellation?.Cancel();
    }

    private async void ResumeDownloaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (isDownloaderRunning)
        {
            return;
        }

        RefreshDownloaderResumeState();
        if (downloaderResumeState?.CanResume != true)
        {
            StatusMessage = UiText.Get("Downloader.ResumeUnavailable");
            return;
        }

        ApplyDownloaderResumeState(downloaderResumeState);
        await RunDownloaderAsync();
    }

    private void RefreshDownloaderResumeState()
    {
        try
        {
            downloaderResumeState = downloadResumeService.Detect(
                AppSettings.RuntimeDirectory,
                AppSettings.ArchiveInboxDirectory);
            DownloaderResumeSummary = downloaderResumeState.Summary;
        }
        catch (Exception ex)
        {
            downloaderResumeState = DownloadResumeState.None;
            DownloaderResumeSummary = UiText.Format("Downloader.ResumeDetectionFailed", ex.Message);
        }

        OnPropertyChanged(nameof(CanResumeLastDownloaderRun));
    }

    private void ApplyDownloaderResumeState(DownloadResumeState resumeState)
    {
        SelectDownloaderStartStep(resumeState.StartStepIndex);
        if (resumeState.DateFrom.HasValue)
        {
            DownloadDateFromText = resumeState.DateFrom.Value.ToString("yyyy-MM-dd");
        }

        if (resumeState.DateTo.HasValue)
        {
            DownloadDateToText = resumeState.DateTo.Value.ToString("yyyy-MM-dd");
        }

        IsFailedOnlyDownloaderRun = false;
        StatusMessage = UiText.Format(
            "Status.DownloaderResumeReady",
            resumeState.StartStepIndex,
            DownloadPipelineService.StepLabels[resumeState.StartStepIndex]);
    }

    private void UpdateDownloaderFailureRerunSummary()
    {
        if (!IsFailedOnlyDownloaderRun)
        {
            DownloaderFailureRerunSummary = UiText.Get("Downloader.FailedOnlyOff");
            return;
        }

        try
        {
            DownloadFailureRerunInfo rerunInfo = downloadPipelineService.GetFailureRerunInfo(
                AppSettings.RuntimeDirectory,
                GetSelectedStartStepIndex());
            DownloaderFailureRerunSummary = UiText.Format(
                "Downloader.FailedOnlySummary",
                rerunInfo.FailureSetName,
                rerunInfo.UnresolvedCount);
        }
        catch (Exception ex)
        {
            DownloaderFailureRerunSummary = UiText.Format("Downloader.FailedOnlyUnavailable", ex.Message);
        }
    }

    private void RefreshArchivesButton_Click(object sender, RoutedEventArgs e)
    {
        LoadArchives();
    }

    private void RefreshDownloaderFailuresButton_Click(object sender, RoutedEventArgs e)
    {
        LoadDownloaderFailures();
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
            bool isMedia = archiveImportService.IsMediaPath(archive.FullPath);
            archive.Status = UiText.Get(isMedia ? "Media.StatusImporting" : "Archive.StatusExtracting");

            try
            {
                ArchiveImportResult result = isMedia
                    ? await Task.Run(() => archiveImportService.ImportMedia(archive.FullPath, AppSettings.VideoLibraryDirectory))
                    : await Task.Run(() => archiveImportService.ImportArchive(archive.FullPath, AppSettings.GamesDirectory));

                archive.Status = result.Message;
                if (result.Success && !result.Skipped)
                {
                    importedCount++;
                    if (!isMedia)
                    {
                        TryMoveArchiveToStorage(archive);
                    }
                }
                else if (result.Skipped)
                {
                    skippedCount++;
                    if (!isMedia)
                    {
                        TryMoveArchiveToStorage(archive);
                    }
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
        LoadArchives();
            RefreshStorageMaintenance(includeSizeSummary: true);
        StorageActionResult pruneResult = await PruneExpandedLibraryIfNeededAsync(auto: true);

        string importMessage = UiText.Format(
            "Status.ImportComplete",
            importedCount,
            skippedCount,
            failedCount);
        StatusMessage = pruneResult?.ItemCount > 0
            ? importMessage + " " + pruneResult.Message
            : importMessage;
    }

    private void OpenInboxButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.ArchiveInboxDirectory);
        OpenFolder(AppSettings.ArchiveInboxDirectory);
    }

    private void OpenRuntimeFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.RuntimeDirectory);
        OpenFolder(AppSettings.RuntimeDirectory);
    }

    private void OpenLibraryFolderButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.GamesDirectory);
        OpenFolder(AppSettings.GamesDirectory);
    }

    private void CopyAllFailureLinksButton_Click(object sender, RoutedEventArgs e)
    {
        CopyFailureLinks(DownloaderFailureGroups.SelectMany(group => group.Entries));
    }

    private void CopyFailureLinksButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DownloadFailureGroup group)
        {
            CopyFailureLinks(group.Entries);
        }
    }

    private async void RerunFailureGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DownloadFailureGroup group || isDownloaderRunning)
        {
            return;
        }

        LoadDownloaderFailures();
        SelectDownloaderStartStep(group.RerunStepIndex);
        IsFailedOnlyDownloaderRun = true;
        UpdateDownloaderFailureRerunSummary();

        string stepLabel = DownloadPipelineService.StepLabels[Math.Clamp(
            group.RerunStepIndex,
            0,
            DownloadPipelineService.StepLabels.Count - 1)];
        StatusMessage = UiText.Format("Status.DownloaderFailureRerunReady", group.FileName, group.RerunStepIndex, stepLabel);

        MessageBoxResult result = MessageBox.Show(
            UiText.Format("Message.DownloaderFailureRerunPrompt", group.Title, group.RerunStepIndex, stepLabel),
            UiText.Get("Dialog.AppTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            await RunDownloaderAsync();
        }
    }

    private void LoadDownloaderFailures()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.RuntimeDirectory);
            DownloaderFailureGroups.Clear();

            AddDownloaderFailureGroup(
                UiText.Get("Downloader.FailureGroup.HostLinks"),
                HostLinkFailuresFile,
                2);
            AddDownloaderFailureGroup(
                UiText.Get("Downloader.FailureGroup.PrimaryDownloads"),
                PrimaryFailuresFile,
                4);
            AddDownloaderFailureGroup(
                UiText.Get("Downloader.FailureGroup.MirrorDownloads"),
                MirrorFailuresFile,
                7);

            DownloaderFailureCount = DownloaderFailureGroups.Sum(group => group.Count);
            UpdateDownloaderFailureRerunSummary();
            StatusMessage = DownloaderFailureCount == 0
                ? UiText.Get("Status.DownloaderFailuresNone")
                : UiText.Format("Status.DownloaderFailuresLoaded", DownloaderFailureCount);
        }
        catch (Exception ex)
        {
            StatusMessage = UiText.Format("Status.DownloaderFailuresLoadFailed", ex.Message);
        }
    }

    private void AddDownloaderFailureGroup(string title, string fileName, int rerunStepIndex)
    {
        string path = Path.Combine(AppSettings.RuntimeDirectory, fileName);
        List<DownloadEntry> entries = LoadDownloadFailureEntries(path);
        DownloaderFailureGroups.Add(new DownloadFailureGroup(
            title,
            fileName,
            rerunStepIndex,
            entries
                .Where(entry => entry != null)
                .Select(entry => new DownloadFailureItem(entry))));
    }

    private static List<DownloadEntry> LoadDownloadFailureEntries(string path)
    {
        return LoadJson<List<DownloadEntry>>(path) ?? [];
    }

    private void CopyFailureLinks(IEnumerable<DownloadFailureItem> items)
    {
        List<string> links = items
            .Where(item => item != null)
            .SelectMany(item => item.Urls)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (links.Count == 0)
        {
            StatusMessage = UiText.Get("Status.DownloaderFailureLinksNone");
            return;
        }

        Clipboard.SetText(string.Join(Environment.NewLine, links));
        StatusMessage = UiText.Format("Status.DownloaderFailureLinksCopied", links.Count);
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
        Dispatcher.BeginInvoke(() => DownloaderLogTextBox.ScrollToEnd());
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

    private void SelectDownloaderStartStep(int stepIndex)
    {
        if (DownloaderStartSteps.Count == 0)
        {
            return;
        }

        int clampedIndex = Math.Clamp(stepIndex, 0, DownloaderStartSteps.Count - 1);
        SelectedDownloaderStartStep = DownloaderStartSteps[clampedIndex];
    }

    private void TryMoveArchiveToStorage(ArchiveItem archive)
    {
        try
        {
            MoveArchiveToStorage(archive);
        }
        catch (Exception ex)
        {
            StatusMessage = UiText.Format("Status.ImportError", ex.Message);
        }
    }

    private static void MoveArchiveToStorage(ArchiveItem archive)
    {
        if (archive == null || string.IsNullOrWhiteSpace(archive.FullPath) || !File.Exists(archive.FullPath))
        {
            return;
        }

        Directory.CreateDirectory(AppSettings.ArchiveStorageDirectory);
        string destinationPath = GetUniqueArchiveStoragePath(archive.FullPath, AppSettings.ArchiveStorageDirectory);
        File.Move(archive.FullPath, destinationPath);
    }

    private static string GetUniqueArchiveStoragePath(string archivePath, string storageDirectory)
    {
        string fileName = Path.GetFileNameWithoutExtension(archivePath);
        string extension = Path.GetExtension(archivePath);
        string destinationPath = Path.Combine(storageDirectory, Path.GetFileName(archivePath));
        int suffix = 1;

        while (File.Exists(destinationPath))
        {
            destinationPath = Path.Combine(storageDirectory, fileName + " (" + suffix + ")" + extension);
            suffix++;
        }

        return destinationPath;
    }

    private async Task<StorageActionResult> PruneExpandedLibraryIfNeededAsync(bool auto)
    {
        if (auto && (!AppSettings.EnableAutoPruneExpandedLibrary || AppSettings.ExpandedLibrarySizeLimitGb <= 0))
        {
            return null;
        }

        List<GameInfo> games = Games.ToList();
        StorageActionResult result = await Task.Run(() => storageMaintenanceService.PruneExpandedLibraryIfNeeded(games));
        if (result.ItemCount > 0)
        {
            LoadGames();
        }

        RefreshStorageMaintenance(includeSizeSummary: true);
        return result;
    }

    private void SetDownloaderRunning(bool isRunning)
    {
        isDownloaderRunning = isRunning;
        OnPropertyChanged(nameof(CanStartDownloader));
        OnPropertyChanged(nameof(CanStopDownloader));
        OnPropertyChanged(nameof(CanResumeLastDownloaderRun));
    }

    private void SetStorageMaintenanceRunning(bool isRunning)
    {
        isStorageMaintenanceRunning = isRunning;
        OnPropertyChanged(nameof(CanRunStorageMaintenance));
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

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            ShowWarning("Message.UrlNotFound", url);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private static GameInfo ResolveGame(object sender)
    {
        return (sender as FrameworkElement)?.Tag as GameInfo;
    }

    private static StoredArchiveItem ResolveStoredArchive(object sender)
    {
        return (sender as FrameworkElement)?.Tag as StoredArchiveItem;
    }

    private void QueueLaunchVolumeAdjustment(Process process, GameInfo game)
    {
        if (game == null || AppSettings.LaunchVolumePercent >= 100)
        {
            return;
        }

        int processId = process?.Id ?? 0;
        int volumePercent = AppSettings.LaunchVolumePercent;
        string executablePath = game.Executable;
        string installDirectory = game.InstallDirectory;
        string gameName = game.Name;

        _ = Task.Run(async () =>
        {
            try
            {
                await audioSessionVolumeService.SetLaunchVolumeAsync(
                    processId,
                    executablePath,
                    installDirectory,
                    gameName,
                    volumePercent);
            }
            catch
            {
            }
        });
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
                RefreshGamesView(resetToFirstPage: false);
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
