using Newtonsoft.Json;
using System.Text.RegularExpressions;
using static GameLibrary.Net8.DownloadPipelineArtifacts;
using static GameLibrary.Net8.DownloadFileInspector;
using static GameLibrary.Net8.FileExtensionMatcher;

namespace GameLibrary.Net8;

public class GamesJsonGenerator
{
    private static readonly HashSet<string> ExcludedExecutableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnityCrashHandler64.exe",
        "notification_helper.exe"
    };

    private readonly string defaultDescription;
    private readonly GameIconPipelineService iconPipelineService;

    public GamesJsonGenerator(
        string defaultDescription = null,
        GameIconPipelineService iconPipelineService = null)
    {
        this.defaultDescription = string.IsNullOrWhiteSpace(defaultDescription)
            ? "Auto-generated game entry"
            : defaultDescription;
        this.iconPipelineService = iconPipelineService ?? new GameIconPipelineService(
            AppSettings.IconCacheDirectory,
            AppSettings.EnableRemoteIconFetch,
            AppSettings.RemoteIconFetchLimitPerRun);
    }

    public void Generate(string gamesDirectory, string outputJsonPath)
    {
        var gameList = new List<GameInfo>();
        List<GameInfo> existingGames = LoadExistingGames(outputJsonPath);
        Dictionary<string, GameInfo> existingByInstallDirectory = existingGames
            .Where(game => game != null && game.IsVideo != true && !string.IsNullOrWhiteSpace(game.InstallDirectory))
            .GroupBy(game => NormalizePathKey(game.InstallDirectory), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, GameInfo> existingByMediaPath = existingGames
            .Where(game => game != null && game.IsVideo && !string.IsNullOrWhiteSpace(game.MediaPath))
            .GroupBy(game => NormalizePathKey(game.MediaPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenInstallDirectories = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenMediaPaths = new(StringComparer.OrdinalIgnoreCase);
        int remainingRemoteFetches = iconPipelineService.RemoteFetchLimitPerRun;
        string videoLibraryDirectory = AppSettings.VideoLibraryDirectory;

        foreach (string gameRootDirectory in EnumerateCandidateGameDirectories(gamesDirectory, videoLibraryDirectory))
        {
            string installDirectoryKey = NormalizePathKey(gameRootDirectory);
            if (!seenInstallDirectories.Add(installDirectoryKey))
            {
                continue;
            }

            if (existingByInstallDirectory.TryGetValue(installDirectoryKey, out GameInfo existingGame) &&
                IsExistingGameStillUsable(existingGame))
            {
                GameInfo refreshedExistingGame = RefreshExistingGame(
                    existingGame,
                    remainingRemoteFetches > 0,
                    out bool attemptedRemoteFetch);
                if (attemptedRemoteFetch)
                {
                    remainingRemoteFetches--;
                }

                gameList.Add(refreshedExistingGame);
                continue;
            }

            string exePath = Directory
                .EnumerateFiles(gameRootDirectory, "*.exe", SearchOption.AllDirectories)
                .Where(path => !IsExcludedExecutablePath(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(exePath))
            {
                continue;
            }

            string gameName = new DirectoryInfo(gameRootDirectory).Name;
            GameIconResolveResult iconResult = ResolveIcon(gameRootDirectory, gameName, remainingRemoteFetches > 0);
            GameSourceMetadata sourceMetadata = iconPipelineService.ResolveSourceMetadata(gameName, gameRootDirectory);
            GameIconFetchDiagnostics fetchDiagnostics = iconPipelineService.ResolveFetchDiagnostics(gameName, gameRootDirectory);
            if (iconResult.AttemptedRemoteFetch)
            {
                remainingRemoteFetches--;
            }

            List<string> defaultTags = sourceMetadata.DefaultTags.Count > 0
                ? sourceMetadata.DefaultTags
                : BuildTags(gameName);

            gameList.Add(new GameInfo
            {
                Name = gameName,
                Icon = iconResult.IconPath,
                Executable = exePath,
                InstallDirectory = gameRootDirectory,
                Description = defaultDescription,
                SourcePageUrl = sourceMetadata.SourcePageUrl,
                RemoteIconFetchDiagnostics = fetchDiagnostics,
                DefaultTags = defaultTags,
                Tags = defaultTags.ToList()
            });
        }

        List<DownloadEntry> downloadMetadataRecords = LoadDownloadMetadataRecords();
        foreach (string videoPath in EnumerateVideoFiles(videoLibraryDirectory))
        {
            string mediaPathKey = NormalizePathKey(videoPath);
            if (!seenMediaPaths.Add(mediaPathKey))
            {
                continue;
            }

            existingByMediaPath.TryGetValue(mediaPathKey, out GameInfo existingVideo);
            DownloadEntry metadata = FindVideoDownloadMetadata(videoPath, downloadMetadataRecords);
            gameList.Add(BuildVideoEntry(videoPath, existingVideo, metadata));
        }

        foreach (GameInfo existingGame in existingGames)
        {
            if (existingGame == null || existingGame.IsVideo)
            {
                continue;
            }

            string installDirectoryKey = NormalizePathKey(existingGame.InstallDirectory);
            if (string.IsNullOrWhiteSpace(installDirectoryKey) ||
                seenInstallDirectories.Contains(installDirectoryKey) ||
                !IsExistingGameStillUsable(existingGame))
            {
                continue;
            }

            gameList.Add(existingGame);
        }

        string outputDirectory = Path.GetDirectoryName(outputJsonPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        string jsonContent = JsonConvert.SerializeObject(gameList, Formatting.Indented);
        File.WriteAllText(outputJsonPath, jsonContent);
    }

    private GameIconResolveResult ResolveIcon(string gameRootDirectory, string gameName, bool allowRemoteFetch)
    {
        return iconPipelineService.ResolveIcon(gameName, gameRootDirectory, allowRemoteFetch);
    }

    private GameInfo RefreshExistingGame(GameInfo existingGame, bool allowRemoteFetch, out bool attemptedRemoteFetch)
    {
        attemptedRemoteFetch = false;
        if (existingGame == null)
        {
            return null;
        }

        string gameName = string.IsNullOrWhiteSpace(existingGame.Name)
            ? new DirectoryInfo(existingGame.InstallDirectory).Name
            : existingGame.Name;

        bool hasExistingIcon = HasUsableIcon(existingGame.Icon);
        bool shouldResolveMissingIcon = !hasExistingIcon;
        GameIconResolveResult iconResult = shouldResolveMissingIcon
            ? iconPipelineService.ResolveIcon(gameName, existingGame.InstallDirectory, allowRemoteFetch, existingGame.SourcePageUrl)
            : new GameIconResolveResult(existingGame.Icon, attemptedRemoteFetch: false);
        attemptedRemoteFetch = iconResult.AttemptedRemoteFetch;
        GameSourceMetadata sourceMetadata = iconPipelineService.ResolveSourceMetadata(
            gameName,
            existingGame.InstallDirectory,
            existingGame.SourcePageUrl);
        List<string> defaultTags = sourceMetadata.DefaultTags.Count > 0
            ? sourceMetadata.DefaultTags
            : NormalizeTags(existingGame.DefaultTags.Count > 0 ? existingGame.DefaultTags : existingGame.Tags);
        GameIconFetchDiagnostics fetchDiagnostics = iconPipelineService.ResolveFetchDiagnostics(gameName, existingGame.InstallDirectory);

        return new GameInfo
        {
            Name = gameName,
            EntryType = GameInfo.GameEntryType,
            Icon = string.IsNullOrWhiteSpace(iconResult.IconPath)
                ? (hasExistingIcon ? existingGame.Icon : string.Empty)
                : iconResult.IconPath,
            Executable = existingGame.Executable,
            InstallDirectory = existingGame.InstallDirectory,
            Description = string.IsNullOrWhiteSpace(existingGame.Description) ? defaultDescription : existingGame.Description,
            SourcePageUrl = string.IsNullOrWhiteSpace(sourceMetadata.SourcePageUrl)
                ? existingGame.SourcePageUrl
                : sourceMetadata.SourcePageUrl,
            RemoteIconFetchDiagnostics = fetchDiagnostics,
            DefaultTags = defaultTags,
            Tags = defaultTags.ToList()
        };
    }

    private GameInfo BuildVideoEntry(string videoPath, GameInfo existingVideo, DownloadEntry metadata)
    {
        string videoName = FirstNonEmpty(metadata?.Name, existingVideo?.Name, Path.GetFileNameWithoutExtension(videoPath));
        string iconPath = HasUsableIcon(metadata?.IconPath)
            ? metadata.IconPath
            : HasUsableIcon(existingVideo?.Icon)
                ? existingVideo.Icon
                : string.Empty;
        List<string> defaultTags = metadata?.DefaultTags?.Count > 0
            ? NormalizeTags(metadata.DefaultTags)
            : NormalizeTags(existingVideo?.DefaultTags?.Count > 0 ? existingVideo.DefaultTags : existingVideo?.Tags);
        if (!defaultTags.Contains("Video", StringComparer.OrdinalIgnoreCase))
        {
            defaultTags.Add("Video");
        }

        return new GameInfo
        {
            Name = videoName,
            EntryType = GameInfo.VideoEntryType,
            Icon = iconPath,
            Executable = string.Empty,
            InstallDirectory = Path.GetDirectoryName(videoPath),
            MediaPath = videoPath,
            Description = string.IsNullOrWhiteSpace(existingVideo?.Description)
                ? "Downloaded video"
                : existingVideo.Description,
            SourcePageUrl = FirstNonEmpty(metadata?.ArticleUrl, existingVideo?.SourcePageUrl),
            RemoteIconFetchDiagnostics = new GameIconFetchDiagnostics(),
            DefaultTags = defaultTags,
            Tags = defaultTags.ToList()
        };
    }

    private static bool HasUsableIcon(string iconPath)
    {
        return !string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath);
    }

    private static List<GameInfo> LoadExistingGames(string outputJsonPath)
    {
        if (string.IsNullOrWhiteSpace(outputJsonPath) || !File.Exists(outputJsonPath))
        {
            return new List<GameInfo>();
        }

        try
        {
            string jsonContent = File.ReadAllText(outputJsonPath);
            return JsonConvert.DeserializeObject<List<GameInfo>>(jsonContent) ?? new List<GameInfo>();
        }
        catch (JsonException)
        {
            return new List<GameInfo>();
        }
        catch (IOException)
        {
            return new List<GameInfo>();
        }
        catch (UnauthorizedAccessException)
        {
            return new List<GameInfo>();
        }
    }

    private static IEnumerable<string> EnumerateCandidateGameDirectories(string gamesDirectory, string excludedDirectory)
    {
        if (string.IsNullOrWhiteSpace(gamesDirectory) || !Directory.Exists(gamesDirectory))
        {
            yield break;
        }

        foreach (string directory in Directory.EnumerateDirectories(gamesDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (IsSamePath(directory, excludedDirectory))
            {
                continue;
            }

            yield return directory;
        }

        if (Directory.EnumerateFiles(gamesDirectory, "*.exe", SearchOption.TopDirectoryOnly).Any())
        {
            yield return gamesDirectory;
        }
    }

    private static bool IsExistingGameStillUsable(GameInfo game)
    {
        return game != null &&
               !string.IsNullOrWhiteSpace(game.InstallDirectory) &&
               !game.IsVideo &&
               Directory.Exists(game.InstallDirectory) &&
               !string.IsNullOrWhiteSpace(game.Executable) &&
               File.Exists(game.Executable) &&
               !IsExcludedExecutablePath(game.Executable);
    }

    private static string NormalizePathKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static List<string> NormalizeTags(IEnumerable<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();

        foreach (string tag in tags ?? Enumerable.Empty<string>())
        {
            string trimmed = tag?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || !seen.Add(trimmed))
            {
                continue;
            }

            results.Add(trimmed);
        }

        return results;
    }

    private static IEnumerable<string> EnumerateVideoFiles(string videoLibraryDirectory)
    {
        if (string.IsNullOrWhiteSpace(videoLibraryDirectory) || !Directory.Exists(videoLibraryDirectory))
        {
            yield break;
        }

        HashSet<string> mediaExtensions = ToExtensionSet(AppSettings.MediaExtensions);
        if (mediaExtensions.Count == 0)
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(videoLibraryDirectory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => mediaExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            yield return path;
        }
    }

    private static List<DownloadEntry> LoadDownloadMetadataRecords()
    {
        var results = new List<DownloadEntry>();
        foreach (string fileName in new[] { DownloadRecordsFile, PrimaryResolvedFile, MirrorResolvedFile })
        {
            string path = Path.Combine(AppSettings.RuntimeDirectory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                List<DownloadEntry> records = LoadJson<List<DownloadEntry>>(path) ?? [];
                foreach (DownloadEntry record in records.Where(record => record != null))
                {
                    if (!results.Any(existing => IsSameDownloadMetadata(existing, record)))
                    {
                        results.Add(record);
                    }
                }
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return results;
    }

    private static DownloadEntry FindVideoDownloadMetadata(string videoPath, IEnumerable<DownloadEntry> records)
    {
        string videoFileName = Path.GetFileName(videoPath);
        string videoBaseName = NormalizeDuplicateFileBaseName(Path.GetFileNameWithoutExtension(videoPath));
        string videoProductCode = ExtractProductCode(videoFileName);

        return records
            .Where(record => record != null)
            .FirstOrDefault(record =>
                IsSameFileName(videoFileName, record.DownloadedFilePath) ||
                IsSameBaseName(videoBaseName, record.DownloadedFilePath) ||
                IsSameBaseName(videoBaseName, record.Name) ||
                (!string.IsNullOrWhiteSpace(videoProductCode) &&
                    string.Equals(videoProductCode, ExtractProductCode(record.Name), StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsSameDownloadMetadata(DownloadEntry left, DownloadEntry right)
    {
        return string.Equals(left?.ArticleUrl, right?.ArticleUrl, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(left?.Name) &&
                string.Equals(left.Name, right?.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSameFileName(string fileName, string path)
    {
        return !string.IsNullOrWhiteSpace(fileName) &&
            !string.IsNullOrWhiteSpace(path) &&
            string.Equals(fileName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameBaseName(string baseName, string value)
    {
        if (string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string candidateBaseName = Path.HasExtension(value)
            ? Path.GetFileNameWithoutExtension(value)
            : value;
        string normalizedCandidate = NormalizeDuplicateFileBaseName(SanitizeFileName(candidateBaseName));
        string normalizedFullValue = NormalizeDuplicateFileBaseName(SanitizeFileName(value));

        return string.Equals(baseName, normalizedCandidate, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(baseName, normalizedFullValue, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDuplicateFileBaseName(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"\s+\(\d+\)$", string.Empty).Trim();
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static bool IsSamePath(string left, string right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) &&
            string.Equals(NormalizePathKey(left), NormalizePathKey(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcludedExecutablePath(string exePath)
    {
        string fileName = Path.GetFileName(exePath);
        if (ExcludedExecutableNames.Contains(fileName))
        {
            return true;
        }

        string directoryPath = Path.GetDirectoryName(exePath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        string[] segments = directoryPath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return segments.Any(segment => string.Equals(segment, "lib", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveGameRootDirectory(string gamesDirectory, string exePath)
    {
        string parentFolderPath = Path.GetDirectoryName(exePath);
        if (string.IsNullOrWhiteSpace(parentFolderPath))
        {
            return gamesDirectory;
        }

        string relativeDirectory = Path.GetRelativePath(gamesDirectory, parentFolderPath);
        if (string.IsNullOrWhiteSpace(relativeDirectory) ||
            string.Equals(relativeDirectory, ".", StringComparison.Ordinal) ||
            relativeDirectory.StartsWith("..", StringComparison.Ordinal))
        {
            return parentFolderPath;
        }

        string topLevelFolder = relativeDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return string.IsNullOrWhiteSpace(topLevelFolder)
            ? parentFolderPath
            : Path.Combine(gamesDirectory, topLevelFolder);
    }

    private static List<string> BuildTags(string gameName)
    {
        var tags = new List<string>();
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return tags;
        }

        if (gameName.IndexOf("NPC", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            tags.Add("NPC");
        }

        if (gameName.IndexOf("RPG", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            tags.Add("RPG");
        }

        if (gameName.IndexOf("Action", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            tags.Add("Action");
        }

        return tags;
    }

}
