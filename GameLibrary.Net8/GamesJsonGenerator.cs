using Newtonsoft.Json;

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
            .Where(game => !string.IsNullOrWhiteSpace(game.InstallDirectory))
            .GroupBy(game => NormalizePathKey(game.InstallDirectory), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenInstallDirectories = new(StringComparer.OrdinalIgnoreCase);
        int remainingRemoteFetches = iconPipelineService.RemoteFetchLimitPerRun;

        foreach (string gameRootDirectory in EnumerateCandidateGameDirectories(gamesDirectory))
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
                DefaultTags = defaultTags,
                Tags = defaultTags.ToList()
            });
        }

        foreach (GameInfo existingGame in existingGames)
        {
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

        bool shouldResolveMissingIcon = string.IsNullOrWhiteSpace(existingGame.Icon);
        GameIconResolveResult iconResult = shouldResolveMissingIcon
            ? ResolveIcon(existingGame.InstallDirectory, gameName, allowRemoteFetch)
            : new GameIconResolveResult(existingGame.Icon, attemptedRemoteFetch: false);
        attemptedRemoteFetch = iconResult.AttemptedRemoteFetch;
        List<string> defaultTags = NormalizeTags(existingGame.DefaultTags.Count > 0 ? existingGame.DefaultTags : existingGame.Tags);

        return new GameInfo
        {
            Name = gameName,
            Icon = string.IsNullOrWhiteSpace(iconResult.IconPath) ? existingGame.Icon : iconResult.IconPath,
            Executable = existingGame.Executable,
            InstallDirectory = existingGame.InstallDirectory,
            Description = string.IsNullOrWhiteSpace(existingGame.Description) ? defaultDescription : existingGame.Description,
            SourcePageUrl = existingGame.SourcePageUrl,
            DefaultTags = defaultTags,
            Tags = defaultTags.ToList()
        };
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

    private static IEnumerable<string> EnumerateCandidateGameDirectories(string gamesDirectory)
    {
        if (string.IsNullOrWhiteSpace(gamesDirectory) || !Directory.Exists(gamesDirectory))
        {
            yield break;
        }

        foreach (string directory in Directory.EnumerateDirectories(gamesDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
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
