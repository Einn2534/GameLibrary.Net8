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
        int remainingRemoteFetches = iconPipelineService.RemoteFetchLimitPerRun;
        string[] exeFiles = Directory.Exists(gamesDirectory)
            ? Directory.GetFiles(gamesDirectory, "*.exe", SearchOption.AllDirectories)
                .Where(path => !IsExcludedExecutablePath(path))
                .ToArray()
            : Array.Empty<string>();

        foreach (string exePath in exeFiles)
        {
            string gameRootDirectory = ResolveGameRootDirectory(gamesDirectory, exePath);
            string gameName = new DirectoryInfo(gameRootDirectory).Name;
            GameIconResolveResult iconResult = ResolveIcon(gameRootDirectory, gameName, remainingRemoteFetches > 0);
            if (iconResult.AttemptedRemoteFetch)
            {
                remainingRemoteFetches--;
            }

            gameList.Add(new GameInfo
            {
                Name = gameName,
                Icon = iconResult.IconPath,
                Executable = exePath,
                InstallDirectory = gameRootDirectory,
                Description = defaultDescription,
                Tags = BuildTags(gameName)
            });
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
        string localIconPath = ResolveLocalIconPath(gameRootDirectory);
        if (!string.IsNullOrWhiteSpace(localIconPath))
        {
            return new GameIconResolveResult(localIconPath, attemptedRemoteFetch: false);
        }

        return iconPipelineService.ResolveIcon(gameName, gameRootDirectory, allowRemoteFetch);
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

    private static string ResolveLocalIconPath(string parentFolderPath)
    {
        string[] candidatePaths =
        {
            Path.Combine(parentFolderPath, "icon.png"),
            Path.Combine(parentFolderPath, "icon.ico"),
            Path.Combine(parentFolderPath, "icon", "icon.png"),
            Path.Combine(parentFolderPath, "www", "icon", "icon.png"),
            Path.Combine(parentFolderPath, "www", "icon", "icon.ico")
        };

        string directMatch = candidatePaths.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(directMatch))
        {
            return directMatch;
        }

        if (!Directory.Exists(parentFolderPath))
        {
            return string.Empty;
        }

        string[] allowedFileNames =
        {
            "icon.png",
            "icon.ico"
        };

        string recursiveMatch = Directory
            .EnumerateFiles(parentFolderPath, "*", SearchOption.AllDirectories)
            .Where(path => allowedFileNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path.Count(character => character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return recursiveMatch ?? string.Empty;
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
